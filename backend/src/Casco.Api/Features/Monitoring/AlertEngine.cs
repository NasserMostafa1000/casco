namespace Casco.Api.Features.Monitoring;

public enum AlertLevel { Ok = 0, Warning = 1, Critical = 2 }

public static class ServerIds
{
    public const string App = "server1";
    public const string Database = "server2";
}

/// <param name="Sustain">Samples in a row needed to raise or clear the level (1 = immediately).</param>
public record MetricReading(string Server, string Key, double? Value, string Display, AlertLevel Level, int Sustain = 1)
{
    public string Id => $"{Server}:{Key}";
}

public record AlertChange(MetricReading Reading, AlertLevel From, AlertLevel To, bool Repeat)
{
    public bool Recovered => To == AlertLevel.Ok;
}

public class AlertState
{
    public AlertLevel Level { get; set; }
    /// <summary>Level of the last e-mail about this metric.</summary>
    public AlertLevel Notified { get; set; }
    public DateTime? NotifiedAt { get; set; }
    public DateTime Since { get; set; }
    public MetricReading? Last { get; set; }
    internal Queue<AlertLevel> Recent { get; } = new();
}

/// <summary>
/// Turns readings into e-mail-worthy changes. A level is raised only when the last N samples all reach it and cleared only
/// when the last N samples are all below it, so short spikes and flapping don't send mail. Unresolved alerts are repeated
/// every <c>repeatAfter</c>.
/// </summary>
public class AlertEngine
{
    private readonly Dictionary<string, AlertState> _states = new();

    public IReadOnlyList<AlertChange> Evaluate(IEnumerable<MetricReading> readings, DateTime now, TimeSpan repeatAfter)
    {
        var changes = new List<AlertChange>();
        foreach (var r in readings)
        {
            if (!_states.TryGetValue(r.Id, out var s)) _states[r.Id] = s = new AlertState { Since = now };
            s.Last = r;
            var sustain = Math.Max(1, r.Sustain);
            s.Recent.Enqueue(r.Level);
            while (s.Recent.Count > sustain) s.Recent.Dequeue();

            if (s.Recent.Count >= sustain)
            {
                var lowest = s.Recent.Min();
                var highest = s.Recent.Max();
                var next = lowest > s.Level ? lowest : highest < s.Level ? highest : s.Level;
                if (next != s.Level)
                {
                    s.Level = next;
                    s.Since = now;
                }
            }

            if (s.Level > s.Notified)
            {
                changes.Add(new AlertChange(r, s.Notified, s.Level, false));
                s.Notified = s.Level;
                s.NotifiedAt = now;
            }
            else if (s.Level == AlertLevel.Ok && s.Notified > AlertLevel.Ok)
            {
                changes.Add(new AlertChange(r, s.Notified, AlertLevel.Ok, false));
                s.Notified = AlertLevel.Ok;
                s.NotifiedAt = now;
            }
            else if (s.Level > AlertLevel.Ok && s.Level < s.Notified)
            {
                s.Notified = s.Level;
            }
            else if (s.Level > AlertLevel.Ok && s.NotifiedAt is { } at && now - at >= repeatAfter)
            {
                changes.Add(new AlertChange(r, s.Notified, s.Level, true));
                s.NotifiedAt = now;
            }
        }
        return changes;
    }

    public IReadOnlyList<(string Id, AlertState State)> Active =>
        _states.Where(kv => kv.Value.Level > AlertLevel.Ok).Select(kv => (kv.Key, kv.Value)).ToList();

    public static AlertLevel LevelFor(double value, double warning, double critical) =>
        value >= critical ? AlertLevel.Critical : value >= warning ? AlertLevel.Warning : AlertLevel.Ok;
}
