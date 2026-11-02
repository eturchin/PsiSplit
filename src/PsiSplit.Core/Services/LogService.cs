namespace PsiSplit.Core.Services;

public enum LogLevel
{
    Info,
    Warn,
    Error
}

public sealed record LogEntry(DateTime Timestamp, LogLevel Level, string Message);

/// <summary>
/// Thread-safe logger. Writes to the UI event and to a daily file.
/// </summary>
public sealed class LogService
{
    private readonly object _fileLock = new();
    private readonly string? _directory;
    private readonly Action<LogEntry>? _uiSink;

    public LogService(string? directory = null, Action<LogEntry>? uiSink = null)
    {
        _directory = directory;
        _uiSink = uiSink;

        if (_directory is not null)
        {
            try
            {
                Directory.CreateDirectory(_directory);
            }
            catch
            {
                _directory = null;
            }
        }
    }

    public void Info(string message) => Write(LogLevel.Info, message);
    public void Warn(string message) => Write(LogLevel.Warn, message);
    public void Error(string message, Exception? exception = null)
        => Write(LogLevel.Error, exception is null ? message : $"{message}: {exception.Message}");

    private void Write(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);

        try
        {
            _uiSink?.Invoke(entry);
        }
        catch
        {
            // A broken UI callback must never break the proxy.
        }

        if (_directory is null)
        {
            return;
        }

        try
        {
            lock (_fileLock)
            {
                var file = Path.Combine(_directory, $"psisplit-{DateTime.Now:yyyy-MM-dd}.log");
                File.AppendAllText(file, $"{entry.Timestamp:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never throw.
        }
    }
}
