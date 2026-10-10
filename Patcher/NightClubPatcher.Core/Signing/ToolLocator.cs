using NightClubPatcher.Core.Infrastructure;

namespace NightClubPatcher.Core.Signing;

/// <summary>
/// Finds external tools. Lookup order: the kit's Tools/ folder, the Android SDK
/// (ANDROID_HOME / ANDROID_SDK_ROOT / %LOCALAPPDATA%\Android\Sdk), then PATH.
/// </summary>
public sealed class ToolLocator
{
    private readonly KitPaths _paths;

    public ToolLocator(KitPaths paths) => _paths = paths;

    private static bool IsWindows => OperatingSystem.IsWindows();
    private static string Exe(string name) => IsWindows ? name + ".exe" : name;

    private IEnumerable<string> SdkRoots()
    {
        foreach (var v in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
        {
            var p = Environment.GetEnvironmentVariable(v);
            if (!string.IsNullOrWhiteSpace(p)) yield return p;
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local)) yield return Path.Combine(local, "Android", "Sdk");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home)) yield return Path.Combine(home, "Android", "Sdk");
    }

    private IEnumerable<string> BuildToolsDirs()
    {
        foreach (var root in SdkRoots())
        {
            var bt = Path.Combine(root, "build-tools");
            if (!Directory.Exists(bt)) continue;
            foreach (var d in Directory.GetDirectories(bt).OrderByDescending(d => ParseVersion(Path.GetFileName(d))))
                yield return d;
        }
    }

    private static Version ParseVersion(string s) =>
        Version.TryParse(new string(s.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()), out var v) ? v : new Version(0, 0);

    private static string? FromPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var f = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(f)) return f;
            }
            catch
            {
                // ignore malformed PATH entries
            }
        }
        return null;
    }

    public string? FindAdb()
    {
        var candidates = new List<string>
        {
            Path.Combine(_paths.Tools, "adb", Exe("adb")),
            Path.Combine(_paths.Tools, "platform-tools", Exe("adb")),
        };
        candidates.AddRange(SdkRoots().Select(r => Path.Combine(r, "platform-tools", Exe("adb"))));
        return candidates.FirstOrDefault(File.Exists) ?? FromPath(Exe("adb"));
    }

    public string? FindZipalign()
    {
        var candidates = new List<string> { Path.Combine(_paths.Tools, "zipalign", Exe("zipalign")) };
        candidates.AddRange(BuildToolsDirs().Select(d => Path.Combine(d, Exe("zipalign"))));
        return candidates.FirstOrDefault(File.Exists) ?? FromPath(Exe("zipalign"));
    }

    /// <summary>Returns apksigner.jar (preferred, run through java).</summary>
    public string? FindApksignerJar()
    {
        var candidates = new List<string>
        {
            Path.Combine(_paths.Tools, "apksigner", "apksigner.jar"),
            Path.Combine(_paths.Tools, "apksigner", "lib", "apksigner.jar"),
        };
        candidates.AddRange(BuildToolsDirs().Select(d => Path.Combine(d, "lib", "apksigner.jar")));
        return candidates.FirstOrDefault(File.Exists);
    }

    public string? FindJava() => FindJdkTool("java");

    public string? FindKeytool() => FindJdkTool("keytool");

    private string? FindJdkTool(string name)
    {
        var candidates = new List<string> { Path.Combine(_paths.Tools, "jdk", "bin", Exe(name)) };
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome)) candidates.Add(Path.Combine(javaHome, "bin", Exe(name)));
        if (IsWindows)
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            candidates.Add(Path.Combine(pf, "Android", "Android Studio", "jbr", "bin", Exe(name)));
            candidates.Add(Path.Combine(pf, "Android", "Android Studio", "jre", "bin", Exe(name)));
        }
        return candidates.FirstOrDefault(File.Exists) ?? FromPath(Exe(name));
    }

    public string? FindApktool()
    {
        var jar = Path.Combine(_paths.Tools, "apktool", "apktool.jar");
        return File.Exists(jar) ? jar : null;
    }

    public IEnumerable<(string tool, string? path)> Report()
    {
        yield return ("adb", FindAdb());
        yield return ("zipalign", FindZipalign());
        yield return ("apksigner.jar", FindApksignerJar());
        yield return ("java", FindJava());
        yield return ("keytool", FindKeytool());
        yield return ("apktool.jar (optional, FULL PATCH)", FindApktool());
    }
}
