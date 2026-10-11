using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NightClubPatcher.Core.Infrastructure;

namespace NightClubPatcher.Core.Signing;

public enum ToolId { Adb, BuildTools, Jdk, Ndk, CMake, Ninja }

/// <summary>State of one tool in the setup panel.</summary>
public sealed record ToolState(ToolId Id, string Name, bool Ready, string? Version, string? Location, bool Bundled, string? Problem)
{
    public string StatusText => Ready
        ? $"Ready{(Version != null ? " (" + Version + ")" : "")}{(Bundled ? "" : " - system")}"
        : Problem ?? "Missing";
}

public sealed record RemoteArchive(string PackagePath, Version Revision, string Url, string? Sha1, long Size);

public sealed record ToolSetupResult(bool Ok, IReadOnlyList<string> Installed, IReadOnlyList<string> Errors);

/// <summary>
/// Portable tool installer. Everything goes into the kit's Tools/ folder; nothing is installed
/// system-wide and PATH is never modified globally.
///
///   Tools/adb/          Android SDK platform-tools               (Google SDK repository, SHA-1)
///   Tools/zipalign/     zipalign      } latest stable Android     (Google SDK repository, SHA-1)
///   Tools/apksigner/    apksigner.jar } SDK build-tools
///   Tools/jdk/          Microsoft Build of OpenJDK 17             (SHA-256 published by Microsoft)
///   Tools/ndk/          Android NDK r27 (LTS)                     (Google SDK repository, SHA-1)
///   Tools/cmake/        CMake (official Kitware release)          (SHA-256 file of the release)
///   Tools/ninja/        Ninja (official ninja-build release)      (pinned SHA-256 / GitHub asset digest)
///
/// Each tool is downloaded to Cache/, verified, extracted to Tools/.staging/ and only then swapped
/// into place, so a failed download never breaks a tool that is already installed. Versions are
/// recorded in Tools/tools.json; a tool that is present and working is never downloaded again.
/// </summary>
public sealed class ToolSetup
{
    public const string RepositoryBase = "https://dl.google.com/android/repository/";
    public const string RepositoryXml = RepositoryBase + "repository2-3.xml";
    public const string LicenseUrl = "https://developer.android.com/studio/terms";
    public const string JdkLicenseUrl = "https://www.microsoft.com/openjdk";

    /// <summary>NDK r27 is the current LTS line; the newest stable r27 revision is preferred.</summary>
    public const string PreferredNdkPrefix = "ndk;27.";
    public const string CMakeVersion = "3.31.7";
    public const string NinjaVersion = "1.12.1";

    /// <summary>SHA-256 of the official ninja v1.12.1 release assets (ninja publishes no checksum file).</summary>
    public static readonly IReadOnlyDictionary<string, string> NinjaSha256 = new Dictionary<string, string>
    {
        ["ninja-win.zip"] = "f550fec705b6d6ff58f2db3c374c2277a37691678d6aba463adcbb129108467a",
        ["ninja-linux.zip"] = "6f98805688d19672bd699fbbfa2c2cf0fc054ac3df1f0e6a47664d963d530255",
    };

    public static readonly IReadOnlyList<ToolId> AllTools = new[] { ToolId.Adb, ToolId.BuildTools, ToolId.Jdk, ToolId.Ndk, ToolId.CMake, ToolId.Ninja };
    public static readonly IReadOnlyList<ToolId> NativeTools = new[] { ToolId.Ndk, ToolId.CMake, ToolId.Ninja };

    private readonly KitPaths _paths;
    private readonly ToolLocator _tools;
    private readonly KitLog _log;
    private readonly HttpClient _http;

    public ToolSetup(KitPaths paths, ToolLocator tools, KitLog log, HttpClient? http = null)
    {
        _paths = paths;
        _tools = tools;
        _log = log;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
        if (http == null) _http.DefaultRequestHeaders.UserAgent.ParseAdd("NCMK/0.4");
    }

