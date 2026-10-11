using System.Text.RegularExpressions;

namespace ProPS1Studio.Core.Logging;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

public sealed record LogEntry(DateTime Timestamp, LogLevel Level, string Source, string Message);

public interface ILogSink
{
    void Write(LogLevel level, string source, string message);
}

/// <summary>
/// Redacts secret-looking material from log messages so diagnostics stay shareable.
/// </summary>
public static class LogSanitizer
{
    private static readonly Regex[] Redactions =
    {
        new(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----", RegexOptions.Compiled),
        new(@"\b(AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.Compiled),
        new(@"\bgh[pousr]_[A-Za-z0-9]{20,}\b", RegexOptions.Compiled),
        new(@"\bxox[baprs]-[0-9A-Za-z-]{10,}\b", RegexOptions.Compiled),
        new(@"\bAIza[0-9A-Za-z_\-]{35}\b", RegexOptions.Compiled),
        new(@"(?i)\b(password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key)\s*[:=]\s*('""|""|')?[^\s'"";,)]{4,}", RegexOptions.Compiled),
        new(@"(?i)bearer\s+[A-Za-z0-9\-._~+/]{16,}", RegexOptions.Compiled),
        new(@"(?i)(AccountKey|SharedAccessSignature|sig)=([A-Za-z0-9+/=]{16,})", RegexOptions.Compiled),
    };

    public static string Sanitize(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message;
        }

        var result = message;
        foreach (var regex in Redactions)
        {
            result = regex.Replace(result, static match => RedactMatch(match));
        }

        return result;
    }

    private static string RedactMatch(Match match)
    {
        var value = match.Value;
        if (value.Length <= 8)
        {
            return "[REDACTED]";
        }

        // Keep a short, non-actionable prefix/suffix for correlation.
        return value[..4] + "…" + value[^4..] + " [REDACTED]";
    }
}

public interface IAppLogger : ILogSink
{
    event EventHandler<LogEntry>? EntryLogged;
    IReadOnlyList<LogEntry> Snapshot { get; }
    string LogFilePath { get; }
    void Flush();
}

/// <summary>Thread-safe logger writing to a daily file plus an in-memory ring buffer and an event.</summary>
public sealed class AppLogger : IAppLogger, IDisposable
{
    private readonly object _sync = new();
    private readonly Queue<LogEntry> _ring = new();
    private readonly int _ringCapacity;
    private readonly StreamWriter? _writer;
    private bool _disposed;

    public event EventHandler<LogEntry>? EntryLogged;

    public AppLogger(string logDirectory, int ringCapacity = 2000)
    {
        _ringCapacity = Math.Max(50, ringCapacity);
        if (!string.IsNullOrWhiteSpace(logDirectory))
        {
            try
            {
                Directory.CreateDirectory(logDirectory);
                LogFilePath = Path.Combine(logDirectory, $"prop-s1-{DateTime.Now:yyyyMMdd}.log");
                _writer = new StreamWriter(
                    new FileStream(LogFilePath, FileMode.Append, FileAccess.Write, FileShare.Read),
                    System.Text.Encoding.UTF8)
                {
                    AutoFlush = true,
                };
            }
            catch
            {
                // Logging must never take the app down; fall back to memory-only.
                _writer = null;
            }
        }
    }

    public string LogFilePath { get; } = string.Empty;

    public IReadOnlyList<LogEntry> Snapshot
    {
        get
        {
            lock (_sync)
            {
                return _ring.ToArray();
            }
        }
    }

    public void Write(LogLevel level, string source, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, source, LogSanitizer.Sanitize(message));
        lock (_sync)
        {
            _ring.Enqueue(entry);
            while (_ring.Count > _ringCapacity)
            {
                _ring.Dequeue();
            }

            try
            {
                _writer?.WriteLine($"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{level,-5}] [{source}] {entry.Message}");
            }
            catch
            {
                // Ignore IO failures in the log file writer.
            }
        }

        EntryLogged?.Invoke(this, entry);
    }

    public void Flush()
    {
        lock (_sync)
        {
            try
            {
                _writer?.Flush();
            }
            catch
            {
                // ignored
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _writer?.Dispose();
        }
    }
}

/// <summary>Logger that discards everything; used in tests.</summary>
public sealed class NullLogger : IAppLogger
{
    public event EventHandler<LogEntry>? EntryLogged { add { } remove { } }
    public IReadOnlyList<LogEntry> Snapshot => Array.Empty<LogEntry>();
    public string LogFilePath => string.Empty;
    public void Write(LogLevel level, string source, string message) { }
    public void Flush() { }
}
