using System.Collections.Concurrent;
using System.Text;

namespace Casco.Api.Infrastructure.Logging;

public class FileLogOptions
{
    /// <summary>Folder for daily log files; relative paths are under App:DataPath.</summary>
    public string Directory { get; set; } = "logs";
    public LogLevel MinLevel { get; set; } = LogLevel.Information;
    public int RetainDays { get; set; } = 30;
}

public record LogEntry(DateTime At, LogLevel Level, string Category, string Message, string? Exception);

/// <summary>Recent errors kept in memory for the admin page and the error-rate alert.</summary>
public class ErrorFeed
{
    public const int Keep = 200;
    private readonly Lock _gate = new();
    private readonly LinkedList<LogEntry> _recent = new();
    private readonly Queue<DateTime> _times = new();

    public void Add(LogEntry entry)
    {
        lock (_gate)
        {
            _recent.AddFirst(entry);
            while (_recent.Count > Keep) _recent.RemoveLast();
            _times.Enqueue(entry.At);
            var hourAgo = entry.At.AddHours(-1);
            while (_times.Count > 0 && _times.Peek() < hourAgo) _times.Dequeue();
        }
    }

    public IReadOnlyList<LogEntry> Recent(int count)
    {
        lock (_gate) return _recent.Take(count).ToList();
    }

    /// <summary>Errors logged since <paramref name="since"/> (at most one hour back).</summary>
    public int CountSince(DateTime since)
    {
        lock (_gate) return _times.Count(t => t >= since);
    }
}

/// <summary>
/// Writes log lines to {Directory}/casco-yyyy-MM-dd.log (UTC days) on a background thread and deletes files older
/// than RetainDays. Errors also go to <see cref="ErrorFeed"/>.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly FileLogOptions _options;
    private readonly ErrorFeed _feed;
    private readonly BlockingCollection<(DateTime At, string Line)> _queue = new(20_000);
    private readonly Thread _thread;
    private StreamWriter? _writer;
    private string _day = "";

    public string DirectoryPath { get; }

    public FileLoggerProvider(string directory, FileLogOptions options, ErrorFeed feed)
    {
        _options = options;
        _feed = feed;
        DirectoryPath = Path.GetFullPath(directory);
        System.IO.Directory.CreateDirectory(DirectoryPath);
        _thread = new Thread(Run) { IsBackground = true, Name = "file-logger" };
        _thread.Start();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= _options.MinLevel;

    internal void Write(LogEntry entry)
    {
        if (entry.Level >= LogLevel.Error) _feed.Add(entry);
        if (!_queue.IsAddingCompleted) _queue.TryAdd((entry.At, Format(entry)));
    }

    public static string Format(LogEntry e)
    {
        var level = e.Level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            _ => "CRT"
        };
        var sb = new StringBuilder().Append(e.At.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("Z [").Append(level).Append("] ")
            .Append(e.Category).Append(": ").Append(e.Message);
        if (e.Exception is not null) sb.Append(Environment.NewLine).Append(e.Exception);
        return sb.ToString();
    }

    private void Run()
    {
        foreach (var (at, line) in _queue.GetConsumingEnumerable())
        {
            try
            {
                var day = at.ToString("yyyy-MM-dd");
                if (day != _day || _writer is null)
                {
                    _writer?.Dispose();
                    _writer = new StreamWriter(new FileStream(Path.Combine(DirectoryPath, $"casco-{day}.log"),
                        FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false));
                    _day = day;
                    DeleteOldFiles();
                }
                _writer.WriteLine(line);
                if (_queue.Count == 0) _writer.Flush();
            }
            catch (IOException) { _writer = null; }
            catch (UnauthorizedAccessException) { _writer = null; }
        }
        _writer?.Flush();
    }

    private void DeleteOldFiles()
    {
        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, _options.RetainDays));
        foreach (var file in System.IO.Directory.EnumerateFiles(DirectoryPath, "casco-*.log"))
        {
            try { if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(3));
        _writer?.Dispose();
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            provider.Write(new LogEntry(DateTime.UtcNow, logLevel, category, formatter(state, exception), exception?.ToString()));
        }
    }
}
