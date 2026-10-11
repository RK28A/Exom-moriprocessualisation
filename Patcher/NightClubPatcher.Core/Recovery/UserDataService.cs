using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using NightClubPatcher.Core.Adb;
using NightClubPatcher.Core.Infrastructure;

namespace NightClubPatcher.Core.Recovery;

/// <summary>One file found on the headset (path relative to the backed-up folder).</summary>
public sealed record RemoteFile(string RelativePath, long Size);

/// <summary>Result of backing up one device folder (Android/data/&lt;pkg&gt; or Android/obb/&lt;pkg&gt;).</summary>
public sealed class FolderBackupReport
{
    public required string Label { get; init; }           // "Android/data"
    public required string RemotePath { get; init; }
    public required string LocalPath { get; init; }        // <backup>/data/<pkg>
    public bool Exists { get; set; }
    public bool InventoryComplete { get; set; }            // false when listing hit "Permission denied"
    public List<RemoteFile> Inventory { get; } = new();
    public List<string> InventoryErrors { get; } = new();
    public List<string> Saved { get; } = new();
    public List<string> Missing { get; } = new();
    public List<string> Attempts { get; } = new();         // "adb pull: 3/12 files" ...

    /// <summary>Files the headset refuses to give to adb (Permission denied), relative paths.</summary>
    public List<string> Unreadable { get; } = new();

    /// <summary>Protected files that are not game data (crash dumps, caches, logs): left out of the backup.</summary>
    public List<string> Skipped { get; } = new();
    public string? Method { get; set; }

    /// <summary>True: every saved file's MD5 matches the headset; false: MD5 could not be checked (sizes only).</summary>
    public bool ContentVerified { get; set; }

    /// <summary>Files present in the local copy (useful when the device folder could not be listed).</summary>
    public int LocalFileCount { get; set; }

    public long TotalBytes => Inventory.Sum(f => f.Size);

    /// <summary>Nothing on the device, or every listed file saved with the right size.</summary>
    public bool Complete => !Exists || (InventoryComplete && Missing.Count == 0 && Saved.Count == Inventory.Count);

    public string Summary
    {
        get
        {
            if (!Exists) return $"{Label}: folder does not exist on the headset (nothing to lose).";
            if (Inventory.Count == 0 && InventoryComplete) return $"{Label}: folder is empty (nothing to lose).";
            var head = $"{Label}: {Saved.Count}/{Inventory.Count} files saved ({FormatSize(TotalBytes)})";
            var skipped = Skipped.Count == 0 ? "" :
                $" {Skipped.Count} protected non-game file(s) skipped ({string.Join(", ", Skipped.Take(3))}{(Skipped.Count > 3 ? ", ..." : "")}).";
            if (Complete) return head + $" - complete, verified ({(ContentVerified ? "size + MD5" : "size")}, method: {Method})." + skipped;
            var sb = new StringBuilder(head + " - INCOMPLETE." + skipped);
            if (!InventoryComplete)
                sb.Append(" The folder could not be fully listed (" + string.Join("; ", InventoryErrors.Take(3)) +
                          $"); {LocalFileCount} file(s) were copied but cannot be verified.");
            foreach (var m in Missing.Take(15)) sb.Append(Environment.NewLine + "    missing: " + m);
            if (Missing.Count > 15) sb.Append(Environment.NewLine + $"    ... and {Missing.Count - 15} more");
            return sb.ToString();
        }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:0.#} MB",
        _ => $"{bytes / 1073741824.0:0.##} GB",
    };
}

public sealed class UserDataBackup
{
    public required string Directory { get; init; }
    public required FolderBackupReport ExternalData { get; init; }
    public required FolderBackupReport Obb { get; init; }
    public bool HasInternalData { get; set; }
    public List<string> Notes { get; } = new();

    /// <summary>The user chose to skip the slow file-by-file copy: what was saved is kept but never restored.</summary>
    public bool SkippedByUser { get; set; }

    public bool HasExternalData => ExternalData.Exists && ExternalData.Saved.Count > 0;
    public bool HasObb => Obb.Exists && Obb.Saved.Count > 0;

    /// <summary>Everything adb can reach was saved and verified.</summary>
    public bool AccessibleDataComplete => ExternalData.Complete && Obb.Complete;

