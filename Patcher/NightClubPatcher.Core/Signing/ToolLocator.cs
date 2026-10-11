using NightClubPatcher.Core.Infrastructure;

namespace NightClubPatcher.Core.Signing;

/// <summary>
/// Finds external tools. Lookup order: the kit's Tools/ folder, the shared per-user tools folder
/// (%LOCALAPPDATA%\NCMK\Tools, so a freshly unzipped kit reuses what an earlier one downloaded),
/// the Tools/ folders of other NCMK copies next to this one, the Android SDK
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

    /// <summary>Per-user tools folder shared by every NCMK copy (NCMK_SHARED_TOOLS overrides it).</summary>
    public static string? SharedToolsDir
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("NCMK_SHARED_TOOLS");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrEmpty(local) ? null : Path.Combine(local, "NCMK", "Tools");
        }
    }

    /// <summary>
    /// Where Install / Repair Tools puts new tools: the shared folder (so the next unzipped kit finds
    /// them), or the kit's own Tools/ when BundledOnly is set or no per-user folder exists.
    /// </summary>
    public string InstallRoot => BundledOnly || SharedToolsDir == null ? _paths.Tools : SharedToolsDir;

    public string BundledNdkDir => Path.Combine(InstallRoot, "ndk");
    public string BundledCMakeDir => Path.Combine(InstallRoot, "cmake");
    public string BundledNinjaDir => Path.Combine(InstallRoot, "ninja");

    private List<string>? _roots;

    /// <summary>Kit Tools/, shared tools, then Tools/ of sibling NCMK copies (cached per instance).</summary>
    public IReadOnlyList<string> ToolRoots
    {
        get
        {
            if (_roots != null && _rootsBundledOnly == BundledOnly) return _roots;
            var roots = new List<string> { _paths.Tools };
            if (!BundledOnly)
            {
                if (SharedToolsDir != null) roots.Add(SharedToolsDir);
                roots.AddRange(SiblingKitTools());
            }
            _rootsBundledOnly = BundledOnly;
            return _roots = roots.Select(r => Path.GetFullPath(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    private bool _rootsBundledOnly;

    /// <summary>
    /// Tools/ folders of other NCMK copies (e.g. Downloads\NCMK-main (2)\Release\Tools) found next to
    /// this kit, two levels up at most. Only folders that contain a tools.json are used.
    /// </summary>
    private IEnumerable<string> SiblingKitTools()
    {
        var found = new List<string>();
        try
        {
            var root = Path.GetFullPath(_paths.Root);
            var parent = Path.GetDirectoryName(root);
            var grand = parent == null ? null : Path.GetDirectoryName(parent);
            foreach (var dir in new[] { parent, grand })
            {
                if (dir == null || !Directory.Exists(dir)) continue;
                foreach (var kit in Directory.EnumerateDirectories(dir).Take(200))
                {
                    foreach (var t in new[] { Path.Combine(kit, "Tools"), Path.Combine(kit, "Release", "Tools") })
                        if (File.Exists(Path.Combine(t, "tools.json"))) found.Add(t);
                }
            }
        }
        catch
        {
            // unreadable folders are simply skipped
        }
        return found;
    }

    private IEnumerable<string> InRoots(params string[] rel) => ToolRoots.Select(r => Path.Combine(new[] { r }.Concat(rel).ToArray()));

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
        var candidates = new List<string>();
        foreach (var r in ToolRoots)
        {
            candidates.Add(Path.Combine(r, "adb", Exe("adb")));
            candidates.Add(Path.Combine(r, "platform-tools", Exe("adb")));
        }
        candidates.AddRange(SdkRoots().Select(r => Path.Combine(r, "platform-tools", Exe("adb"))));
        return candidates.FirstOrDefault(File.Exists) ?? FromPath(Exe("adb"));
    }

    public string? FindZipalign()
    {
        var candidates = InRoots("zipalign", Exe("zipalign")).ToList();
        candidates.AddRange(BuildToolsDirs().Select(d => Path.Combine(d, Exe("zipalign"))));
        return candidates.FirstOrDefault(File.Exists) ?? FromPath(Exe("zipalign"));
    }

    /// <summary>Returns apksigner.jar (preferred, run through java).</summary>
    public string? FindApksignerJar()
    {
        var candidates = new List<string>();
        foreach (var r in ToolRoots)
        {
            candidates.Add(Path.Combine(r, "apksigner", "apksigner.jar"));
            candidates.Add(Path.Combine(r, "apksigner", "lib", "apksigner.jar"));
        }
        candidates.AddRange(BuildToolsDirs().Select(d => Path.Combine(d, "lib", "apksigner.jar")));
        return candidates.FirstOrDefault(File.Exists);
    }

    public string? FindJava() => FindJdkTool("java");

    public string? FindKeytool() => FindJdkTool("keytool");

    private string? FindJdkTool(string name)
    {
        var candidates = InRoots("jdk", "bin", Exe(name)).ToList();
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
        var candidates = InRoots("ndk").ToList();
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
        var bundled = InRoots("cmake", "bin", Exe("cmake")).FirstOrDefault(File.Exists);
        if (bundled != null) return bundled;
        return FromPath(Exe("cmake")) ?? SdkCMakeBins().Select(b => Path.Combine(b, Exe("cmake"))).FirstOrDefault(File.Exists);
    }

    /// <summary>Order: Tools/ninja, PATH, Android SDK cmake/&lt;latest&gt;/bin (ships ninja).</summary>
    public string? FindNinja()
    {
        var bundled = InRoots("ninja", Exe("ninja")).FirstOrDefault(File.Exists);
        if (bundled != null) return bundled;
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

    /// <summary>True when the path lives inside an NCMK tools folder (this kit, shared, or a sibling kit).</summary>
    public bool IsBundled(string? path) =>
        path != null && ToolRoots.Any(r => Path.GetFullPath(path).StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    /// <summary>The tools root a located tool lives in (for tools.json), or null for system tools.</summary>
    public string? RootOf(string? path) =>
        path == null ? null : ToolRoots.FirstOrDefault(r => Path.GetFullPath(path).StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

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
