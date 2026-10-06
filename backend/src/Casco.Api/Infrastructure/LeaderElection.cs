using Npgsql;

namespace Casco.Api.Infrastructure;

/// <summary>
/// One API runs on each server, but only the active server runs background jobs
/// (agent, mail, billing, monitoring). The choice is the <c>cluster_state</c> row,
/// which the failover watcher on server 2 updates. The advisory lock stops both
/// sides from working at the same time during the hand-off.
/// An empty <c>App:ServerId</c> (local development) is always the leader.
/// </summary>
public sealed class LeaderElection : BackgroundService
{
    public const string Server1 = "server1";
    public const string Server2 = "server2";
    private const long LockKey = 87421301;

    private readonly string? _serverId;
    private readonly string? _connectionString;
    private readonly bool _clustered;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<LeaderElection> _logger;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _leader;

    public LeaderElection(IConfiguration config, IHostApplicationLifetime lifetime, ILogger<LeaderElection> logger)
    {
        _lifetime = lifetime;
        _logger = logger;
        _serverId = config["App:ServerId"];
        _connectionString = config.GetConnectionString("Default");
        _clustered = !string.IsNullOrWhiteSpace(_serverId)
            && string.Equals(config["Database:Provider"], "Postgres", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(_connectionString);
        if (!_clustered)
        {
            _leader = true;
            _ready.TrySetResult();
        }
    }

    public bool IsLeader => _leader;

    public async Task WaitUntilLeaderAsync(CancellationToken ct)
    {
        if (_leader) return;
        await _ready.Task.WaitAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_clustered) return;

        var cs = _connectionString + ";Pooling=false;Application Name=casco-leader";
        NpgsqlConnection? conn = null;
        var holding = false;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (conn is null || conn.State != System.Data.ConnectionState.Open)
                    {
                        holding = false;
                        _leader = false;
                        conn?.Dispose();
                        conn = new NpgsqlConnection(cs);
                        await conn.OpenAsync(stoppingToken);
                        await EnsureTableAsync(conn, stoppingToken);
                    }

                    var active = await ReadActiveAsync(conn, stoppingToken);
                    var should = string.Equals(active, _serverId, StringComparison.Ordinal);
                    if (should && !holding)
                    {
                        holding = await TryLockAsync(conn, stoppingToken);
                        if (holding)
                        {
                            _leader = true;
                            _ready.TrySetResult();
                            _logger.LogInformation("This server ({ServerId}) is active and will run background jobs", _serverId);
                        }
                    }
                    else if (!should && holding)
                    {
                        await UnlockAsync(conn, stoppingToken);
                        holding = false;
                        _leader = false;
                        _logger.LogWarning("This server ({ServerId}) is no longer active; restarting so background jobs stop", _serverId);
                        _lifetime.StopApplication();
                        return;
                    }
                    else if (should && holding)
                    {
                        await using var ping = new NpgsqlCommand("SELECT 1", conn);
                        await ping.ExecuteScalarAsync(stoppingToken);
                    }
                    else if (!should)
                    {
                        _logger.LogDebug("Waiting; active server is {Active}, this one is {ServerId}", active, _serverId);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    var lost = holding;
                    _logger.LogWarning("Leader check failed: {Error}", ex.Message);
                    holding = false;
                    _leader = false;
                    conn?.Dispose();
                    conn = null;
                    if (lost)
                    {
                        _logger.LogWarning("Lost the leader connection; restarting so the other server can take over");
                        _lifetime.StopApplication();
                        return;
                    }
                }

                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            if (holding && conn is { State: System.Data.ConnectionState.Open })
            {
                try { await UnlockAsync(conn, CancellationToken.None); } catch { /* shutting down */ }
            }
            conn?.Dispose();
        }
    }

    private static async Task EnsureTableAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS cluster_state (
                id integer PRIMARY KEY,
                active_server text NOT NULL
            );
            INSERT INTO cluster_state (id, active_server) VALUES (1, 'server1')
            ON CONFLICT (id) DO NOTHING;
            """, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string> ReadActiveAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT active_server FROM cluster_state WHERE id = 1", conn);
        var value = await cmd.ExecuteScalarAsync(ct) as string;
        return string.IsNullOrWhiteSpace(value) ? Server1 : value.Trim();
    }

    private static async Task<bool> TryLockAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", conn);
        cmd.Parameters.Add(new NpgsqlParameter("key", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = LockKey });
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    private static async Task UnlockAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", conn);
        cmd.Parameters.Add(new NpgsqlParameter("key", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = LockKey });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
