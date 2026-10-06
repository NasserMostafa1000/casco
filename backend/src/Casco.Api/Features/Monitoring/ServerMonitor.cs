using System.Globalization;
using Casco.Api.Infrastructure;
using Casco.Api.Infrastructure.Email;
using Casco.Api.Infrastructure.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Casco.Api.Features.Monitoring;

public class Limits
{
    public double Warning { get; set; }
    public double Critical { get; set; }
}

public class MonitorOptions
{
    /// <summary>Who gets server alerts (the platform owner).</summary>
    public string[] AlertEmails { get; set; } = [];
    /// <summary>"ar" or "en".</summary>
    public string AlertLanguage { get; set; } = "ar";
    /// <summary>Shared secret of the server 2 agent (deploy/server2/monitor.sh). Empty = server 2 is not monitored.</summary>
    public string AgentToken { get; set; } = "";
    public int IntervalSeconds { get; set; } = 60;
    public Limits Cpu { get; set; } = new() { Warning = 85, Critical = 95 };
    public Limits Memory { get; set; } = new() { Warning = 90, Critical = 95 };
    public Limits Disk { get; set; } = new() { Warning = 85, Critical = 95 };
    public Limits DbConnections { get; set; } = new() { Warning = 80, Critical = 95 };
    /// <summary>Errors logged in the last 10 minutes.</summary>
    public Limits Errors { get; set; } = new() { Warning = 20, Critical = 100 };
    /// <summary>Minutes in a row CPU and memory must stay high before an alert (and low before it clears).</summary>
    public int SustainedSamples { get; set; } = 3;
    /// <summary>Server 2 counts as down when it hasn't reported for this long.</summary>
    public int HeartbeatMinutes { get; set; } = 5;
    /// <summary>Newest database backup older than this is a warning (twice this: critical).</summary>
    public int BackupMaxHours { get; set; } = 26;
    /// <summary>An unresolved alert is e-mailed again after this many hours.</summary>
    public int RepeatHours { get; set; } = 6;
}

/// <summary>What deploy/server2/monitor.sh posts every minute.</summary>
public record AgentReport(double? Cpu, double? Memory, double? MemoryTotalGb, double? Disk, double? DiskUsedGb, double? DiskTotalGb,
    double? Load1, int? Cores, double? BackupAgeHours, double? UptimeHours, bool? PgReady);

