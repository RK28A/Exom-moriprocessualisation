using System.Security.Cryptography;
using NightClubPatcher.Core.Adb;
using NightClubPatcher.Core.Infrastructure;

namespace NightClubPatcher.Core.Recovery;

/// <summary>One expansion file of the game (path relative to /sdcard/Android/obb/&lt;pkg&gt;).</summary>
public sealed record ObbFile(string RelativePath, long Size, string? Md5);

public sealed record ObbSaveResult(bool Ok, bool DeviceHadObb, IReadOnlyList<ObbFile> Files, string Message);

/// <summary>
/// Game content that lives outside the APK: /sdcard/Android/obb/&lt;pkg&gt; (main.&lt;versionCode&gt;.&lt;pkg&gt;.obb,
/// Meta asset files). Unity loads Resources and every scene after the first from it, so the game is
/// stuck in its Loading scene without it. Android deletes the folder when the package is uninstalled,
/// which a signature change forces; NCMK saves it before and puts it back after.
///
/// This is game content only. Save data (Android/data, /data/data) is never touched.
/// Saved copies live in Backups/&lt;pkg&gt;/obb/ and are reused when identical (size + MD5).
/// </summary>
public sealed class ObbService
{
    private readonly KitPaths _paths;
    private readonly KitLog _log;

    public ObbService(KitPaths paths, KitLog log)
    {
        _paths = paths;
        _log = log;
    }

    public string LocalDir => Path.Combine(_paths.BackupRoot, "obb");
    public static string DeviceDir => GameConstants.DeviceObbDir;

    private string LocalFile(string rel) => Path.Combine(LocalDir, rel.Replace('/', Path.DirectorySeparatorChar));

    // ------------------------------------------------------------------ listing

    /// <summary>Files in the device OBB folder ("size|/full/path" via toybox find/stat).</summary>
    public List<ObbFile> ListDevice(AdbClient adb)
    {
        var r = adb.Shell($"find \"{DeviceDir}\" -type f -exec stat -c '%s|%n' {{}} + 2>/dev/null", TimeSpan.FromMinutes(2));
        return ParseListing(r.StdOut, DeviceDir);
    }

