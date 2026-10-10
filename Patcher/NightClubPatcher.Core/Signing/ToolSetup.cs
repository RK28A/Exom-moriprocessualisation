using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;
using NightClubPatcher.Core.Infrastructure;

namespace NightClubPatcher.Core.Signing;

public sealed record RemoteArchive(string PackagePath, Version Revision, string Url, string? Sha1, long Size);

public sealed record ToolSetupResult(bool Ok, IReadOnlyList<string> Installed, IReadOnlyList<string> Errors);

/// <summary>
/// Downloads the external tools into Tools/ so the patcher works out of the box:
///   Tools/adb/        Android SDK platform-tools (adb + DLLs)
///   Tools/zipalign/   zipalign from the latest stable Android SDK build-tools
///   Tools/apksigner/  apksigner.jar from the same build-tools
///   Tools/jdk/        Microsoft Build of OpenJDK 17 (java + keytool for apksigner)
/// Android packages are located through Google's official SDK repository manifest and their
/// SHA-1 checksums are verified. Nothing is downloaded without the caller's consent.
/// </summary>
public sealed class ToolSetup
{
    public const string RepositoryBase = "https://dl.google.com/android/repository/";
    public const string RepositoryXml = RepositoryBase + "repository2-3.xml";
    public const string LicenseUrl = "https://developer.android.com/studio/terms";
    public const string JdkLicenseUrl = "https://www.microsoft.com/openjdk";

    private readonly KitPaths _paths;
    private readonly ToolLocator _tools;
    private readonly KitLog _log;
    private readonly HttpClient _http;

    public ToolSetup(KitPaths paths, ToolLocator tools, KitLog log, HttpClient? http = null)
    {
        _paths = paths;
        _tools = tools;
        _log = log;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        if (http == null) _http.DefaultRequestHeaders.UserAgent.ParseAdd("NightClubModKit/0.2");
    }

    public static string HostOs =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macosx" : "linux";

    /// <summary>What is missing right now.</summary>
    public IReadOnlyList<string> Missing()
    {
        var m = new List<string>();
        if (_tools.FindAdb() == null) m.Add("adb (Android platform-tools)");
        if (_tools.FindZipalign() == null || _tools.FindApksignerJar() == null) m.Add("zipalign + apksigner (Android build-tools)");
        if (_tools.FindJava() == null || _tools.FindKeytool() == null) m.Add("Java 17 (OpenJDK)");
        return m;
    }

    public string ConsentText()
    {
        var missing = Missing();
        return "The following tools are missing and can be downloaded into Tools/:" + Environment.NewLine +
               string.Join(Environment.NewLine, missing.Select(x => " - " + x)) + Environment.NewLine + Environment.NewLine +
               "Android SDK tools come from dl.google.com (checksums verified). By downloading them you accept the " +
               $"Android SDK License Agreement ({LicenseUrl})." + Environment.NewLine +
               $"Java comes from the Microsoft Build of OpenJDK ({JdkLicenseUrl}, GPLv2 + Classpath Exception).";
    }

    public async Task<ToolSetupResult> InstallMissingAsync(CancellationToken ct = default)
    {
        var installed = new List<string>();
        var errors = new List<string>();
        RemoteRepository? repo = null;

        async Task<RemoteRepository> Repo()
        {
            if (repo != null) return repo;
            _log.Step("Reading the Android SDK repository");
            repo = RemoteRepository.Parse(await _http.GetStringAsync(RepositoryXml, ct));
            return repo;
        }

        async Task Try(string name, Func<Task> action)
        {
            try
            {
                await action();
                installed.Add(name);
            }
            catch (Exception ex)
            {
                _log.Error($"{name}: {ex.Message}");
                errors.Add($"{name}: {ex.Message}");
            }
        }

        if (_tools.FindAdb() == null)
            await Try("adb", async () =>
            {
                var a = (await Repo()).Latest("platform-tools", HostOs) ?? throw new InvalidOperationException("platform-tools not found in repository");
                await InstallArchiveAsync(a, Path.Combine(_paths.Tools, "adb"), stripTopFolder: true, filter: null, ct);
            });

        if (_tools.FindZipalign() == null || _tools.FindApksignerJar() == null)
            await Try("zipalign + apksigner", async () =>
            {
                var a = (await Repo()).Latest("build-tools;", HostOs) ?? throw new InvalidOperationException("build-tools not found in repository");
                var staging = Path.Combine(_paths.Cache, "build-tools-" + a.Revision);
                await InstallArchiveAsync(a, staging, stripTopFolder: true, filter: null, ct);
                var exe = OperatingSystem.IsWindows() ? "zipalign.exe" : "zipalign";
                var za = Path.Combine(_paths.Tools, "zipalign");
                var asg = Path.Combine(_paths.Tools, "apksigner");
                Directory.CreateDirectory(za);
                Directory.CreateDirectory(asg);
                File.Copy(Path.Combine(staging, exe), Path.Combine(za, exe), true);
                foreach (var dll in Directory.EnumerateFiles(staging, "*.dll")) File.Copy(dll, Path.Combine(za, Path.GetFileName(dll)), true);
                File.Copy(Path.Combine(staging, "lib", "apksigner.jar"), Path.Combine(asg, "apksigner.jar"), true);
                if (!OperatingSystem.IsWindows()) MakeExecutable(Path.Combine(za, exe));
                TryDelete(staging);
            });

        if (_tools.FindJava() == null || _tools.FindKeytool() == null)
            await Try("Java 17", async () =>
            {
                var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
                           System.Runtime.InteropServices.Architecture.Arm64 ? "aarch64" : "x64";
                var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
                var ext = OperatingSystem.IsWindows() ? "zip" : "tar.gz";
                if (ext != "zip") throw new NotSupportedException("Automatic JDK install is only implemented for Windows; install a JDK 17 and set JAVA_HOME.");
                var url = $"https://aka.ms/download-jdk/microsoft-jdk-17-{os}-{arch}.{ext}";
                string? sha256 = null;
                try
                {
                    sha256 = (await _http.GetStringAsync(url + ".sha256sum.txt", ct)).Split(' ', '\t', '\n')[0].Trim();
                }
                catch
                {
                    _log.Warn("JDK checksum file unavailable; continuing over HTTPS only.");
                }
                var archive = new RemoteArchive("jdk-17", new Version(17, 0), url, null, 0);
                await InstallArchiveAsync(archive, Path.Combine(_paths.Tools, "jdk"), stripTopFolder: true, filter: null, ct, sha256);
            });

        return new ToolSetupResult(errors.Count == 0, installed, errors);
    }

