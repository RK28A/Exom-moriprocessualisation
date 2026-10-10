using System.Formats.Tar;
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
    public string? Method { get; set; }

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
            if (Complete) return head + $" - complete, verified (method: {Method}).";
            var sb = new StringBuilder(head + " - INCOMPLETE.");
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
///   4. staging:     cp -r to /sdcard/Download/NightClubModKit-staging, adb pull, delete staging
///   5. per file:    adb pull of every file still missing (keeps what can be read)
///
/// An inventory (path + size of every file) is taken first; after each method every file is
/// verified locally (exists, same size) and the next method only runs while files are missing.
/// Nothing on the headset is ever deleted here (only the temporary staging copy).
/// </summary>
public sealed class UserDataService
{
    public const string StagingDir = "/sdcard/Download/NightClubModKit-staging";

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
        var r = adb.Shell($"find \"{remote}\" -type f -exec stat -c '%s|%n' {{}} + ; echo __NCS_END__:$?", TimeSpan.FromMinutes(5));
        var (files, errors) = ParseInventory(r.StdOut + Environment.NewLine + r.StdErr, remote);
        report.Inventory.AddRange(files);
        report.InventoryErrors.AddRange(errors);
        report.InventoryComplete = errors.Count == 0 && r.StdOut.Contains("__NCS_END__:0");
        if (!report.InventoryComplete && errors.Count == 0) report.InventoryErrors.Add("find did not complete");
        return report;
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
            if (line.Length == 0 || line.StartsWith("__NCS_END__", StringComparison.Ordinal)) continue;
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

    public UserDataBackup Backup(AdbClient adb)
    {
        var dir = Path.Combine(_paths.UserDataBackupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(dir);
        _log.Step("Backing up game data");
        _log.Info("Backup folder: " + dir);

        var external = BackupFolder(adb, "Android/data", GameConstants.DeviceExternalData, "/sdcard/Android/data",
            "/storage/emulated/0/Android/data", Path.Combine(dir, "data"));
        var obb = BackupFolder(adb, "Android/obb", GameConstants.DeviceObbDir, "/sdcard/Android/obb",
            "/storage/emulated/0/Android/obb", Path.Combine(dir, "obb"));

        var backup = new UserDataBackup { Directory = dir, ExternalData = external, Obb = obb };

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

    private FolderBackupReport BackupFolder(AdbClient adb, string label, string remote, string parentSdcard, string parentStorage, string localParent)
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
        Directory.CreateDirectory(localParent);

        var pkg = Path.GetFileName(report.LocalPath);
        var methods = new (string name, Func<bool> run)[]
        {
            ("adb pull", () => adb.Pull(remote, localParent, TimeSpan.FromHours(1))),
            ("adb pull via /storage/emulated/0", () => adb.Pull($"{parentStorage}/{pkg}", localParent, TimeSpan.FromHours(1))),
            ("tar stream (adb exec-out)", () => TarStream(adb, parentSdcard, pkg, localParent)),
            ("staging copy in /sdcard/Download", () => StagingCopy(adb, remote, pkg, localParent)),
            ("file by file", () => FileByFile(adb, report)),
        };

        foreach (var (name, run) in methods)
        {
            if (report.Inventory.Count == 0 && report.InventoryComplete) { report.Method = "nothing to copy"; break; }
            _log.Step($"Saving {label}: {name}");
            bool ok;
            try { ok = run(); }
            catch (Exception ex) { ok = false; _log.Warn($"  {name} failed: {ex.Message}"); }
            VerifyLocal(report);
            report.Attempts.Add($"{name}: {(ok ? "ok" : "errors")}, {report.Saved.Count}/{report.Inventory.Count} files verified");
            _log.Info("  " + report.Attempts[^1]);
            if (report.Missing.Count == 0 && report.Inventory.Count > 0)
            {
                report.Method = name;
                if (report.InventoryComplete) break;
            }
        }
        return report;
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
            var cp = adb.Shell($"mkdir -p \"{staging}\" && cp -r \"{remote}\" \"{staging}/\" 2>&1; echo __NCS_END__:$?", TimeSpan.FromHours(1));
            if (!cp.StdOut.Contains("__NCS_END__:0"))
                _log.Warn("  cp: " + cp.StdOut.Replace("__NCS_END__", "exit").Trim());
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
            if (!adb.Pull($"{report.RemotePath}/{missing.RelativePath}", local, TimeSpan.FromMinutes(30))) allOk = false;
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
        sb.AppendLine($"NightClubModKit game data backup - {DateTimeOffset.Now:O}");
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

    public bool Restore(AdbClient adb, UserDataBackup backup)
    {
        var ok = true;
        _log.Step("Restoring game data");
        foreach (var (report, parent) in new[] { (backup.ExternalData, "/sdcard/Android/data"), (backup.Obb, "/sdcard/Android/obb") })
        {
            if (report.Saved.Count == 0 || !Directory.Exists(report.LocalPath)) continue;
            ok &= RestoreFolder(adb, report, parent);
        }
        if (backup.HasInternalData)
            _log.Warn("Internal data was backed up to internal.tar but is not restored automatically (requires a debuggable build).");
        if (ok) _log.Success("Game data restored and verified.");
        else _log.Error("Some game data could not be restored. Your backup is kept in " + backup.Directory);
        return ok;
    }

    private bool RestoreFolder(AdbClient adb, FolderBackupReport report, string parent)
    {
        adb.Shell($"mkdir -p \"{parent}\"");
        adb.Push(report.LocalPath, parent + "/", TimeSpan.FromHours(1));
        if (RemoteMatches(adb, report)) return true;

        // Fallback: push a tar to /data/local/tmp and extract it on the device.
        _log.Warn($"  {report.Label}: adb push incomplete, trying a tar upload");
        var tar = Path.Combine(Path.GetTempPath(), $"ncs-restore-{Guid.NewGuid():N}.tar");
        try
        {
            TarFile.CreateFromDirectory(report.LocalPath, tar, includeBaseDirectory: true);
            var remoteTar = $"/data/local/tmp/{Path.GetFileName(tar)}";
            if (adb.Push(tar, remoteTar))
            {
                adb.Shell($"tar -xf \"{remoteTar}\" -C \"{parent}\"; rm -f \"{remoteTar}\"", TimeSpan.FromHours(1));
            }
        }
        finally
        {
            try { File.Delete(tar); } catch { /* ignore */ }
        }
        return RemoteMatches(adb, report);
    }

    private bool RemoteMatches(AdbClient adb, FolderBackupReport report)
    {
        var now = Inventory(adb, report.Label, report.RemotePath, Path.GetTempPath());
        var onDevice = now.Inventory.ToDictionary(f => f.RelativePath, f => f.Size, StringComparer.Ordinal);
        var missing = report.Inventory.Where(f => report.Saved.Contains(f.RelativePath))
            .Where(f => !onDevice.TryGetValue(f.RelativePath, out var size) || size != f.Size).Select(f => f.RelativePath).ToList();
        if (missing.Count == 0) _log.Info($"  {report.Label}: {report.Saved.Count} files restored and verified.");
        else foreach (var m in missing.Take(10)) _log.Warn($"  {report.Label}: not restored: {m}");
        return missing.Count == 0;
    }
}
