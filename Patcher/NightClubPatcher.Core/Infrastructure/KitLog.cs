namespace NightClubPatcher.Core.Infrastructure;

public enum LogLevel { Debug, Info, Warn, Error, Success, Step }

/// <summary>
/// Thread-safe logger writing to Logs/patcher-YYYYMMDD.log and to any attached sink (console, GUI).
/// </summary>
public sealed class KitLog
{
    private readonly object _lock = new();
    private readonly string? _file;

    public event Action<LogLevel, string>? Message;

    /// <summary>
    /// Live progress of a long copy: (key, text, fraction 0..1). UIs show it as one line that updates
    /// in place (not written to the log file); <see cref="EndProgress"/> removes it.
    /// </summary>
    public event Action<string, string?, double?>? Progress;

    public void ReportProgress(string key, string text, double? fraction) => Progress?.Invoke(key, text, fraction);
    public void EndProgress(string key) => Progress?.Invoke(key, null, null);

    /// <summary>"[██████░░░░░░] 57/173 files · 12.3/40 MB · 31%".</summary>
    public static string ProgressBar(double fraction, int width = 20)
    {
        var f = Math.Clamp(fraction, 0, 1);
        var full = (int)Math.Round(f * width);
        return "[" + new string('█', full) + new string('░', width - full) + $"] {f * 100,3:0}%";
    }

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

    /// <summary>Start of a workflow step; UIs show these as a progress list.</summary>
    public void Step(string msg) => Write(LogLevel.Step, msg);

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
