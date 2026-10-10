using NightClubPatcher.Core.Adb;
using NightClubPatcher.Core.Infrastructure;

namespace NightClubPatcher.Core.Recovery;

public sealed record UserDataBackup(string Directory, bool HasExternalData, bool HasObb, bool HasInternalData, IReadOnlyList<string> Notes);

/// <summary>
/// Backs up / restores the user data that adb can reach:
///   /sdcard/Android/data/&lt;pkg&gt;  (Unity persistentDataPath - most saves live here)
///   /sdcard/Android/obb/&lt;pkg&gt;
///   /data/data/&lt;pkg&gt;           (only through run-as, i.e. debuggable builds)
/// Nothing is ever deleted here.
/// </summary>
public sealed class UserDataService
{
    private readonly KitPaths _paths;
    private readonly KitLog _log;

    public UserDataService(KitPaths paths, KitLog log)
    {
        _paths = paths;
        _log = log;
    }

    public UserDataBackup Backup(AdbClient adb)
    {
        var dir = Path.Combine(_paths.UserDataBackupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(dir);
        var notes = new List<string>();
        _log.Step("Backing up accessible user data to " + dir);

        bool PullDir(string remote, string localParent)
        {
            if (!adb.RemoteExists(remote)) { notes.Add($"{remote} does not exist."); return false; }
            Directory.CreateDirectory(localParent);
            var ok = adb.Pull(remote, localParent, TimeSpan.FromHours(1));
            notes.Add(ok ? $"Saved {remote}" : $"FAILED to pull {remote}");
            return ok;
        }

        var ext = PullDir(GameConstants.DeviceExternalData, Path.Combine(dir, "data"));
        var obb = PullDir(GameConstants.DeviceObbDir, Path.Combine(dir, "obb"));

        // Internal data is only reachable for debuggable packages.
        var internalOk = false;
        var probe = adb.Shell($"run-as {GameConstants.PackageName} ls 2>&1");
        if (probe.Ok && !probe.StdOut.Contains("not debuggable") && !probe.StdOut.Contains("unknown package"))
        {
            notes.Add("Internal data is accessible via run-as (debuggable build).");
            internalOk = PullInternalTar(adb, Path.Combine(dir, "internal.tar"));
            notes.Add(internalOk ? "Saved internal data to internal.tar" : "FAILED to save internal data");
        }
        else
        {
            notes.Add("Internal app data (/data/data) is not accessible without root (store builds are not debuggable).");
        }

        Json.Write(Path.Combine(dir, "userdata.json"), new { createdAt = DateTimeOffset.Now, external = ext, obb, internalData = internalOk, notes });
        foreach (var n in notes) _log.Info("  " + n);
        return new UserDataBackup(dir, ext, obb, internalOk, notes);
    }

    private bool PullInternalTar(AdbClient adb, string localTar)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(adb.AdbPath!)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (adb.Serial != null) { psi.ArgumentList.Add("-s"); psi.ArgumentList.Add(adb.Serial); }
            psi.ArgumentList.Add("exec-out");
            psi.ArgumentList.Add($"run-as {GameConstants.PackageName} tar cf - .");
            using var p = System.Diagnostics.Process.Start(psi)!;
            using (var f = File.Create(localTar)) p.StandardOutput.BaseStream.CopyTo(f);
            p.WaitForExit();
            return p.ExitCode == 0 && new FileInfo(localTar).Length > 0;
        }
        catch (Exception ex)
        {
            _log.Warn("Internal data backup failed: " + ex.Message);
            return false;
        }
    }

    public bool Restore(AdbClient adb, UserDataBackup backup)
    {
        var ok = true;
        _log.Step("Restoring accessible user data from " + backup.Directory);
        if (backup.HasExternalData)
        {
            var local = Path.Combine(backup.Directory, "data", GameConstants.PackageName);
            adb.Shell("mkdir -p /sdcard/Android/data");
            ok &= Directory.Exists(local) && adb.Push(local, "/sdcard/Android/data/", TimeSpan.FromHours(1));
        }
        if (backup.HasObb)
        {
            var local = Path.Combine(backup.Directory, "obb", GameConstants.PackageName);
            adb.Shell("mkdir -p /sdcard/Android/obb");
            ok &= Directory.Exists(local) && adb.Push(local, "/sdcard/Android/obb/", TimeSpan.FromHours(1));
        }
        if (backup.HasInternalData)
            _log.Warn("Internal data was backed up to internal.tar but is not restored automatically (requires a debuggable build).");
        if (ok) _log.Success("User data restored.");
        else _log.Error("Some user data could not be restored. Your backup is kept in " + backup.Directory);
        return ok;
    }
}