    public string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine(ExternalData.Summary);
        sb.AppendLine(Obb.Summary);
        sb.AppendLine(HasInternalData
            ? "Internal data (/data/data): saved through run-as (debuggable build)."
            : "Internal data (/data/data): NOT accessible without root on store builds. Anything the game keeps there " +
              "(e.g. Unity PlayerPrefs) is lost by an uninstall unless Meta cloud backup restores it.");
        sb.Append("Backup folder: " + Directory);
        return sb.ToString();
    }
}

/// <summary>
/// Backs up / restores the user data adb can reach, trying several methods because
/// Android/data is often only partially readable over adb on recent Horizon OS versions:
///
///   1. adb pull /sdcard/Android/data/&lt;pkg&gt;
///   2. adb pull /storage/emulated/0/Android/data/&lt;pkg&gt;   (same storage, different FUSE path)
///   3. tar stream:  adb exec-out "tar -cf - -C /sdcard/Android/data &lt;pkg&gt;"
///   4. staging:     cp -r to /sdcard/Download/NCMK-staging, adb pull, delete staging
///   5. per file:    adb pull of every file still missing (keeps what can be read)
///
/// An inventory (path + size of every file) is taken first; after each method every file is
/// verified locally (exists, same size) and the next method only runs while files are missing.
/// Nothing on the headset is ever deleted here (only the temporary staging copy).
/// </summary>
public sealed class UserDataService
{
    public const string StagingDir = "/sdcard/Download/NCMK-staging";

    private readonly KitPaths _paths;
    private readonly KitLog _log;

    public UserDataService(KitPaths paths, KitLog log)
    {
        _paths = paths;
        _log = log;
    }

    // ------------------------------------------------------------------ inventory

    /// <summary>Lists every file (with size) under a device folder.</summary>
    public FolderBackupReport Inventory(AdbClient adb, string label, string remote, string localParent)
    {
        var pkgName = remote.TrimEnd('/')[(remote.TrimEnd('/').LastIndexOf('/') + 1)..];
        var report = new FolderBackupReport { Label = label, RemotePath = remote, LocalPath = Path.Combine(localParent, pkgName) };
        report.Exists = adb.RemoteExists(remote);
        if (!report.Exists) return report;

        // toybox: one "size|path" line per file; errors (Permission denied...) go to stderr.
        var r = adb.Shell($"find \"{remote}\" -type f -exec stat -c '%s|%n' {{}} + ; echo __NCMK_END__:$?", TimeSpan.FromMinutes(5));
        var (files, errors) = ParseInventory(r.StdOut + Environment.NewLine + r.StdErr, remote);
        report.Inventory.AddRange(files);
        report.InventoryErrors.AddRange(errors);
        report.InventoryComplete = errors.Count == 0 && r.StdOut.Contains("__NCMK_END__:0");
        if (!report.InventoryComplete && errors.Count == 0) report.InventoryErrors.Add("find did not complete");

        // Files adb is not allowed to read (e.g. crash tombstones written with owner-only permissions):
        // a folder "adb pull" stops at the first one, so they are detected up front.
        var nr = adb.Shell($"find \"{remote}\" -type f -exec sh -c 'for f; do [ -r \"$f\" ] || echo \"NOREAD|$f\"; done' sh {{}} +", TimeSpan.FromMinutes(5));
        var root = remote.TrimEnd('/') + "/";
        foreach (var line in nr.StdOut.Split('\n').Select(l => l.TrimEnd('\r')))
            if (line.StartsWith("NOREAD|" + root, StringComparison.Ordinal)) report.Unreadable.Add(line[("NOREAD|" + root).Length..]);
        foreach (var rel in report.Unreadable.Where(IsNonEssential).ToList())
        {
            report.Inventory.RemoveAll(f => f.RelativePath == rel);
            report.Skipped.Add(rel);
        }
        return report;
    }