    public static List<ObbFile> ParseListing(string output, string root)
    {
        var prefix = root.TrimEnd('/') + "/";
        var files = new List<ObbFile>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var bar = line.IndexOf('|');
            if (bar <= 0 || !long.TryParse(line[..bar], out var size)) continue;
            var path = line[(bar + 1)..];
            if (path.StartsWith(prefix, StringComparison.Ordinal) && path.Length > prefix.Length)
                files.Add(new ObbFile(path[prefix.Length..], size, null));
        }
        return files;
    }

    /// <summary>"relative path -> md5" of the device OBB folder; empty when md5sum is unavailable.</summary>
    public static Dictionary<string, string> RemoteMd5(AdbClient adb)
    {
        var r = adb.Shell($"cd \"{DeviceDir}\" && find . -type f -exec md5sum {{}} + 2>/dev/null", TimeSpan.FromMinutes(20));
        return ParseMd5(r.StdOut);
    }

    public static Dictionary<string, string> ParseMd5(string output)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 35 || line[32] != ' ' || !line[..32].All(Uri.IsHexDigit)) continue;
            var path = line[33..].TrimStart(' ', '*');
            if (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
            if (path.Length > 0) map[path] = line[..32].ToLowerInvariant();
        }
        return map;
    }

    public static string LocalMd5(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(MD5.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>Saved OBB files for a game version (file names carry the versionCode: main.212.&lt;pkg&gt;.obb).</summary>
    public List<ObbFile> SavedFor(long versionCode)
    {
        if (!Directory.Exists(LocalDir)) return new List<ObbFile>();
        return Directory.EnumerateFiles(LocalDir, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".part", StringComparison.Ordinal))
            .Select(f => new ObbFile(Path.GetRelativePath(LocalDir, f).Replace(Path.DirectorySeparatorChar, '/'), new FileInfo(f).Length, null))
            .Where(f => Path.GetFileName(f.RelativePath).Contains($".{versionCode}.", StringComparison.Ordinal))
            .OrderBy(f => f.RelativePath, StringComparer.Ordinal)
            .ToList();
    }

    // ------------------------------------------------------------------ save

    /// <summary>
    /// Copies every file of the device OBB folder to Backups/&lt;pkg&gt;/obb/ and verifies it (size + MD5).
    /// Files already saved identically are not copied again. Ok=false when any file could not be saved.
    /// </summary>
    public ObbSaveResult Save(AdbClient adb)
    {
        var files = ListDevice(adb);
        if (files.Count == 0)
            return new ObbSaveResult(true, false, files, $"No OBB file on the headset ({DeviceDir} is empty or missing).");

        _log.Step($"Saving the game's OBB files ({files.Count}, {FormatSize(files.Sum(f => f.Size))})");
        _log.Info("Checking OBB checksums on the headset");
        var md5 = RemoteMd5(adb);
        Directory.CreateDirectory(LocalDir);
        var saved = new List<ObbFile>();
        var problems = new List<string>();
        foreach (var f in files)
        {
            md5.TryGetValue(f.RelativePath, out var remoteMd5);
            var local = LocalFile(f.RelativePath);
            if (File.Exists(local) && new FileInfo(local).Length == f.Size && (remoteMd5 == null || LocalMd5(local) == remoteMd5))
            {
                _log.Info($"  {f.RelativePath}: already saved ({FormatSize(f.Size)})");
                saved.Add(f with { Md5 = remoteMd5 });
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            var part = local + ".part";
            TryDelete(part);
            bool ok;
            using (Progress($"Saving {f.RelativePath}", () => File.Exists(part) ? new FileInfo(part).Length : 0, f.Size))
                ok = adb.Pull($"{DeviceDir}/{f.RelativePath}", part, TimeSpan.FromHours(2));
            var size = File.Exists(part) ? new FileInfo(part).Length : -1;
            if (!ok || size != f.Size || (remoteMd5 != null && LocalMd5(part) != remoteMd5))
            {
                problems.Add($"{f.RelativePath}: copy failed or differs from the headset (size {size} / {f.Size})");
                TryDelete(part);
                continue;
            }
            TryDelete(local);
            File.Move(part, local);
            _log.Info($"  {f.RelativePath}: saved and verified ({FormatSize(f.Size)}{(remoteMd5 != null ? ", MD5" : "")})");
            saved.Add(f with { Md5 = remoteMd5 });
        }
        foreach (var p in problems) _log.Error("  " + p);
        var okAll = problems.Count == 0;
        var msg = okAll
            ? $"{saved.Count} OBB file(s) saved in {LocalDir}."
            : "Some OBB files could not be saved:" + Environment.NewLine + string.Join(Environment.NewLine, problems);
        if (okAll) _log.Success(msg);
        return new ObbSaveResult(okAll, true, saved, msg);
    }

    // ------------------------------------------------------------------ restore

    /// <summary>
    /// Pushes saved OBB files back to /sdcard/Android/obb/&lt;pkg&gt;/ and verifies them on the headset
    /// (size, and MD5 when known). Returns false when any file is missing or differs.
    /// </summary>
    public bool Restore(AdbClient adb, IReadOnlyList<ObbFile> files)
    {
        if (files.Count == 0) return true;
        _log.Step($"Putting the game's OBB files back ({files.Count}, {FormatSize(files.Sum(f => f.Size))})");
        adb.Shell($"mkdir -p \"{DeviceDir}\"");
        var ok = true;
        foreach (var f in files)
        {
            var local = LocalFile(f.RelativePath);
            if (!File.Exists(local) || new FileInfo(local).Length != f.Size)
            {
                _log.Error($"  {f.RelativePath}: saved copy missing or changed ({local})");
                ok = false;
                continue;
            }
            var remote = $"{DeviceDir}/{f.RelativePath}";
            var dir = remote[..remote.LastIndexOf('/')];
            if (dir != DeviceDir) adb.Shell($"mkdir -p \"{dir}\"");
            using (Progress($"Copying {f.RelativePath} to the headset", () => RemoteSize(adb, remote), f.Size, TimeSpan.FromSeconds(3)))
                ok &= adb.Push(local, remote, TimeSpan.FromHours(2));
        }

        var onDevice = ListDevice(adb).ToDictionary(f => f.RelativePath, f => f.Size, StringComparer.Ordinal);
        var md5 = files.Any(f => f.Md5 != null) ? RemoteMd5(adb) : new Dictionary<string, string>();
        foreach (var f in files)
        {
            if (!onDevice.TryGetValue(f.RelativePath, out var size) || size != f.Size)
            {
                _log.Error($"  {f.RelativePath}: not on the headset with the right size");
                ok = false;
            }
            else if (f.Md5 != null && md5.TryGetValue(f.RelativePath, out var h) && h != f.Md5)
            {
                _log.Error($"  {f.RelativePath}: content differs on the headset (MD5)");
                ok = false;
            }
            else
            {
                _log.Info($"  {f.RelativePath}: on the headset and verified");
            }
        }
        if (ok) _log.Success("OBB files restored.");
        return ok;
    }

    // ------------------------------------------------------------------ helpers

    private static long RemoteSize(AdbClient adb, string remote)
    {
        var r = adb.Shell($"stat -c %s \"{remote}\" 2>/dev/null", TimeSpan.FromSeconds(10));
        return long.TryParse(r.StdOut.Trim(), out var n) ? n : 0;
    }

    private IDisposable Progress(string label, Func<long> current, long total, TimeSpan? every = null)
    {
        var key = "obb:" + label;
        var busy = 0;
        var timer = new Timer(_ =>
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            try
            {
                var done = Math.Min(current(), total);
                var frac = total > 0 ? (double)done / total : 0;
                _log.ReportProgress(key, $"{label}: {KitLog.ProgressBar(frac)}  {FormatSize(done)} / {FormatSize(total)}", frac);
            }
            catch
            {
                // file being written; next tick
            }
            finally
            {
                Interlocked.Exchange(ref busy, 0);
            }
        }, null, TimeSpan.Zero, every ?? TimeSpan.FromSeconds(1));
        return new Scope(() =>
        {
            timer.Dispose();
            _log.EndProgress(key);
        });
    }

    private sealed class Scope(Action end) : IDisposable
    {
        private Action? _end = end;
        public void Dispose() { _end?.Invoke(); _end = null; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* ignore */ }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:0.#} MB",
        _ => $"{bytes / 1073741824.0:0.##} GB",
    };
}