    public string ManifestPath => Path.Combine(_tools.InstallRoot, "tools.json");
    public ToolsManifest Manifest => ToolsManifest.Load(ManifestPath);

    public static string HostOs =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macosx" : "linux";

    public static string Name(ToolId id) => id switch
    {
        ToolId.Adb => "ADB",
        ToolId.BuildTools => "Build Tools",
        ToolId.Jdk => "JDK 17",
        ToolId.Ndk => "Android NDK",
        ToolId.CMake => "CMake",
        ToolId.Ninja => "Ninja",
        _ => id.ToString(),
    };

    public static string Key(ToolId id) => id switch
    {
        ToolId.Adb => "adb",
        ToolId.BuildTools => "buildTools",
        ToolId.Jdk => "jdk",
        ToolId.Ndk => "ndk",
        ToolId.CMake => "cmake",
        ToolId.Ninja => "ninja",
        _ => id.ToString().ToLowerInvariant(),
    };

    // ------------------------------------------------------------------ status

    /// <summary>Cheap presence check (no processes started). Used on every refresh.</summary>
    public IReadOnlyList<string> Missing(bool includeNative = true)
    {
        var m = new List<string>();
        if (_tools.FindAdb() == null) m.Add("adb");
        if (_tools.FindZipalign() == null || _tools.FindApksignerJar() == null) m.Add("zipalign + apksigner");
        if (_tools.FindJava() == null || _tools.FindKeytool() == null) m.Add("Java 17");
        if (includeNative)
        {
            if (_tools.FindNdk() == null) m.Add("Android NDK");
            if (_tools.FindCMake() == null) m.Add("CMake");
            if (_tools.FindNinja() == null) m.Add("Ninja");
        }
        return m;
    }

    /// <summary>Full check: locates every tool and runs it to read its version.</summary>
    public IReadOnlyList<ToolState> CheckAll(IEnumerable<ToolId>? ids = null) => (ids ?? AllTools).Select(Check).ToList();

    public ToolState Check(ToolId id)
    {
        var manifest = Manifest;
        try
        {
            switch (id)
            {
                case ToolId.Adb:
                {
                    var adb = _tools.FindAdb();
                    if (adb == null) return Missing(id);
                    var r = ProcessRunner.Run(adb, new[] { "version" }, TimeSpan.FromSeconds(20));
                    var v = Regex.Match(r.StdOut, @"Version\s+([\d.]+)").Groups[1].Value;
                    return r.Ok ? Ready(id, Nz(v) ?? manifest.Version(Key(id)), adb) : Broken(id, adb, "adb does not run");
                }
                case ToolId.BuildTools:
                {
                    var za = _tools.FindZipalign();
                    var jar = _tools.FindApksignerJar();
                    if (za == null || jar == null) return Missing(id, za == null ? "zipalign missing" : "apksigner.jar missing");
                    var root = _tools.RootOf(za);
                    var v = root != null ? ToolsManifest.Load(Path.Combine(root, "tools.json")).Version(Key(id)) : Path.GetFileName(Path.GetDirectoryName(za));
                    return Ready(id, v, za);
                }
                case ToolId.Jdk:
                {
                    var java = _tools.FindJava();
                    if (java == null || _tools.FindKeytool() == null) return Missing(id);
                    var r = ProcessRunner.Run(java, new[] { "-version" }, TimeSpan.FromSeconds(30));
                    var v = Regex.Match(r.StdErr + r.StdOut, "version \"([^\"]+)\"").Groups[1].Value;
                    if (!r.Ok) return Broken(id, java, "java does not run");
                    var major = int.TryParse(v.Split('.')[0], out var mj) ? mj : 0;
                    return major is > 0 and < 11 ? Broken(id, java, $"Java {v} is too old (17 needed)") : Ready(id, Nz(v), java);
                }
                case ToolId.Ndk:
                {
                    var ndk = _tools.FindNdk();
                    return ndk == null ? Missing(id) : Ready(id, ToolLocator.NdkVersion(ndk), ndk);
                }
                case ToolId.CMake:
                {
                    var cmake = _tools.FindCMake();
                    if (cmake == null) return Missing(id);
                    var r = ProcessRunner.Run(cmake, new[] { "--version" }, TimeSpan.FromSeconds(30));
                    var v = Regex.Match(r.StdOut, @"cmake version\s+([\d.]+)").Groups[1].Value;
                    return r.Ok ? Ready(id, Nz(v), cmake) : Broken(id, cmake, "cmake does not run");
                }
                case ToolId.Ninja:
                {
                    var ninja = _tools.FindNinja();
                    if (ninja == null) return Missing(id);
                    var r = ProcessRunner.Run(ninja, new[] { "--version" }, TimeSpan.FromSeconds(30));
                    return r.Ok ? Ready(id, Nz(r.StdOut.Trim()), ninja) : Broken(id, ninja, "ninja does not run");
                }
            }
        }
        catch (Exception ex)
        {
            return new ToolState(id, Name(id), false, null, null, false, "Check failed: " + ex.Message);
        }
        return Missing(id);
    }