    /// <summary>Crash dumps, caches and logs: not game data, never worth blocking a backup for.</summary>
    public static bool IsNonEssential(string relativePath)
    {
        var name = relativePath[(relativePath.LastIndexOf('/') + 1)..];
        return !IsRestorable(relativePath) ||
               name.StartsWith("tombstone", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
               relativePath.Contains("crash", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Parses "size|/full/path" lines; anything else non-empty is an error line.</summary>
    public static (List<RemoteFile> files, List<string> errors) ParseInventory(string output, string remoteRoot)
    {
        var root = remoteRoot.TrimEnd('/') + "/";
        var files = new List<RemoteFile>();
        var errors = new List<string>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith("__NCMK_END__", StringComparison.Ordinal)) continue;
            var bar = line.IndexOf('|');
            if (bar > 0 && long.TryParse(line[..bar], out var size) && line[(bar + 1)..].StartsWith(root, StringComparison.Ordinal))
                files.Add(new RemoteFile(line[(bar + 1 + root.Length)..], size));
            else
                errors.Add(line.Trim());
        }
        return (files, errors);
    }

    /// <summary>Compares the inventory with what exists locally; fills Saved / Missing.</summary>
    public static void VerifyLocal(FolderBackupReport report)
    {
        report.LocalFileCount = Directory.Exists(report.LocalPath)
            ? Directory.EnumerateFiles(report.LocalPath, "*", SearchOption.AllDirectories).Count()
            : 0;
        report.Saved.Clear();
        report.Missing.Clear();
        foreach (var f in report.Inventory)
        {
            var local = Path.Combine(report.LocalPath, f.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var fi = new FileInfo(local);
            if (fi.Exists && fi.Length == f.Size) report.Saved.Add(f.RelativePath);
            else report.Missing.Add(fi.Exists ? $"{f.RelativePath} (size {fi.Length} != {f.Size})" : f.RelativePath);
        }
    }

    // ------------------------------------------------------------------ backup

    /// <summary>Lists the game's accessible data without copying anything (for the UI / diagnostics).</summary>
    public string DescribeOnDevice(AdbClient adb)
    {
        var sb = new StringBuilder();
        foreach (var (label, remote) in new[] { ("Android/data", GameConstants.DeviceExternalData), ("Android/obb", GameConstants.DeviceObbDir) })
        {
            var inv = Inventory(adb, label, remote, Path.GetTempPath());
            if (!inv.Exists) { sb.AppendLine($"{remote}: does not exist"); continue; }
            sb.AppendLine($"{remote}: {inv.Inventory.Count} files, {FolderBackupReport.FormatSize(inv.TotalBytes)}" +
                          (inv.InventoryComplete ? "" : "  (listing incomplete: " + string.Join("; ", inv.InventoryErrors.Take(3)) + ")"));
            foreach (var f in inv.Inventory.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
                sb.AppendLine($"  {FolderBackupReport.FormatSize(f.Size),10}  {f.RelativePath}");
        }
        return sb.ToString();
    }

    public UserDataBackup Backup(AdbClient adb, IUserInteraction? ui = null)
    {
        var dir = Path.Combine(_paths.UserDataBackupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(dir);
        _log.Step("Backing up game data");
        _log.Info("Backup folder: " + dir);

        var skipped = false;
        var external = BackupFolder(adb, ui, "Android/data", GameConstants.DeviceExternalData, "/sdcard/Android/data",
            "/storage/emulated/0/Android/data", Path.Combine(dir, "data"), ref skipped);
        var obb = BackupFolder(adb, ui, "Android/obb", GameConstants.DeviceObbDir, "/sdcard/Android/obb",
            "/storage/emulated/0/Android/obb", Path.Combine(dir, "obb"), ref skipped);

        var backup = new UserDataBackup { Directory = dir, ExternalData = external, Obb = obb, SkippedByUser = skipped };
        if (skipped) backup.Notes.Add("The slow file-by-file copy was skipped by the user: the partial copy is kept but never restored.");

        // Internal data is only reachable for debuggable packages.
        var probe = adb.Shell($"run-as {GameConstants.PackageName} ls 2>&1");
        if (probe.Ok && !probe.StdOut.Contains("not debuggable") && !probe.StdOut.Contains("unknown package") && !probe.StdOut.Contains("run-as:"))
        {
            var (ok, err) = adb.ExecOutToFile($"run-as {GameConstants.PackageName} tar cf - .", Path.Combine(dir, "internal.tar"), TimeSpan.FromMinutes(30));
            backup.HasInternalData = ok && new FileInfo(Path.Combine(dir, "internal.tar")).Length > 0;
            backup.Notes.Add(backup.HasInternalData ? "Saved internal data to internal.tar (run-as)." : "run-as backup failed: " + err);
        }
        else
        {
            backup.Notes.Add("Internal app data (/data/data) is not accessible without root (store builds are not debuggable).");
        }

        WriteReport(backup);
        foreach (var line in backup.Describe().Split(Environment.NewLine)) _log.Info("  " + line);
        if (backup.AccessibleDataComplete) _log.Success("Accessible game data saved and verified.");
        else _log.Warn("Game data backup is INCOMPLETE - see the report above.");
        return backup;
    }

    private FolderBackupReport BackupFolder(AdbClient adb, IUserInteraction? ui, string label, string remote, string parentSdcard,
        string parentStorage, string localParent, ref bool skippedByUser)
    {
        _log.Step($"Listing {label} on the headset");
        var report = Inventory(adb, label, remote, localParent);
        if (!report.Exists)
        {
            _log.Info($"  {remote} does not exist.");
            return report;
        }
        _log.Info($"  {report.Inventory.Count} files, {FolderBackupReport.FormatSize(report.TotalBytes)}" +
                  (report.InventoryComplete ? "" : "  (listing incomplete)"));
        foreach (var f in report.Inventory.Take(40)) _log.Info($"    {FolderBackupReport.FormatSize(f.Size),10}  {f.RelativePath}");
        if (report.Inventory.Count > 40) _log.Info($"    ... and {report.Inventory.Count - 40} more (see inventory.txt)");
        foreach (var e in report.InventoryErrors.Take(10)) _log.Warn("    " + e);
        foreach (var s in report.Skipped)
            _log.Info($"    skipped (protected by Android, not game data): {s}");
        foreach (var u in report.Unreadable.Except(report.Skipped))
            _log.Warn($"    protected by Android (Permission denied): {u}");
        Directory.CreateDirectory(localParent);

        var pkg = Path.GetFileName(report.LocalPath);
        // A folder "adb pull" aborts at the first protected file, so it is pointless when there are any.
        var folderPullUseful = report.Unreadable.Count == 0;
        var methods = new List<(string name, Func<bool> run)>();
        if (folderPullUseful)
        {
            methods.Add(("adb pull", () => adb.Pull(remote, localParent, TimeSpan.FromHours(1))));
            methods.Add(("adb pull via /storage/emulated/0", () => adb.Pull($"{parentStorage}/{pkg}", localParent, TimeSpan.FromHours(1))));
        }
        else
        {
            _log.Info($"  {report.Unreadable.Count} protected file(s): skipping the folder pull (it stops at the first one), using tar.");
        }
        methods.Add(("tar stream (adb exec-out)", () => TarStream(adb, parentSdcard, pkg, localParent)));
        methods.Add(("staging copy in /sdcard/Download", () => StagingCopy(adb, remote, pkg, localParent)));

        if (RunMethods(adb, report, label, localParent, methods)) return Finish(adb, report);
        if (report.Inventory.Count == 0 && report.InventoryComplete) return report;

        // Still missing: the last method copies file by file (with a "cat" fallback per file). It is slow
        // on big folders, so the user decides.
        if (skippedByUser)
        {
            _log.Warn($"  {label}: slow copy skipped (game data backup skipped by the user).");
            return report;
        }
        if (ui != null)
        {
            var missing = report.Missing.Count;
            var choice = ui.Choose($"{label}: {missing} file(s) still missing",
                $"The fast copy methods saved {report.Saved.Count}/{report.Inventory.Count} files of {label}." + Environment.NewLine +
                string.Join(Environment.NewLine, report.Missing.Take(8).Select(m => "  - " + m)) +
                (missing > 8 ? Environment.NewLine + $"  ... and {missing - 8} more" : "") + Environment.NewLine + Environment.NewLine +
                "Last method: copy every missing file one by one. It is slower (about 5-20 min)." + Environment.NewLine +
                "Skip: continue without the game data backup (the partial copy is kept on the PC but never restored).",
                new[] { "Skip game data backup", "Use the slow method (5-20 min)" });
            if (choice != 1)
            {
                skippedByUser = true;
                _log.Warn($"  {label}: slow copy skipped by the user ({report.Saved.Count}/{report.Inventory.Count} files saved).");
                return report;
            }
        }
        RunMethods(adb, report, label, localParent, new List<(string, Func<bool>)> { ("file by file (slow)", () => FileByFile(adb, report)) });
        return Finish(adb, report);
    }

    private FolderBackupReport Finish(AdbClient adb, FolderBackupReport report)
    {
        if (report.Saved.Count > 0) VerifyBackupContent(adb, report);
        return report;
    }

    /// <summary>Runs copy methods until nothing is missing; true when the folder is complete.</summary>
    private bool RunMethods(AdbClient adb, FolderBackupReport report, string label, string localParent, List<(string name, Func<bool> run)> methods)
    {
        foreach (var (name, run) in methods)
        {
            if (report.Inventory.Count == 0 && report.InventoryComplete) { report.Method = "nothing to copy"; return true; }
            _log.Step($"Saving {label}: {name}");
            bool ok;
            using (StartProgress(report, label, localParent))
            {
                try { ok = run(); }
                catch (Exception ex) { ok = false; _log.Warn($"  {name} failed: {ex.Message}"); }
            }
            VerifyLocal(report);
            report.Attempts.Add($"{name}: {(ok ? "ok" : "errors")}, {report.Saved.Count}/{report.Inventory.Count} files verified");
            _log.Info("  " + report.Attempts[^1]);
            if (report.Missing.Count == 0 && report.Inventory.Count > 0)
            {
                report.Method = name;
                if (report.InventoryComplete) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Live progress while a method copies: files present locally and bytes written (the tar stream
    /// grows as one file, so its size counts too), refreshed every second.
    /// </summary>
    private IDisposable StartProgress(FolderBackupReport report, string label, string localParent)
    {
        var key = "backup:" + label;
        var total = Math.Max(1, report.TotalBytes);
        var count = report.Inventory.Count;
        void Tick()
        {
            try
            {
                long bytes = 0;
                var files = 0;
                if (Directory.Exists(localParent))
                    foreach (var f in new DirectoryInfo(localParent).EnumerateFiles("*", SearchOption.AllDirectories))
                    {
                        bytes += f.Length;
                        if (!f.Name.EndsWith(".tar", StringComparison.Ordinal)) files++;
                    }
                var frac = Math.Min(1.0, (double)bytes / total);
                _log.ReportProgress(key, $"{label}: {KitLog.ProgressBar(frac)}  {Math.Min(files, count)}/{count} files · " +
                                         $"{FolderBackupReport.FormatSize(Math.Min(bytes, total))} / {FolderBackupReport.FormatSize(total)}", frac);
            }
            catch
            {
                // files are being written; next tick
            }
        }
        var timer = new Timer(_ => Tick(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        return new ProgressScope(() =>
        {
            timer.Dispose();
            _log.EndProgress(key);
        });
    }

    private sealed class ProgressScope(Action end) : IDisposable
    {
        private Action? _end = end;
        public void Dispose() { _end?.Invoke(); _end = null; }
    }

    /// <summary>
    /// Compares the MD5 of every saved file with the headset. A size match alone does not prove the
    /// copy is right (e.g. two names that differ only by case collapse into one file on Windows).
    /// Mismatching files are pulled again one by one; whatever still differs is reported as missing.
    /// </summary>
    private void VerifyBackupContent(AdbClient adb, FolderBackupReport report)
    {
        var remote = RemoteMd5(adb, report.RemotePath);
        if (remote == null)
        {
            _log.Warn($"  {report.Label}: md5sum is not available on the headset; files were checked by size only.");
            return;
        }
        List<string> Bad() => report.Saved.Where(rel =>
            !remote.TryGetValue(rel, out var h) || !string.Equals(h, LocalMd5(LocalFile(report, rel)), StringComparison.OrdinalIgnoreCase)).ToList();
        var bad = Bad();
        if (bad.Count > 0)
        {
            _log.Warn($"  {report.Label}: {bad.Count} file(s) differ from the headset (MD5), pulling them again");
            foreach (var rel in bad)
            {
                var local = LocalFile(report, rel);
                try { File.Delete(local); } catch { /* re-pulled below */ }
                adb.Pull($"{report.RemotePath}/{rel}", local, TimeSpan.FromMinutes(30));
            }
            VerifyLocal(report);
            bad = Bad();
        }
        foreach (var rel in bad)
        {
            report.Saved.Remove(rel);
            report.Missing.Add(rel + " (content differs from the headset)");
        }
        report.ContentVerified = bad.Count == 0;
        _log.Info(bad.Count == 0
            ? $"  {report.Label}: {report.Saved.Count} files verified by MD5."
            : $"  {report.Label}: {bad.Count} file(s) could not be copied identically.");
    }

    private static string LocalFile(FolderBackupReport report, string rel) =>
        Path.Combine(report.LocalPath, rel.Replace('/', Path.DirectorySeparatorChar));

    public static string? LocalMd5(string path)
    {
        if (!File.Exists(path)) return null;
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(MD5.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>"relative path -> md5" for every file under a device folder; null when md5sum is unavailable.</summary>
    public static Dictionary<string, string>? RemoteMd5(AdbClient adb, string remote)
    {
        var r = adb.Shell($"cd \"{remote}\" && find . -type f -exec md5sum {{}} + ; echo __NCMK_END__:$?", TimeSpan.FromMinutes(30));
        return ParseMd5(r.StdOut);
    }

    /// <summary>Parses md5sum lines ("hash  ./path"); null when no line could be parsed.</summary>
    public static Dictionary<string, string>? ParseMd5(string output)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 35 || line[32] != ' ') continue;
            var hash = line[..32];
            if (!hash.All(Uri.IsHexDigit)) continue;
            var path = line[33..].TrimStart(' ', '*');
            if (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
            if (path.Length > 0) map[path] = hash.ToLowerInvariant();
        }
        return map.Count == 0 ? null : map;
    }

    private bool TarStream(AdbClient adb, string parent, string pkg, string localParent)
    {
        var tar = Path.Combine(localParent, pkg + ".tar");
        var (ok, err) = adb.ExecOutToFile($"tar -cf - -C \"{parent}\" \"{pkg}\" 2>/dev/null", tar, TimeSpan.FromHours(1));
        try
        {
            if (!File.Exists(tar) || new FileInfo(tar).Length == 0)
            {
                if (!string.IsNullOrEmpty(err)) _log.Warn("  tar: " + err);
                return false;
            }
            ExtractTarSafely(tar, localParent);
            return ok;
        }
        finally
        {
            try { File.Delete(tar); } catch { /* ignore */ }
        }
    }

    private bool StagingCopy(AdbClient adb, string remote, string pkg, string localParent)
    {
        var staging = $"{StagingDir}/{DateTime.Now:yyyyMMddHHmmss}";
        try
        {
            var cp = adb.Shell($"mkdir -p \"{staging}\" && cp -r \"{remote}\" \"{staging}/\" 2>&1; echo __NCMK_END__:$?", TimeSpan.FromHours(1));
            if (!cp.StdOut.Contains("__NCMK_END__:0"))
                _log.Warn("  cp: " + cp.StdOut.Replace("__NCMK_END__", "exit").Trim());
            return adb.Pull($"{staging}/{pkg}", localParent, TimeSpan.FromHours(1));
        }
        finally
        {
            adb.Shell($"rm -rf \"{staging}\"");
        }
    }

    private bool FileByFile(AdbClient adb, FolderBackupReport report)
    {
        var allOk = true;
        foreach (var missing in report.Inventory.Where(f => !report.Saved.Contains(f.RelativePath)).ToList())
        {
            var local = Path.Combine(report.LocalPath, missing.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            var remotePath = $"{report.RemotePath}/{missing.RelativePath}";
            if (adb.Pull(remotePath, local, TimeSpan.FromMinutes(30))) continue;
            // Second chance through the shell (different code path than the adb sync service).
            var (ok, _) = adb.ExecOutToFile($"cat \"{remotePath}\"", local, TimeSpan.FromMinutes(30));
            if (!ok || new FileInfo(local).Length != missing.Size)
            {
                try { File.Delete(local); } catch { /* reported as missing */ }
                allOk = false;
            }
        }
        return allOk;
    }

    /// <summary>Extracts a tar produced on the device; rejects absolute paths and "..".</summary>
    public static void ExtractTarSafely(string tarPath, string destination)
    {
        var root = Path.GetFullPath(destination);
        using var fs = File.OpenRead(tarPath);
        using var reader = new TarReader(fs);
        while (reader.GetNextEntry() is { } e)
        {
            var name = e.Name.Replace('\\', '/').TrimStart('/');
            if (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
            if (name.Length == 0) continue;
            var target = Path.GetFullPath(Path.Combine(root, name));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException("Unsafe path in device tar: " + e.Name);
            if (e.EntryType == TarEntryType.Directory) { Directory.CreateDirectory(target); continue; }
            if (e.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, overwrite: true);
        }
    }

    private static void WriteReport(UserDataBackup b)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"NCMK game data backup - {DateTimeOffset.Now:O}");
        sb.AppendLine(b.Describe());
        foreach (var r in new[] { b.ExternalData, b.Obb })
        {
            sb.AppendLine();
            sb.AppendLine($"## {r.Label}  ({r.RemotePath})");
            foreach (var a in r.Attempts) sb.AppendLine("attempt: " + a);
            foreach (var e in r.InventoryErrors) sb.AppendLine("listing error: " + e);
            foreach (var f in r.Inventory.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
                sb.AppendLine($"{(r.Saved.Contains(f.RelativePath) ? "OK     " : "MISSING")} {f.Size,12}  {f.RelativePath}");
        }
        foreach (var n in b.Notes) sb.AppendLine("note: " + n);
        File.WriteAllText(Path.Combine(b.Directory, "inventory.txt"), sb.ToString());
        Json.Write(Path.Combine(b.Directory, "userdata.json"), new
        {
            createdAt = DateTimeOffset.Now,
            externalComplete = b.ExternalData.Complete,
            externalFiles = b.ExternalData.Inventory.Count,
            externalSaved = b.ExternalData.Saved.Count,
            externalMethod = b.ExternalData.Method,
            obbComplete = b.Obb.Complete,
            obbFiles = b.Obb.Inventory.Count,
            obbSaved = b.Obb.Saved.Count,
            internalData = b.HasInternalData,
            notes = b.Notes,
        });
    }

    // ------------------------------------------------------------------ restore

    /// <summary>
    /// Paths under Android/data/&lt;pkg&gt; that are never restored: caches the game and Unity rebuild on
    /// first launch (restoring a cache made by another build can crash the game), and NCMK's own
    /// binaries, logs and safe-mode flag (the patcher pushes the current ones).
    /// </summary>
    public static readonly string[] NotRestoredPrefixes =
    {
        "cache/",
        "code_cache/",
        "files/il2cpp/",
        "files/UnityCache/",
        "files/NCMKMods/Mods/",
        "files/NCMKMods/Logs/",
        "files/NCMKMods/disable_mods",
        "files/NightClubMods/",
    };

    public static bool IsRestorable(string relativePath) =>
        !NotRestoredPrefixes.Any(p => relativePath.StartsWith(p, StringComparison.OrdinalIgnoreCase)) &&
        !relativePath.EndsWith("pso_cache.bin", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Puts the verified files of a backup back on the headset and checks them (size + MD5).
    /// Only files whose copy was verified during the backup are pushed: partial or unverified files
    /// never reach the headset. Never throws; returns false when something could not be restored.
    /// </summary>
    public bool Restore(AdbClient adb, UserDataBackup backup)
    {
        var ok = true;
        _log.Step("Restoring game data");
        foreach (var (report, parent, isData) in new[] { (backup.ExternalData, "/sdcard/Android/data", true), (backup.Obb, "/sdcard/Android/obb", false) })
        {
            if (report.Saved.Count == 0 || !Directory.Exists(report.LocalPath)) continue;
            try
            {
                ok &= RestoreFolder(adb, report, parent, isData);
            }
            catch (Exception ex)
            {
                ok = false;
                _log.Error($"  {report.Label}: restore failed: {ex.Message}");
            }
        }
        if (backup.HasInternalData)
            _log.Warn("Internal data was backed up to internal.tar but is not restored automatically (requires a debuggable build).");
        if (ok) _log.Success("Game data restored and verified.");
        else _log.Error("Some game data could not be restored. Your backup is kept in " + backup.Directory);
        return ok;
    }

    private bool RestoreFolder(AdbClient adb, FolderBackupReport report, string parent, bool applyExclusions)
    {
        var pkg = Path.GetFileName(report.LocalPath);
        var files = report.Inventory
            .Where(f => report.Saved.Contains(f.RelativePath) && (!applyExclusions || IsRestorable(f.RelativePath)))
            .ToList();
        var skipped = report.Saved.Count - files.Count;
        if (skipped > 0) _log.Info($"  {report.Label}: {skipped} cache / NCMK file(s) not restored (rebuilt by the game and the patcher).");

        // The local copy must still be exactly what was verified.
        var ok = true;
        foreach (var f in files.ToList())
        {
            var fi = new FileInfo(LocalFile(report, f.RelativePath));
            if (fi.Exists && fi.Length == f.Size) continue;
            _log.Warn($"  {report.Label}: local copy of {f.RelativePath} changed since the backup; not restored.");
            files.Remove(f);
            ok = false;
        }
        if (files.Count == 0) return ok;

        // Push a folder that holds exactly the files to restore (no partial leftovers, no caches).
        var wanted = new HashSet<string>(files.Select(f => f.RelativePath), StringComparer.Ordinal);
        var present = Directory.EnumerateFiles(report.LocalPath, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(report.LocalPath, p).Replace(Path.DirectorySeparatorChar, '/')).ToHashSet(StringComparer.Ordinal);
        string? staging = null;
        var source = report.LocalPath;
        if (!present.SetEquals(wanted))
        {
            staging = Path.Combine(Path.GetDirectoryName(report.LocalPath)!, ".restore-" + Guid.NewGuid().ToString("N")[..8]);
            source = Path.Combine(staging, pkg);
            foreach (var f in files)
            {
                var dst = Path.Combine(source, f.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(LocalFile(report, f.RelativePath), dst);
            }
        }

        try
        {
            adb.Shell($"mkdir -p \"{parent}\"");
            adb.Push(source, parent + "/", TimeSpan.FromHours(1));
            if (RemoteMatches(adb, report, files)) return ok;

            // Fallback: upload a tar (GNU format, no pax headers: toybox extracts it as-is) and extract it on the device.
            _log.Warn($"  {report.Label}: adb push incomplete, trying a tar upload");
            var tar = Path.Combine(Path.GetTempPath(), $"ncmk-restore-{Guid.NewGuid():N}.tar");
            try
            {
                WriteRestoreTar(tar, report, pkg, files);
                var remoteTar = $"/data/local/tmp/{Path.GetFileName(tar)}";
                if (adb.Push(tar, remoteTar))
                    adb.Shell($"tar -xf \"{remoteTar}\" -C \"{parent}\"; rm -f \"{remoteTar}\"", TimeSpan.FromHours(1));
            }
            finally
            {
                try { File.Delete(tar); } catch { /* ignore */ }
            }
            return RemoteMatches(adb, report, files) && ok;
        }
        finally
        {
            if (staging != null) try { Directory.Delete(staging, true); } catch { /* ignore */ }
        }
    }

    /// <summary>Tar of the given files under "&lt;pkg&gt;/", GNU format, regular files and folders only.</summary>
    public static void WriteRestoreTar(string tarPath, FolderBackupReport report, string pkg, IEnumerable<RemoteFile> files)
    {
        using var fs = File.Create(tarPath);
        using var w = new TarWriter(fs, TarEntryFormat.Gnu, leaveOpen: false);
        var dirs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            var parts = f.RelativePath.Split('/');
            for (var i = 1; i < parts.Length; i++)
            {
                var d = pkg + "/" + string.Join('/', parts.Take(i)) + "/";
                if (dirs.Add(d)) w.WriteEntry(new GnuTarEntry(TarEntryType.Directory, d) { Mode = (UnixFileMode)0b111_111_101 });
            }
            var entry = new GnuTarEntry(TarEntryType.RegularFile, pkg + "/" + f.RelativePath)
            {
                Mode = (UnixFileMode)0b110_110_100,
                DataStream = File.OpenRead(LocalFile(report, f.RelativePath)),
            };
            using (entry.DataStream) w.WriteEntry(entry);
        }
    }

    /// <summary>Every restored file is on the headset with the right size and (when md5sum exists) the same MD5.</summary>
    private bool RemoteMatches(AdbClient adb, FolderBackupReport report, IReadOnlyList<RemoteFile> files)
    {
        var now = Inventory(adb, report.Label, report.RemotePath, Path.GetTempPath());
        var onDevice = now.Inventory.ToDictionary(f => f.RelativePath, f => f.Size, StringComparer.Ordinal);
        var bad = files.Where(f => !onDevice.TryGetValue(f.RelativePath, out var size) || size != f.Size).Select(f => f.RelativePath).ToList();
        var md5Checked = false;
        if (bad.Count == 0)
        {
            var remote = RemoteMd5(adb, report.RemotePath);
            if (remote != null)
            {
                md5Checked = true;
                bad = files.Where(f => !remote.TryGetValue(f.RelativePath, out var h) ||
                                       !string.Equals(h, LocalMd5(LocalFile(report, f.RelativePath)), StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.RelativePath + " (content differs)").ToList();
            }
        }
        if (bad.Count == 0) _log.Info($"  {report.Label}: {files.Count} files restored and verified ({(md5Checked ? "size + MD5" : "size")}).");
        else foreach (var m in bad.Take(10)) _log.Warn($"  {report.Label}: not restored: {m}");
        return bad.Count == 0;
    }
}
