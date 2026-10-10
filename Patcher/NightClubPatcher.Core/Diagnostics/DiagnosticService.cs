using System.Text;
using System.Text.RegularExpressions;
using NightClubPatcher.Core.Adb;
using NightClubPatcher.Core.Infrastructure;

namespace NightClubPatcher.Core.Diagnostics;

public enum CheckStatus { Pass, Warn, Fail, Info }

public sealed record DiagnosticCheck(string Name, CheckStatus Status, string Detail);

public sealed class DiagnosticReport
{
    public List<DiagnosticCheck> Checks { get; } = new();
    public List<string> LoaderLog { get; } = new();
    public List<string> RelevantLogcat { get; } = new();
    public List<string> CrashLog { get; } = new();
    public List<string> Tools { get; } = new();

    public void Add(string name, CheckStatus s, string detail) => Checks.Add(new DiagnosticCheck(name, s, detail));

    public string Render()
    {
        var sb = new StringBuilder();
        sb.AppendLine("NightClubModKit diagnostic report");
        sb.AppendLine($"Generated: {DateTimeOffset.Now:O}");
        sb.AppendLine($"Target: {GameConstants.PackageName}");
        sb.AppendLine(new string('=', 70));
        foreach (var c in Checks)
            sb.AppendLine($"[{c.Status.ToString().ToUpperInvariant(),-4}] {c.Name,-28} {c.Detail}");
        Section(sb, "Tools", Tools);
        Section(sb, "Loader log (device)", LoaderLog);
        Section(sb, "Relevant logcat (NCSLoader / Unity / il2cpp / crashes)", RelevantLogcat);
        Section(sb, "Android crash buffer", CrashLog);
        return sb.ToString();
    }

    private static void Section(StringBuilder sb, string title, List<string> lines)
    {
        sb.AppendLine();
        sb.AppendLine("---- " + title + " " + new string('-', Math.Max(0, 60 - title.Length)));
        if (lines.Count == 0) sb.AppendLine("(empty)");
        foreach (var l in lines) sb.AppendLine(l);
    }
}

public static partial class LogcatFilter
{
    /// <summary>Lines worth showing in OPEN LOGCAT and the diagnostic report.</summary>
    [GeneratedRegex(@"NCSLoader|NCSMod|\bNCS\b|\bUnity\b|libil2cpp|il2cpp|AndroidRuntime|Fatal signal|SIGSEGV|SIGABRT|\bDEBUG\b|tombstone|com\.mandegames", RegexOptions.IgnoreCase)]
    public static partial Regex Relevant();

    public static readonly string[] Tags = { "NCSLoader", "Unity", "libil2cpp", "AndroidRuntime", "DEBUG", "libc" };
}

public sealed class LogcatService
{
    private readonly KitLog _log;

    public LogcatService(KitLog log) => _log = log;

    public List<string> Dump(AdbClient adb, int lines = 3000)
    {
        var r = adb.RunTimed(TimeSpan.FromSeconds(30), "logcat", "-d", "-v", "threadtime", "-t", lines.ToString());
        return r.StdOut.Split('\n').Select(l => l.TrimEnd()).Where(l => LogcatFilter.Relevant().IsMatch(l)).ToList();
    }

    public List<string> CrashBuffer(AdbClient adb)
    {
        var r = adb.RunTimed(TimeSpan.FromSeconds(30), "logcat", "-d", "-b", "crash", "-v", "threadtime");
        return r.StdOut.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).TakeLast(400).ToList();
    }

    public List<string> LoaderLog(AdbClient adb)
    {
        foreach (var path in new[] { GameConstants.DeviceLoaderLog, GameConstants.DeviceFallbackModsRoot + "/Logs/latest.log" })
        {
            var r = adb.Shell($"cat \"{path}\" 2>/dev/null");
            if (r.Ok && r.StdOut.Trim().Length > 0)
                return new[] { "# " + path }.Concat(r.StdOut.Split('\n').Select(l => l.TrimEnd()).TakeLast(500)).ToList();
        }
        return new List<string>();
    }

    public System.Diagnostics.Process Stream(AdbClient adb, Action<string> onLine, bool clearFirst)
    {
        if (clearFirst) adb.Run("logcat", "-c");
        _log.Info("Streaming filtered logcat (NCSLoader, Unity, libil2cpp, AndroidRuntime, Fatal signal, SIGSEGV, SIGABRT)");
        return adb.StartLogcat(line =>
        {
            if (LogcatFilter.Relevant().IsMatch(line)) onLine(line);
        });
    }
}