    private static string? Nz(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    private ToolState Ready(ToolId id, string? version, string path) => new(id, Name(id), true, version, path, _tools.IsBundled(path), null);
    private static ToolState Missing(ToolId id, string? why = null) => new(id, Name(id), false, null, null, false, why ?? "Missing");
    private ToolState Broken(ToolId id, string path, string why) => new(id, Name(id), false, null, path, _tools.IsBundled(path), why);

    public string ConsentText(IReadOnlyCollection<ToolId> toInstall)
    {
        static string Size(ToolId id) => id switch
        {
            ToolId.Adb => "~10 MB",
            ToolId.BuildTools => "~60 MB",
            ToolId.Jdk => "~190 MB",
            ToolId.Ndk => "~700 MB download, ~2.5 GB on disk",
            ToolId.CMake => "~45 MB",
            ToolId.Ninja => "<1 MB",
            _ => "",
        };
        return "The following tools will be downloaded into " + _tools.InstallRoot + " (shared by every NCMK copy on this PC):" + Environment.NewLine +
               string.Join(Environment.NewLine, toInstall.Select(t => $" - {Name(t)} ({Size(t)})")) + Environment.NewLine + Environment.NewLine +
               "Nothing is installed system-wide and your PATH is not changed." + Environment.NewLine +
               "Sources: Android SDK tools and the NDK from dl.google.com (by downloading them you accept the Android SDK " +
               $"License Agreement, {LicenseUrl}); CMake and Ninja from their official GitHub releases; Java from the " +
               $"Microsoft Build of OpenJDK ({JdkLicenseUrl}). Every archive is checksum-verified.";
    }

    // ------------------------------------------------------------------ install

    /// <summary>Installs (or repairs) every tool that is not ready. Already working tools are kept.</summary>
    public async Task<ToolSetupResult> InstallMissingAsync(bool includeNative = true, CancellationToken ct = default)
    {
        _log.Step("Checking tools");
        var states = CheckAll(includeNative ? AllTools : AllTools.Except(NativeTools));
        foreach (var s in states) _log.Info($"{s.Name}: {(s.Ready ? "ready" + (s.Version != null ? " (" + s.Version + ")" : "") : s.Problem?.ToLowerInvariant() ?? "missing")}");
        return await InstallAsync(states.Where(s => !s.Ready).Select(s => s.Id).ToList(), ct);
    }

    public async Task<ToolSetupResult> InstallAsync(IReadOnlyCollection<ToolId> ids, CancellationToken ct = default)
    {
        var installed = new List<string>();
        var errors = new List<string>();
        RemoteRepository? repo = null;
        Exception? repoError = null;

        async Task<RemoteRepository> Repo()
        {
            if (repo != null) return repo;
            if (repoError != null) throw new InvalidOperationException("Android SDK repository unavailable: " + repoError.Message, repoError);
            try
            {
                _log.Info("Reading the official Android SDK repository manifest");
                repo = RemoteRepository.Parse(await _http.GetStringAsync(RepositoryXml, ct));
                return repo;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                repoError = ex;
                throw;
            }
        }

        foreach (var id in AllTools.Where(ids.Contains))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                switch (id)
                {
                    case ToolId.Adb: await InstallAdbAsync(await Repo(), ct); break;
                    case ToolId.BuildTools: await InstallBuildToolsAsync(await Repo(), ct); break;
                    case ToolId.Jdk: await InstallJdkAsync(ct); break;
                    case ToolId.Ndk: await InstallNdkAsync(await Repo(), ct); break;
                    case ToolId.CMake: await InstallCMakeAsync(ct); break;
                    case ToolId.Ninja: await InstallNinjaAsync(ct); break;
                }
                installed.Add(Name(id));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Error($"{Name(id)}: {ex.Message}");
                errors.Add($"{Name(id)}: {ex.Message}");
            }
        }

