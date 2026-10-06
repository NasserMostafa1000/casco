using System.Diagnostics;
using Casco.Api.Domain;
using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Casco.Api.Features.Admin;

/// <summary>
/// Live health of the API server for load tests and day-to-day watching: memory, CPU, agent queue,
/// generation and AI latency, failures, database connections, disk and credit holds.
/// </summary>
public static class SystemMetrics
{
    private static readonly Lock Gate = new();
    private static (DateTime At, TimeSpan Cpu) _lastSample = (Process.GetCurrentProcess().StartTime.ToUniversalTime(), TimeSpan.Zero);

    /// <summary>CPU use of this process since the previous call, as a share of all cores (0-100).</summary>
    private static double CpuPercent(Process process)
    {
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            var cpu = process.TotalProcessorTime;
            var wall = (now - _lastSample.At).TotalMilliseconds;
            var used = (cpu - _lastSample.Cpu).TotalMilliseconds;
            _lastSample = (now, cpu);
            return wall <= 0 ? 0 : Math.Round(used / (wall * Environment.ProcessorCount) * 100, 1);
        }
    }

    public static async Task<object> SnapshotAsync(AppDbContext db, AgentOptions agent, AppOptions app, int windowMinutes, CancellationToken ct)
    {
        var process = Process.GetCurrentProcess();
        var since = DateTime.UtcNow.AddMinutes(-Math.Clamp(windowMinutes, 1, 24 * 60));

        var finished = await db.AgentTasks.AsNoTracking()
            .Where(t => t.CompletedAt >= since && t.StartedAt != null)
            .Select(t => new { t.Status, t.StartedAt, t.CompletedAt, t.Parts, t.CreatedAt })
            .ToListAsync(ct);
        var durations = finished.Where(t => t.Status == TaskStatuses.Succeeded)
            .Select(t => (decimal)(t.CompletedAt!.Value - t.StartedAt!.Value).TotalSeconds).OrderBy(d => d).ToList();
        var waits = finished.Select(t => (decimal)(t.StartedAt!.Value - t.CreatedAt).TotalSeconds).OrderBy(d => d).ToList();

        var calls = await db.AiUsages.AsNoTracking()
            .Where(u => u.CreatedAt >= since && !u.ResponseCacheHit)
            .Select(u => new { u.Success, u.DurationMs, u.Error })
            .ToListAsync(ct);
        var latency = calls.Where(c => c.Success).Select(c => (decimal)c.DurationMs).OrderBy(d => d).ToList();

        int? dbConnections = null;
        if (db.Database.IsNpgsql())
        {
            var conn = db.Database.GetDbConnection();
            await db.Database.OpenConnectionAsync(ct);
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database()";
                dbConnections = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
            }
            finally { await db.Database.CloseConnectionAsync(); }
        }

        var dataPath = Path.GetFullPath(app.DataPath);
        var drive = new DriveInfo(Path.GetPathRoot(dataPath) ?? dataPath);

        return new
        {
            at = DateTime.UtcNow,
            windowMinutes,
            process = new
            {
                cpuPercent = CpuPercent(process),
                cores = Environment.ProcessorCount,
                workingSetMb = process.WorkingSet64 / 1_048_576,
                gcHeapMb = GC.GetTotalMemory(false) / 1_048_576,
                threads = process.Threads.Count,
                uptimeMinutes = (int)(DateTime.Now - process.StartTime).TotalMinutes
            },
            agent = new
            {
                workers = agent.Workers,
                running = await db.AgentTasks.CountAsync(t => t.Status == TaskStatuses.Running, ct),
                queued = await db.AgentTasks.CountAsync(t => t.Status == TaskStatuses.Queued, ct),
                succeeded = finished.Count(t => t.Status == TaskStatuses.Succeeded),
                failed = finished.Count(t => t.Status == TaskStatuses.Failed),
                avgGenerationSeconds = durations.Count > 0 ? Math.Round(durations.Average(), 1) : 0,
                p90GenerationSeconds = Math.Round(UnitEconomics.Percentile(durations, 0.9), 1),
                avgQueueWaitSeconds = waits.Count > 0 ? Math.Round(waits.Average(), 1) : 0,
                maxParts = finished.Count > 0 ? finished.Max(t => t.Parts) : 0
            },
            ai = new
            {
                calls = calls.Count,
                failures = calls.Count(c => !c.Success),
                rateLimited = calls.Count(c => c.Error != null && c.Error.StartsWith("HTTP 429")),
                avgLatencyMs = latency.Count > 0 ? (int)latency.Average() : 0,
                p90LatencyMs = (int)UnitEconomics.Percentile(latency, 0.9)
            },
            database = new { connections = dbConnections },
            disk = new
            {
                freeGb = Math.Round(drive.AvailableFreeSpace / 1e9, 1),
                totalGb = Math.Round(drive.TotalSize / 1e9, 1)
            },
            credits = new
            {
                activeHolds = await db.CreditReservations.CountAsync(r => r.Status == ReservationStatuses.Reserved, ct),
                heldCredits = await db.CreditReservations.Where(r => r.Status == ReservationStatuses.Reserved).SumAsync(r => (int?)r.Amount, ct) ?? 0
            }
        };
    }

    public static void MapSystemMetrics(this RouteGroupBuilder admin) =>
        admin.MapGet("/system", async (AppDbContext db, IOptions<AgentOptions> agent, IOptions<AppOptions> app, int? minutes, CancellationToken ct) =>
            Results.Ok(await SnapshotAsync(db, agent.Value, app.Value, minutes ?? 60, ct)));
}
