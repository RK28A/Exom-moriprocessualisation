namespace NightClubPatcher.Core.Infrastructure;

public enum LogLevel { Debug, Info, Warn, Error, Success }

/// <summary>
/// Thread-safe logger writing to Logs/patcher-YYYYMMDD.log and to any attached sink (console, GUI).
/// </summary>
public sealed class KitLog
{
    private readonly object _lock = new();
    private readonly string? _file;

    public event Action<LogLevel, string>? Message;

    public KitLog(string? logDirectory)
    {
        if (logDirectory == null) return;
        try
        {
            Directory.CreateDirectory(logDirectory);
            _file = Path.Combine(logDirectory, $"patcher-{DateTime.Now:yyyyMMdd}.log");
        }
        catch
        {
            _file = null;
        }
    }

    public string? FilePath => _file;

    public void Debug(string msg) => Write(LogLevel.Debug, msg);
    public void Info(string msg) => Write(LogLevel.Info, msg);
    public void Warn(string msg) => Write(LogLevel.Warn, msg);
    public void Error(string msg) => Write(LogLevel.Error, msg);
    public void Success(string msg) => Write(LogLevel.Success, msg);

    public void Step(string msg) => Write(LogLevel.Info, "==> " + msg);

    public void Write(LogLevel level, string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] [{level.ToString().ToUpperInvariant()}] {msg}";
        lock (_lock)
        {
            if (_file != null)
            {
                try { File.AppendAllText(_file, line + Environment.NewLine); } catch { /* never fail on logging */ }
            }
        }
        Message?.Invoke(level, msg);
    }
}