        if (ids.Any(NativeTools.Contains)) ConfigureNativeToolchain(errors);
        if (errors.Count > 0) _log.Warn("Some tools could not be installed. Already installed tools were kept; use Retry.");
        return new ToolSetupResult(errors.Count == 0, installed, errors);
    }

    private void ConfigureNativeToolchain(List<string> errors)
    {
        _log.Step("Configuring toolchain");
        var native = CheckAll(NativeTools);
        foreach (var s in native) _log.Info($"{s.Name}: {s.StatusText}" + (s.Location != null ? $"  [{s.Location}]" : ""));
        if (native.All(s => s.Ready)) _log.Success("Native build tools ready.");
        else if (errors.Count == 0) errors.Add("Native build tools are still not ready: " + string.Join(", ", native.Where(s => !s.Ready).Select(s => s.Name)));
    }

    private async Task InstallAdbAsync(RemoteRepository repo, CancellationToken ct)
    {
        var a = repo.Latest("platform-tools", HostOs) ?? throw new InvalidOperationException("platform-tools not found in the SDK repository.");
        _log.Step("Downloading ADB (platform-tools)");
        var file = await DownloadAsync(a.Url, ct);
        var sha256 = Verify(file, a.Sha1, null);
        InstallFromArchive(file, Path.Combine(_tools.InstallRoot, "adb"), stripTopFolder: true);
        Manifest.Set(Key(ToolId.Adb), a.Revision.ToString(3), Details(a.Url, sha256, "SHA-1 from Google SDK repository"));
    }

    private async Task InstallBuildToolsAsync(RemoteRepository repo, CancellationToken ct)
    {
        var a = repo.Latest("build-tools;", HostOs) ?? throw new InvalidOperationException("build-tools not found in the SDK repository.");
        _log.Step($"Downloading Android build-tools {a.Revision}");
        var file = await DownloadAsync(a.Url, ct);
        var sha256 = Verify(file, a.Sha1, null);
        var staging = Extract(file, "build-tools", stripTopFolder: true);
        try
        {
            var exe = OperatingSystem.IsWindows() ? "zipalign.exe" : "zipalign";
            var za = Path.Combine(_paths.Cache, "staged-zipalign");
            var asg = Path.Combine(_paths.Cache, "staged-apksigner");
            TryDelete(za);
            TryDelete(asg);
            Directory.CreateDirectory(za);
            Directory.CreateDirectory(asg);
            File.Copy(Path.Combine(staging, exe), Path.Combine(za, exe));
            foreach (var dll in Directory.EnumerateFiles(staging, "*.dll")) File.Copy(dll, Path.Combine(za, Path.GetFileName(dll)));
            File.Copy(Path.Combine(staging, "lib", "apksigner.jar"), Path.Combine(asg, "apksigner.jar"));
            MakeExecutable(Path.Combine(za, exe));
            SwapInto(za, Path.Combine(_tools.InstallRoot, "zipalign"));
            SwapInto(asg, Path.Combine(_tools.InstallRoot, "apksigner"));
        }
        finally
        {
            TryDelete(staging);
        }
        Manifest.Set(Key(ToolId.BuildTools), a.Revision.ToString(3), Details(a.Url, sha256, "SHA-1 from Google SDK repository"));
    }

    private async Task InstallJdkAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            throw new NotSupportedException("Automatic JDK install is implemented for Windows; install a JDK 17 and set JAVA_HOME.");
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "aarch64" : "x64";
        var url = $"https://aka.ms/download-jdk/microsoft-jdk-17-windows-{arch}.zip";
        _log.Step("Downloading JDK 17 (Microsoft Build of OpenJDK)");
        var expected = (await _http.GetStringAsync(url + ".sha256sum.txt", ct)).Split(' ', '\t', '\n')[0].Trim();
        var file = await DownloadAsync(url, ct);
        var sha256 = Verify(file, null, expected);
        InstallFromArchive(file, Path.Combine(_tools.InstallRoot, "jdk"), stripTopFolder: true);
        var java = Path.Combine(_tools.InstallRoot, "jdk", "bin", "java.exe");
        var r = ProcessRunner.Run(java, new[] { "-version" }, TimeSpan.FromSeconds(30));
        var v = Regex.Match(r.StdErr + r.StdOut, "version \"([^\"]+)\"").Groups[1].Value;
        Manifest.Set(Key(ToolId.Jdk), Nz(v) ?? "17", Details(url, sha256, "SHA-256 published by Microsoft"));
    }

    private async Task InstallNdkAsync(RemoteRepository repo, CancellationToken ct)
    {
        var a = repo.LatestWithPrefix(PreferredNdkPrefix, HostOs) ?? repo.Latest("ndk;", HostOs)
                ?? throw new InvalidOperationException("No stable NDK found in the SDK repository.");
        _log.Step($"Downloading Android NDK {a.PackagePath["ndk;".Length..]}");
        var file = await DownloadAsync(a.Url, ct);
        var sha256 = Verify(file, a.Sha1, null);
        var target = _tools.BundledNdkDir;
        InstallFromArchive(file, target, stripTopFolder: true);
        if (!ToolLocator.IsNdk(target)) throw new InvalidDataException("The extracted NDK does not contain build/cmake/android.toolchain.cmake.");
        Manifest.Set(Key(ToolId.Ndk), ToolLocator.NdkVersion(target) ?? a.PackagePath["ndk;".Length..],
            Details(a.Url, sha256, "SHA-1 from Google SDK repository"));
    }

    private async Task InstallCMakeAsync(CancellationToken ct)
    {
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x86_64";
        var asset = OperatingSystem.IsWindows() ? $"cmake-{CMakeVersion}-windows-{arch}.zip"
            : OperatingSystem.IsLinux() ? $"cmake-{CMakeVersion}-linux-{(arch == "arm64" ? "aarch64" : "x86_64")}.tar.gz"
            : throw new NotSupportedException("Automatic CMake install is available for Windows and Linux.");
        var baseUrl = $"https://github.com/Kitware/CMake/releases/download/v{CMakeVersion}/";
        _log.Step($"Downloading CMake {CMakeVersion}");
        var sums = await _http.GetStringAsync(baseUrl + $"cmake-{CMakeVersion}-SHA-256.txt", ct);
        var expected = ParseChecksumFile(sums, asset) ?? throw new InvalidDataException($"{asset} is not listed in the official SHA-256 file.");
        var file = await DownloadAsync(baseUrl + asset, ct);
        var sha256 = Verify(file, null, expected);
        InstallFromArchive(file, _tools.BundledCMakeDir, stripTopFolder: true);
        Manifest.Set(Key(ToolId.CMake), CMakeVersion, Details(baseUrl + asset, sha256, "SHA-256 file of the official Kitware release"));
    }

    private async Task InstallNinjaAsync(CancellationToken ct)
    {
        var asset = OperatingSystem.IsWindows() ? "ninja-win.zip" : OperatingSystem.IsLinux() ? "ninja-linux.zip" : "ninja-mac.zip";
        var url = $"https://github.com/ninja-build/ninja/releases/download/v{NinjaVersion}/{asset}";
        _log.Step($"Downloading Ninja {NinjaVersion}");
        var expected = NinjaSha256.TryGetValue(asset, out var pinned) ? pinned : await GitHubAssetDigestAsync("ninja-build/ninja", "v" + NinjaVersion, asset, ct);
        var file = await DownloadAsync(url, ct);
        var sha256 = Verify(file, null, expected);
        InstallFromArchive(file, _tools.BundledNinjaDir, stripTopFolder: false);
        Manifest.Set(Key(ToolId.Ninja), NinjaVersion, Details(url, sha256, "pinned SHA-256 of the official release asset"));
    }

    private static Dictionary<string, string?> Details(string url, string sha256, string verifiedWith) =>
        new() { ["url"] = AbsoluteUrl(url), ["sha256"] = sha256, ["verifiedWith"] = verifiedWith };

    /// <summary>SDK repository archives are listed relative to <see cref="RepositoryBase"/>.</summary>
    public static string AbsoluteUrl(string url) =>
        url.StartsWith("https://", StringComparison.Ordinal) ? url : RepositoryBase + url;

    /// <summary>"&lt;sha256&gt;  &lt;file&gt;" lines (sha256sum format).</summary>
    public static string? ParseChecksumFile(string text, string fileName)
    {
        foreach (var raw in text.Split('\n'))
        {
            var parts = raw.Trim().Split((char[])[' ', '\t', '*'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[^1] == fileName && parts[0].Length == 64) return parts[0].ToLowerInvariant();
        }
        return null;
    }

    /// <summary>SHA-256 digest GitHub records for a release asset ("digest": "sha256:...").</summary>
    private async Task<string> GitHubAssetDigestAsync(string repo, string tag, string asset, CancellationToken ct)
    {
        var json = await _http.GetStringAsync($"https://api.github.com/repos/{repo}/releases/tags/{tag}", ct);
        return ParseGitHubAssetDigest(json, asset) ?? throw new InvalidDataException($"GitHub has no SHA-256 digest for {asset}.");
    }

    public static string? ParseGitHubAssetDigest(string releaseJson, string asset)
    {
        using var doc = JsonDocument.Parse(releaseJson);
        if (!doc.RootElement.TryGetProperty("assets", out var assets)) return null;
        foreach (var a in assets.EnumerateArray())
        {
            if (a.GetProperty("name").GetString() != asset) continue;
            if (a.TryGetProperty("digest", out var d) && d.GetString() is { } s && s.StartsWith("sha256:", StringComparison.Ordinal))
                return s["sha256:".Length..].ToLowerInvariant();
        }
        return null;
    }

    // ------------------------------------------------------------------ download / verify / extract

    private async Task<string> DownloadAsync(string urlOrRelative, CancellationToken ct)
    {
        var url = AbsoluteUrl(urlOrRelative);
        if (!url.StartsWith("https://", StringComparison.Ordinal)) throw new InvalidOperationException("Refusing non-HTTPS download.");
        var dir = Path.Combine(_paths.Cache, "downloads");
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, Path.GetFileName(new Uri(url).AbsolutePath));
        var partial = dest + ".partial";
        _log.Info(url);
        using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using (var dst = File.Create(partial))
            {
                var buf = new byte[1 << 16];
                long done = 0, lastReport = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0 && done - lastReport >= total / 10)
                    {
                        lastReport = done;
                        _log.Info($"  {done * 100 / total}%  ({done / 1048576} / {total / 1048576} MB)");
                    }
                }
            }
        }
        File.Move(partial, dest, overwrite: true);
        return dest;
    }

    /// <summary>Verifies the official checksum(s); returns the SHA-256 recorded in tools.json.</summary>
    private string Verify(string file, string? sha1, string? sha256)
    {
        _log.Step("Verifying checksums");
        if (sha1 == null && sha256 == null) { TryDelete(file); throw new InvalidDataException("No official checksum available; refusing to install."); }
        if (sha1 != null)
        {
            using var fs = File.OpenRead(file);
            var actual = Convert.ToHexString(SHA1.HashData(fs)).ToLowerInvariant();
            if (!Hashing.Equal(actual, sha1)) { fs.Dispose(); TryDelete(file); throw new InvalidDataException($"SHA-1 mismatch for {Path.GetFileName(file)} ({actual} != {sha1})."); }
        }
        var actual256 = Hashing.Sha256File(file);
        if (sha256 != null && !Hashing.Equal(actual256, sha256))
        {
            TryDelete(file);
            throw new InvalidDataException($"SHA-256 mismatch for {Path.GetFileName(file)} ({actual256} != {sha256}).");
        }
        _log.Info($"  OK  sha256={actual256}");
        return actual256;
    }

    /// <summary>Extracts into Tools/.staging/&lt;name&gt; and returns that folder.</summary>
    private string Extract(string archive, string name, bool stripTopFolder)
    {
        _log.Step("Extracting");
        var staging = Path.Combine(_tools.InstallRoot, ".staging", name);
        TryDelete(staging);
        ExtractArchive(archive, staging, stripTopFolder);
        TryDelete(archive);
        return staging;
    }

    private void InstallFromArchive(string archive, string target, bool stripTopFolder)
    {
        var staging = Extract(archive, Path.GetFileName(target), stripTopFolder);
        SwapInto(staging, target);
        _log.Success($"Installed in {target}");
    }

    /// <summary>
    /// Replaces <paramref name="target"/> with <paramref name="staging"/>. The previous version is only
    /// deleted after the new one is in place, and restored if the move fails.
    /// </summary>
    public static void SwapInto(string staging, string target)
    {
        var old = target + ".old";
        TryDelete(old);
        var hadOld = Directory.Exists(target);
        if (hadOld) Directory.Move(target, old);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
            Directory.Move(staging, target);
        }
        catch
        {
            if (hadOld && !Directory.Exists(target)) Directory.Move(old, target);
            throw;
        }
        TryDelete(old);
    }

    public static void ExtractArchive(string archive, string destination, bool stripTopFolder)
    {
        if (archive.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || archive.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            ExtractTarGz(archive, destination, stripTopFolder);
        else
            ExtractZip(archive, destination, stripTopFolder);
    }

    private static string SafeTarget(string root, string name, string original)
    {
        var target = Path.GetFullPath(Path.Combine(root, name));
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException($"Archive entry escapes the destination: {original}");
        return target;
    }

    private static string? Strip(string name, bool stripTopFolder)
    {
        name = name.Replace('\\', '/');
        if (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
        if (stripTopFolder)
        {
            var slash = name.IndexOf('/');
            if (slash < 0) return null;
            name = name[(slash + 1)..];
        }
        return name.Length == 0 || name.EndsWith('/') ? null : name;
    }

    /// <summary>Extracts a ZIP, optionally removing its single top-level folder, rejecting path traversal.</summary>
    public static void ExtractZip(string zipPath, string destination, bool stripTopFolder, Func<string, bool>? filter = null)
    {
        var root = Path.GetFullPath(destination);
        Directory.CreateDirectory(root);
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var e in zip.Entries)
        {
            var name = Strip(e.FullName, stripTopFolder);
            if (name == null || (filter != null && !filter(name))) continue;
            var target = SafeTarget(root, name, e.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, overwrite: true);
            if (!OperatingSystem.IsWindows() && (e.ExternalAttributes >> 16 & 0x40) != 0) MakeExecutable(target);
        }
    }

    public static void ExtractTarGz(string path, string destination, bool stripTopFolder)
    {
        var root = Path.GetFullPath(destination);
        Directory.CreateDirectory(root);
        using var fs = File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var tar = new TarReader(gz);
        while (tar.GetNextEntry() is { } e)
        {
            var name = Strip(e.Name, stripTopFolder);
            if (name == null) continue;
            var target = SafeTarget(root, name, e.Name);
            switch (e.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(target);
                    break;
                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    e.ExtractToFile(target, overwrite: true);
                    break;
                case TarEntryType.SymbolicLink when !OperatingSystem.IsWindows():
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (Path.IsPathRooted(e.LinkName) || e.LinkName.Contains(".."))
                        throw new InvalidDataException($"Unsafe symbolic link in archive: {e.Name}");
                    File.CreateSymbolicLink(target, e.LinkName);
                    break;
            }
        }
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch
        {
            // best effort
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }
}

public sealed class RemoteRepository
{
    private readonly List<RemoteArchive> _archives = new();
    private readonly Dictionary<RemoteArchive, string> _hostOs = new();

    public static RemoteRepository Parse(string xml)
    {
        var repo = new RemoteRepository();
        var doc = XDocument.Parse(xml);
        foreach (var pkg in doc.Descendants().Where(e => e.Name.LocalName == "remotePackage"))
        {
            var path = (string?)pkg.Attribute("path");
            if (path == null) continue;
            // Stable channel only: no <preview> revision and no channelRef other than channel-0.
            var rev = pkg.Elements().FirstOrDefault(e => e.Name.LocalName == "revision");
            if (rev == null || rev.Elements().Any(e => e.Name.LocalName == "preview")) continue;
            var channel = (string?)pkg.Elements().FirstOrDefault(e => e.Name.LocalName == "channelRef")?.Attribute("ref");
            if (channel != null && channel != "channel-0") continue;
            int Part(string n) => int.TryParse(rev.Elements().FirstOrDefault(e => e.Name.LocalName == n)?.Value, out var v) ? v : 0;
            var version = new Version(Part("major"), Part("minor"), Part("micro"));

            foreach (var a in pkg.Descendants().Where(e => e.Name.LocalName == "archive"))
            {
                var complete = a.Elements().FirstOrDefault(e => e.Name.LocalName == "complete");
                var url = complete?.Elements().FirstOrDefault(e => e.Name.LocalName == "url")?.Value;
                if (url == null) continue;
                var sha1 = complete!.Elements().FirstOrDefault(e => e.Name.LocalName == "checksum")?.Value;
                long.TryParse(complete.Elements().FirstOrDefault(e => e.Name.LocalName == "size")?.Value, out var size);
                var os = a.Elements().FirstOrDefault(e => e.Name.LocalName == "host-os")?.Value ?? "any";
                var arch = new RemoteArchive(path, version, url.Trim(), sha1?.Trim(), size);
                repo._archives.Add(arch);
                repo._hostOs[arch] = os;
            }
        }
        return repo;
    }

    /// <summary>Newest stable archive whose package path starts with <paramref name="prefix"/> (e.g. "ndk;27.").</summary>
    public RemoteArchive? LatestWithPrefix(string prefix, string hostOs) =>
        _archives
            .Where(a => a.PackagePath.StartsWith(prefix, StringComparison.Ordinal))
            .Where(a => _hostOs[a] == hostOs || _hostOs[a] == "any")
            .OrderByDescending(a => a.Revision)
            .FirstOrDefault();

    /// <summary>Newest stable archive whose package path equals (or, ending with ';', starts with) <paramref name="pathOrPrefix"/>.</summary>
    public RemoteArchive? Latest(string pathOrPrefix, string hostOs) =>
        _archives
            .Where(a => pathOrPrefix.EndsWith(';') ? a.PackagePath.StartsWith(pathOrPrefix, StringComparison.Ordinal) : a.PackagePath == pathOrPrefix)
            .Where(a => _hostOs[a] == hostOs || _hostOs[a] == "any")
            .OrderByDescending(a => a.Revision)
            .FirstOrDefault();
}
