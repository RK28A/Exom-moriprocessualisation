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

    /// <summary>
    /// Only consider tools inside the kit's Tools/ folder (ignore Android SDK, JAVA_HOME, PATH).
    /// Set by NCMK_BUNDLED_TOOLS_ONLY=1 or the CLI --bundled-only flag; used to test a clean machine.
    /// </summary>
    public bool BundledOnly { get; set; } = Environment.GetEnvironmentVariable("NCMK_BUNDLED_TOOLS_ONLY") == "1";

    public string ToolsDir => _paths.Tools;
    public string BundledNdkDir => Path.Combine(_paths.Tools, "ndk");
    public string BundledCMakeDir => Path.Combine(_paths.Tools, "cmake");
    public string BundledNinjaDir => Path.Combine(_paths.Tools, "ninja");

    private static bool IsWindows => OperatingSystem.IsWindows();
    private static string Exe(string name) => IsWindows ? name + ".exe" : name;

    private IEnumerable<string> SdkRoots()
    {
        if (BundledOnly) yield break;
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

    private string? FromPath(string exe)
    {
        if (BundledOnly) return null;
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
        var javaHome = BundledOnly ? null : Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome)) candidates.Add(Path.Combine(javaHome, "bin", Exe(name)));
        if (IsWindows && !BundledOnly)
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            candidates.Add(Path.Combine(pf, "Android", "Android Studio", "jbr", "bin", Exe(name)));
            candidates.Add(Path.Combine(pf, "Android", "Android Studio", "jre", "bin", Exe(name)));
        }
        return candidates.FirstOrDefault(File.Exists) ?? FromPath(Exe(name));
    }

    // ------------------------------------------------------------------ native build tools

    /// <summary>NDK root (contains build/cmake/android.toolchain.cmake).</summary>
    /// Order: Tools/ndk, ANDROID_NDK_HOME, ANDROID_NDK_ROOT, Android SDK ndk/&lt;latest&gt; and ndk-bundle.
    public string? FindNdk()
    {
        var candidates = new List<string> { BundledNdkDir };
        if (!BundledOnly)
        {
            foreach (var v in new[] { "ANDROID_NDK_HOME", "ANDROID_NDK_ROOT" })
            {
                var p = Environment.GetEnvironmentVariable(v);
                if (!string.IsNullOrWhiteSpace(p)) candidates.Add(p);
            }
        }
        foreach (var root in SdkRoots())
        {
            var ndk = Path.Combine(root, "ndk");
            if (Directory.Exists(ndk))
                candidates.AddRange(Directory.GetDirectories(ndk).OrderByDescending(d => ParseVersion(Path.GetFileName(d))));
            candidates.Add(Path.Combine(root, "ndk-bundle"));
        }
        return candidates.FirstOrDefault(IsNdk);
    }

    public static bool IsNdk(string dir) =>
        File.Exists(Path.Combine(dir, "build", "cmake", "android.toolchain.cmake")) && File.Exists(Path.Combine(dir, "source.properties"));

    /// <summary>Pkg.Revision from source.properties, e.g. "27.2.12479018".</summary>
    public static string? NdkVersion(string ndkDir)
    {
        var props = Path.Combine(ndkDir, "source.properties");
        if (!File.Exists(props)) return null;
        foreach (var line in File.ReadLines(props))
        {
            var i = line.IndexOf('=');
            if (i > 0 && line[..i].Trim() == "Pkg.Revision") return line[(i + 1)..].Trim();
        }
        return null;
    }

    /// <summary>Order: Tools/cmake/bin, PATH, Android SDK cmake/&lt;latest&gt;/bin.</summary>
    public string? FindCMake()
    {
        var bundled = Path.Combine(BundledCMakeDir, "bin", Exe("cmake"));
        if (File.Exists(bundled)) return bundled;
        return FromPath(Exe("cmake")) ?? SdkCMakeBins().Select(b => Path.Combine(b, Exe("cmake"))).FirstOrDefault(File.Exists);
    }

    /// <summary>Order: Tools/ninja, PATH, Android SDK cmake/&lt;latest&gt;/bin (ships ninja).</summary>
    public string? FindNinja()
    {
        var bundled = Path.Combine(BundledNinjaDir, Exe("ninja"));
        if (File.Exists(bundled)) return bundled;
        return FromPath(Exe("ninja")) ?? SdkCMakeBins().Select(b => Path.Combine(b, Exe("ninja"))).FirstOrDefault(File.Exists);
    }

    private IEnumerable<string> SdkCMakeBins()
    {
        foreach (var root in SdkRoots())
        {
            var cm = Path.Combine(root, "cmake");
            if (!Directory.Exists(cm)) continue;
            foreach (var d in Directory.GetDirectories(cm).OrderByDescending(d => ParseVersion(Path.GetFileName(d))))
                yield return Path.Combine(d, "bin");
        }
    }

    /// <summary>True when the path lives inside the kit's Tools/ folder.</summary>
    public bool IsBundled(string? path) =>
        path != null && Path.GetFullPath(path).StartsWith(Path.GetFullPath(_paths.Tools) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

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
        yield return ("Android NDK", FindNdk());
        yield return ("cmake", FindCMake());
        yield return ("ninja", FindNinja());
        yield return ("apktool.jar (optional, FULL PATCH)", FindApktool());
    }
}
