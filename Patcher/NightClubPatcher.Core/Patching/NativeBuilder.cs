using NightClubPatcher.Core.Elf;
using NightClubPatcher.Core.Infrastructure;
using NightClubPatcher.Core.Signing;

namespace NightClubPatcher.Core.Patching;

public sealed record NativeBuildResult(bool Ok, string Message, string? LoaderPath, IReadOnlyList<string> Libraries);

/// <summary>
/// BUILD NATIVE LOADER: runs Native/build.ps1 (Windows) or Native/build.sh with the portable tools
/// from Tools/ prepended to the PATH of the build process only (the user's PATH is never changed).
/// Output: Native/out/arm64-v8a/libncmkloader.so (+ libncmkmod_*.so) - where the patcher looks first.
/// </summary>
public sealed class NativeBuilder
{
    private readonly KitPaths _paths;
    private readonly ToolLocator _tools;
    private readonly KitLog _log;

    public NativeBuilder(KitPaths paths, ToolLocator tools, KitLog log)
    {
        _paths = paths;
        _tools = tools;
        _log = log;
    }

    public string NativeDir => Path.Combine(_paths.Root, "Native");
    public string OutputDir => _paths.NativeOut;
    public string OutputLoader => Path.Combine(OutputDir, GameConstants.LoaderLibName);

    public bool SourcesPresent =>
        File.Exists(Path.Combine(NativeDir, "CMakeLists.txt")) &&
        File.Exists(Path.Combine(_paths.Root, "SDK", "Core", "ncmk_mod_api.h"));

    /// <summary>Mods/&lt;Name&gt;/ folders with C/C++ sources (each one becomes libncmkmod_&lt;name&gt;.so).</summary>
    public List<string> ModSourceDirs()
    {
        var root = Path.Combine(_paths.Root, "Mods");
        if (!Directory.Exists(root)) return new List<string>();
        return Directory.EnumerateDirectories(root)
            .Where(d => Directory.EnumerateFiles(d, "*.*", SearchOption.AllDirectories)
                .Any(f => f.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".c", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Loader built and valid (AArch64 ELF).</summary>
    public bool LoaderReady(out string? path)
    {
        path = _paths.FindLoader();
        if (path == null) return false;
        try { return new ElfFile(File.ReadAllBytes(path)).IsAarch64; }
        catch { return false; }
    }

    public NativeBuildResult Build(CancellationToken ct = default)
    {
        _log.Step("Checking native build tools");
        if (!SourcesPresent)
            return new NativeBuildResult(false, $"Native sources not found in {NativeDir} (CMakeLists.txt and SDK/ are required).", null, Array.Empty<string>());

        var mods = ModSourceDirs();
        if (mods.Count == 0)
            _log.Warn($"No mod sources in {Path.Combine(_paths.Root, "Mods")} - building the loader only (sample mods skipped).");
        else
            _log.Info("Mods to build: " + string.Join(", ", mods.Select(Path.GetFileName)));

        var ndk = _tools.FindNdk();
        var cmake = _tools.FindCMake();
        var ninja = _tools.FindNinja();
        _log.Info($"Android NDK: {(ndk != null ? $"{ToolLocator.NdkVersion(ndk)}  [{ndk}]" : "missing")}");
        _log.Info($"CMake: {cmake ?? "missing"}");
        _log.Info($"Ninja: {ninja ?? "missing"}");
        if (ndk == null || cmake == null || ninja == null)
            return new NativeBuildResult(false, "Native build tools are missing. Click \"Install / Repair Tools\" first.", null, Array.Empty<string>());

        // Process-local environment: bundled tools first, the user's PATH untouched.
        var path = string.Join(Path.PathSeparator, new[]
        {
            Path.GetDirectoryName(cmake)!, Path.GetDirectoryName(ninja)!, ndk,
            Environment.GetEnvironmentVariable("PATH") ?? "",
        });
        var env = new Dictionary<string, string>
        {
            ["PATH"] = path,
            ["ANDROID_NDK_HOME"] = ndk,
            ["ANDROID_NDK_ROOT"] = ndk,
        };

        string file;
        List<string> args;
        if (OperatingSystem.IsWindows())
        {
            var ps = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            file = File.Exists(ps) ? ps : "powershell.exe";
            args = new List<string>
            {
                "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                "-File", Path.Combine(NativeDir, "build.ps1"),
                "-Ndk", ndk, "-CMake", cmake, "-Ninja", ninja,
            };
        }
        else
        {
            file = "bash";
            args = new List<string> { Path.Combine(NativeDir, "build.sh"), ndk };
            env["CMAKE"] = cmake;
            env["NINJA"] = ninja;
        }

        var started = DateTime.UtcNow.AddSeconds(-2);
        _log.Step("Building native loader (CMake + Ninja + NDK)");
        var exit = ProcessRunner.RunStreaming(file, args, line => _log.Info("  " + line), env, NativeDir, TimeSpan.FromMinutes(20), ct);
        if (exit != 0)
            return new NativeBuildResult(false, $"Native build failed (exit code {exit}). See the log for the compiler output.", null, Array.Empty<string>());

        _log.Step("Verifying libncmkloader.so");
        if (!File.Exists(OutputLoader))
            return new NativeBuildResult(false, $"The build finished but {OutputLoader} was not produced.", null, Array.Empty<string>());
        if (File.GetLastWriteTimeUtc(OutputLoader) < started)
            return new NativeBuildResult(false, "libncmkloader.so was not rebuilt (stale file).", null, Array.Empty<string>());
        var elf = new ElfFile(File.ReadAllBytes(OutputLoader));
        if (!elf.IsAarch64)
            return new NativeBuildResult(false, $"libncmkloader.so is {elf.MachineName}, expected AArch64.", null, Array.Empty<string>());

        var libs = Directory.EnumerateFiles(OutputDir, "*.so").Select(Path.GetFileName).OfType<string>().OrderBy(n => n).ToList();
        _log.Success($"Native loader ready: {OutputLoader}");
        foreach (var l in libs) _log.Info("  " + l);
        return new NativeBuildResult(true, "Native loader built: " + OutputLoader, OutputLoader, libs);
    }
}
