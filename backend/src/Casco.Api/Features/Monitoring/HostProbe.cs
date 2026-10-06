using System.Diagnostics;
using System.Globalization;

namespace Casco.Api.Features.Monitoring;

public record MemoryUse(double Percent, double UsedGb, double TotalGb);
public record DiskUse(double Percent, double UsedGb, double TotalGb);

/// <summary>
/// Whole-machine CPU, memory and disk. Inside Docker, /proc/stat and /proc/meminfo describe the host (no container limits
/// are set), so the API reports on server 1 itself. Other systems (development) fall back to process CPU and GC memory load.
/// </summary>
public static class HostProbe
{
    private const double Gb = 1024d * 1024 * 1024;
    private static readonly Lock Gate = new();
    private static (ulong Idle, ulong Total)? _lastStat;
    private static (DateTime At, TimeSpan Cpu)? _lastProcess;

    /// <summary>CPU use since the previous call (null on the first call).</summary>
    public static double? CpuPercent()
    {
        lock (Gate)
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/stat"))
            {
                var fields = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var values = fields.Skip(1).Take(8).Select(v => ulong.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                var idle = values[3] + values[4];
                var total = values.Aggregate(0UL, (a, b) => a + b);
                var prev = _lastStat;
                _lastStat = (idle, total);
                if (prev is not { } p || total <= p.Total) return null;
                return Math.Round((1 - (double)(idle - p.Idle) / (total - p.Total)) * 100, 1);
            }

            var now = DateTime.UtcNow;
            var cpu = Process.GetCurrentProcess().TotalProcessorTime;
            var last = _lastProcess;
            _lastProcess = (now, cpu);
            if (last is not { } l) return null;
            var wall = (now - l.At).TotalMilliseconds * Environment.ProcessorCount;
            return wall <= 0 ? null : Math.Round(Math.Clamp((cpu - l.Cpu).TotalMilliseconds / wall * 100, 0, 100), 1);
        }
    }

    public static MemoryUse? Memory()
    {
        if (OperatingSystem.IsLinux() && File.Exists("/proc/meminfo"))
        {
            long total = 0, available = -1;
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal)) total = Kb(line);
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal)) available = Kb(line);
                if (total > 0 && available >= 0) break;
            }
            if (total > 0 && available >= 0)
            {
                var used = total - available;
                return new MemoryUse(Math.Round(used * 100d / total, 1), used * 1024d / Gb, total * 1024d / Gb);
            }
        }
        var info = GC.GetGCMemoryInfo();
        if (info.TotalAvailableMemoryBytes <= 0 || info.MemoryLoadBytes <= 0) return null;
        return new MemoryUse(Math.Round(info.MemoryLoadBytes * 100d / info.TotalAvailableMemoryBytes, 1),
            info.MemoryLoadBytes / Gb, info.TotalAvailableMemoryBytes / Gb);
    }

    public static DiskUse? Disk(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var drive = new DriveInfo(OperatingSystem.IsWindows() ? Path.GetPathRoot(full) ?? full : full);
            if (drive.TotalSize <= 0) return null;
            var used = drive.TotalSize - drive.AvailableFreeSpace;
            return new DiskUse(Math.Round(used * 100d / drive.TotalSize, 1), used / Gb, drive.TotalSize / Gb);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static double? Load1()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/loadavg")) return null;
        var first = File.ReadAllText("/proc/loadavg").Split(' ')[0];
        return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static long Kb(string line) =>
        long.TryParse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