    private async Task InstallArchiveAsync(RemoteArchive archive, string destination, bool stripTopFolder, Func<string, bool>? filter,
        CancellationToken ct, string? sha256 = null)
    {
        var url = archive.Url.StartsWith("https://", StringComparison.Ordinal) ? archive.Url : RepositoryBase + archive.Url;
        if (!url.StartsWith("https://", StringComparison.Ordinal)) throw new InvalidOperationException("Refusing non-HTTPS download.");
        Directory.CreateDirectory(_paths.Cache);
        var zipPath = Path.Combine(_paths.Cache, "download-" + Path.GetFileName(new Uri(url).AbsolutePath));

        _log.Step($"Downloading {archive.PackagePath} {archive.Revision}");
        _log.Info(url);
        using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? archive.Size;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zipPath);
            var buf = new byte[1 << 16];
            long done = 0, lastReport = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0 && done - lastReport > total / 10)
                {
                    lastReport = done;
                    _log.Info($"  {done * 100 / total}% ({done / 1048576} / {total / 1048576} MB)");
                }
            }
        }

        _log.Info("Verifying checksum");
        if (archive.Sha1 != null)
        {
            await using var fs = File.OpenRead(zipPath);
            var actual = Convert.ToHexString(await SHA1.HashDataAsync(fs, ct)).ToLowerInvariant();
            if (!Hashing.Equal(actual, archive.Sha1)) { TryDelete(zipPath); throw new InvalidDataException($"SHA-1 mismatch ({actual} != {archive.Sha1})."); }
        }
        if (sha256 != null)
        {
            var actual = Hashing.Sha256File(zipPath);
            if (!Hashing.Equal(actual, sha256)) { TryDelete(zipPath); throw new InvalidDataException($"SHA-256 mismatch ({actual} != {sha256})."); }
        }

        _log.Info("Extracting to " + destination);
        ExtractZip(zipPath, destination, stripTopFolder, filter);
        TryDelete(zipPath);
        _log.Success($"{archive.PackagePath} installed in {destination}");
    }

    /// <summary>Extracts a ZIP, optionally removing its single top-level folder, rejecting path traversal.</summary>
    public static void ExtractZip(string zipPath, string destination, bool stripTopFolder, Func<string, bool>? filter = null)
    {
        var root = Path.GetFullPath(destination);
        Directory.CreateDirectory(root);
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var e in zip.Entries)
        {
            var name = e.FullName.Replace('\\', '/');
            if (stripTopFolder)
            {
                var slash = name.IndexOf('/');
                if (slash < 0) continue;
                name = name[(slash + 1)..];
            }
            if (name.Length == 0 || name.EndsWith('/')) continue;
            if (filter != null && !filter(name)) continue;
            var target = Path.GetFullPath(Path.Combine(root, name));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException($"Archive entry escapes the destination: {e.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, overwrite: true);
            if (!OperatingSystem.IsWindows() && (e.ExternalAttributes >> 16 & 0x40) != 0) MakeExecutable(target);
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

/// <summary>Minimal reader of Google's SDK repository manifest (repository2-*.xml).</summary>
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

    /// <summary>Newest stable archive whose package path equals (or, ending with ';', starts with) <paramref name="pathOrPrefix"/>.</summary>
    public RemoteArchive? Latest(string pathOrPrefix, string hostOs) =>
        _archives
            .Where(a => pathOrPrefix.EndsWith(';') ? a.PackagePath.StartsWith(pathOrPrefix, StringComparison.Ordinal) : a.PackagePath == pathOrPrefix)
            .Where(a => _hostOs[a] == hostOs || _hostOs[a] == "any")
            .OrderByDescending(a => a.Revision)
            .FirstOrDefault();
}