/// <summary>Collects readings for both servers, keeps alert state and e-mails the owner when something becomes dangerous.</summary>
public class ServerMonitor(IOptions<MonitorOptions> options, IOptions<AppOptions> app, ErrorFeed errors, EmailQueue email,
    TimeProvider clock, ILogger<ServerMonitor> logger)
{
    private readonly Lock _gate = new();
    private readonly AlertEngine _engine = new();
    private readonly DateTime _startedAt = clock.GetUtcNow().UtcDateTime;
    private (DateTime At, AgentReport Report)? _server2;
    private IReadOnlyList<MetricReading> _latest = [];
    private DateTime? _lastCheck;

    public MonitorOptions Options => options.Value;
    public bool AcceptsAgent => !string.IsNullOrWhiteSpace(options.Value.AgentToken);

    public void Receive(AgentReport report)
    {
        lock (_gate) _server2 = (clock.GetUtcNow().UtcDateTime, report);
    }

    private static string Pct(double v) => v.ToString("0", CultureInfo.InvariantCulture) + "%";
    private static string Num(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    public async Task<IReadOnlyList<MetricReading>> CollectAsync(AppDbContext db, CancellationToken ct)
    {
        var o = options.Value;
        var now = clock.GetUtcNow().UtcDateTime;
        var sustain = Math.Max(1, o.SustainedSamples);
        var list = new List<MetricReading>();

        var load = HostProbe.Load1();
        if (HostProbe.CpuPercent() is { } cpu)
            list.Add(new(ServerIds.App, "cpu", cpu, Pct(cpu) + (load is { } l ? $" · load {Num(l)} / {Environment.ProcessorCount}" : ""),
                AlertEngine.LevelFor(cpu, o.Cpu.Warning, o.Cpu.Critical), sustain));
        if (HostProbe.Memory() is { } mem)
            list.Add(new(ServerIds.App, "memory", mem.Percent, $"{Pct(mem.Percent)} · {Num(mem.UsedGb)} / {Num(mem.TotalGb)} GB",
                AlertEngine.LevelFor(mem.Percent, o.Memory.Warning, o.Memory.Critical), sustain));
        if (HostProbe.Disk(app.Value.DataPath) is { } disk)
            list.Add(new(ServerIds.App, "disk", disk.Percent, $"{Pct(disk.Percent)} · {Num(disk.UsedGb)} / {Num(disk.TotalGb)} GB",
                AlertEngine.LevelFor(disk.Percent, o.Disk.Warning, o.Disk.Critical)));
        var errorCount = errors.CountSince(now.AddMinutes(-10));
        list.Add(new(ServerIds.App, "errors", errorCount, $"{errorCount} / 10 min", AlertEngine.LevelFor(errorCount, o.Errors.Warning, o.Errors.Critical)));

        // The database lives on server 2; the app checks it can reach it.
        var dbUp = false;
        (int Count, int Max)? connections = null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            if (db.Database.IsNpgsql())
            {
                var conn = db.Database.GetDbConnection();
                await db.Database.OpenConnectionAsync(cts.Token);
                try
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT count(*), current_setting('max_connections')::int FROM pg_stat_activity";
                    await using var reader = await cmd.ExecuteReaderAsync(cts.Token);
                    if (await reader.ReadAsync(cts.Token)) connections = (Convert.ToInt32(reader.GetValue(0)), reader.GetInt32(1));
                }
                finally { await db.Database.CloseConnectionAsync(); }
                dbUp = true;
            }
            else dbUp = await db.Database.CanConnectAsync(cts.Token);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Database check failed: {Error}", ex.Message);
        }
        list.Add(new(ServerIds.Database, "database", dbUp ? 1 : 0, dbUp ? "✓" : "✗", dbUp ? AlertLevel.Ok : AlertLevel.Critical, 2));
        if (connections is { Max: > 0 } c)
        {
            var pct = c.Count * 100d / c.Max;
            list.Add(new(ServerIds.Database, "dbConnections", pct, $"{Pct(pct)} · {c.Count} / {c.Max}",
                AlertEngine.LevelFor(pct, o.DbConnections.Warning, o.DbConnections.Critical), 2));
        }

        if (AcceptsAgent)
        {
            (DateTime At, AgentReport Report)? last;
            lock (_gate) last = _server2;
            var age = now - (last?.At ?? _startedAt);
            var late = age > TimeSpan.FromMinutes(o.HeartbeatMinutes);
            list.Add(new(ServerIds.Database, "heartbeat", age.TotalMinutes,
                last is null ? (late ? "✗" : "…") : $"{Num(age.TotalMinutes)} min",
                late ? AlertLevel.Critical : AlertLevel.Ok));

            if (last is { } s && age <= TimeSpan.FromMinutes(o.HeartbeatMinutes * 2))
            {
                var r = s.Report;
                if (r.Cpu is { } c2)
                    list.Add(new(ServerIds.Database, "cpu", c2, Pct(c2) + (r.Load1 is { } l2 ? $" · load {Num(l2)} / {r.Cores ?? 0}" : ""),
                        AlertEngine.LevelFor(c2, o.Cpu.Warning, o.Cpu.Critical), sustain));
                if (r.Memory is { } m2)
                    list.Add(new(ServerIds.Database, "memory", m2, $"{Pct(m2)}{(r.MemoryTotalGb is { } t ? $" · {Num(t * m2 / 100)} / {Num(t)} GB" : "")}",
                        AlertEngine.LevelFor(m2, o.Memory.Warning, o.Memory.Critical), sustain));
                if (r.Disk is { } d2)
                    list.Add(new(ServerIds.Database, "disk", d2, $"{Pct(d2)}{(r.DiskTotalGb is { } t ? $" · {Num(r.DiskUsedGb ?? 0)} / {Num(t)} GB" : "")}",
                        AlertEngine.LevelFor(d2, o.Disk.Warning, o.Disk.Critical)));
                if (r.PgReady is { } ready)
                    list.Add(new(ServerIds.Database, "pgReady", ready ? 1 : 0, ready ? "✓" : "✗", ready ? AlertLevel.Ok : AlertLevel.Critical, 2));
                if (r.BackupAgeHours is { } hours)
                    list.Add(new(ServerIds.Database, "backup", hours, $"{Num(hours)} h",
                        AlertEngine.LevelFor(hours, o.BackupMaxHours, o.BackupMaxHours * 2)));
                else if (r.UptimeHours is > 1)
                    list.Add(new(ServerIds.Database, "backup", null, "✗", AlertLevel.Warning));
            }
        }
        return list;
    }

    /// <summary>Collects, updates alert state and e-mails any change. Returns the changes.</summary>
    public async Task<IReadOnlyList<AlertChange>> CheckAsync(AppDbContext db, CancellationToken ct)
    {
        var readings = await CollectAsync(db, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        IReadOnlyList<AlertChange> changes;
        lock (_gate)
        {
            _latest = readings;
            _lastCheck = now;
            changes = _engine.Evaluate(readings, now, TimeSpan.FromHours(Math.Max(1, options.Value.RepeatHours)));
        }
        if (changes.Count > 0) Notify(changes, readings, now);
        return changes;
    }

    private void Notify(IReadOnlyList<AlertChange> changes, IReadOnlyList<MetricReading> readings, DateTime now)
    {
        var o = options.Value;
        var lang = o.AlertLanguage == "en" ? "en" : "ar";
        var summary = string.Join("; ", changes.Select(c => $"{c.Reading.Id} {c.From}->{c.To} ({c.Reading.Display})"));
        logger.LogWarning("Server alert: {Summary}", summary);
        if (o.AlertEmails.Length == 0) return;
        if (!email.Enabled)
        {
            logger.LogWarning("Server alert not e-mailed: SMTP is not configured");
            return;
        }
        var content = AlertEmail.Build(lang, changes, readings, o, now);
        var (html, text) = EmailLayout.Render(content, app.Value.FrontendBase);
        foreach (var to in o.AlertEmails.Where(e => !string.IsNullOrWhiteSpace(e)))
            email.Enqueue(new EmailMessage(to.Trim(), content.Subject, html, text, "server_alert"));
    }

    public object View(IEnumerable<LogEntry> recentErrors)
    {
        var o = options.Value;
        IReadOnlyList<MetricReading> latest;
        IReadOnlyList<(string Id, AlertState State)> active;
        (DateTime At, AgentReport Report)? s2;
        DateTime? lastCheck;
        lock (_gate)
        {
            latest = _latest;
            active = _engine.Active;
            s2 = _server2;
            lastCheck = _lastCheck;
        }
        object Reading(MetricReading r) => new
        {
            r.Key, label = AlertEmail.Label(r.Key, "ar"), r.Value, r.Display,
            level = r.Level.ToString().ToLowerInvariant(), threshold = AlertEmail.Threshold(r.Key, o, "ar")
        };
        return new
        {
            lastCheck,
            intervalSeconds = o.IntervalSeconds,
            servers = new[]
            {
                new { id = ServerIds.App, name = AlertEmail.ServerName(ServerIds.App, "ar"), monitored = true, lastReport = lastCheck,
                    readings = latest.Where(r => r.Server == ServerIds.App).Select(Reading).ToList() },
                new { id = ServerIds.Database, name = AlertEmail.ServerName(ServerIds.Database, "ar"), monitored = AcceptsAgent, lastReport = s2?.At,
                    readings = latest.Where(r => r.Server == ServerIds.Database).Select(Reading).ToList() }
            },
            alerts = active.Select(a => new
            {
                id = a.Id, server = AlertEmail.ServerName(a.State.Last?.Server ?? "", "ar"), label = AlertEmail.Label(a.State.Last?.Key ?? "", "ar"),
                level = a.State.Level.ToString().ToLowerInvariant(), since = a.State.Since, display = a.State.Last?.Display, notifiedAt = a.State.NotifiedAt
            }),
            email = new { enabled = email.Enabled, mode = email.Mode, sent = email.Sent, failed = email.Failed, lastError = email.LastError, lastSentAt = email.LastSentAt, recipients = o.AlertEmails },
            errors = recentErrors.Select(e => new { e.At, level = e.Level.ToString(), e.Category, e.Message, exception = Text.Truncate(e.Exception, 4000) }),
            errorsLast10Minutes = errors.CountSince(clock.GetUtcNow().UtcDateTime.AddMinutes(-10))
        };
    }
}

public class ServerMonitorService(ServerMonitor monitor, IServiceScopeFactory scopes, LeaderElection leader, ILogger<ServerMonitorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await leader.WaitUntilLeaderAsync(stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(15, monitor.Options.IntervalSeconds)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await monitor.CheckAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Server monitor check failed");
            }
        }
    }
}
