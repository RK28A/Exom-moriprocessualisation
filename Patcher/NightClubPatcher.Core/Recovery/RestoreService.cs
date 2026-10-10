using NightClubPatcher.Core.Adb;
using NightClubPatcher.Core.Apk;
using NightClubPatcher.Core.Backup;
using NightClubPatcher.Core.Infrastructure;

namespace NightClubPatcher.Core.Recovery;

public enum ModFilesPolicy { Ask, Keep, Disable, Delete }

public sealed record RestoreResult(bool Ok, string Message, IReadOnlyList<string> Steps);

/// <summary>RESTORE ORIGINAL and EMERGENCY RESTORE.</summary>
public sealed class RestoreService
{
    private readonly KitLog _log;
    private readonly BackupManager _backups;
    private readonly InstallService _installer;

    public RestoreService(KitLog log, BackupManager backups, InstallService installer)
    {
        _log = log;
        _backups = backups;
        _installer = installer;
    }

    public RestoreResult RestoreOriginal(AdbClient adb, OriginalBackup backup, IUserInteraction ui, string? modKitJavaHash,
        ModFilesPolicy modFiles = ModFilesPolicy.Ask, bool emergency = false)
    {
        var steps = new List<string>();
        void Step(string s) { steps.Add(s); _log.Step(s); }

        Step("Verifying original backup " + backup.ApkPath);
        var v = _backups.Verify(backup, new ApkInspectorOptions { ModKitSignerSha256 = null });
        if (v.Analysis == null || !v.Analysis.IsTargetPackage)
            return new RestoreResult(false, "Backup is unusable: " + string.Join("; ", v.Problems), steps);
        if (!v.Ok)
        {
            var hashOnly = v.Problems.All(p => p.StartsWith("No recorded SHA-256"));
            if (!(emergency && hashOnly))
                return new RestoreResult(false, "Backup verification failed:" + Environment.NewLine + string.Join(Environment.NewLine, v.Problems), steps);
            if (ui.Choose("Unverified backup", "The backup has no recorded hash, so its integrity cannot be proven. Install it anyway?",
                    new[] { "Cancel", "Install anyway" }) != 1)
                return new RestoreResult(false, "Cancelled.", steps);
        }
        var original = v.Analysis;
        var originalJava = original.Signer?.JavaHash ?? backup.Metadata?.SignerJavaHash;

        Step("Detecting installed game state");
        var state = DeviceGameState.Read(adb, originalJava, modKitJavaHash);
        _log.Info($"Installed: {state.Describe()} {state.VersionName} ({state.VersionCode})");

        Step("Stopping the game");
        adb.ForceStop(GameConstants.PackageName);

        Step("Installing original APK");
        var splits = backup.SplitPaths.Where(File.Exists).ToList();
        var install = _installer.Install(adb, backup.ApkPath, splits, ui, originalJava, modKitJavaHash, "restore original");
        if (!install.Ok) return new RestoreResult(false, install.Message, steps);

        Step("Handling NCMK files on the device");
        HandleModFiles(adb, ui, modFiles);

        Step("Verifying package, version and signature");
        var after = DeviceGameState.Read(adb, originalJava, modKitJavaHash);
        if (!after.Installed) return new RestoreResult(false, "Game is not installed after restore!", steps);
        if (after.VersionCode != original.Manifest.VersionCode)
            return new RestoreResult(false, $"Unexpected version after restore: {after.VersionCode}.", steps);
        if (originalJava != null && !DeviceGameState.SignerMatches(after, originalJava))
            return new RestoreResult(false, "Installed signature is not the original developer signature.", steps);
        if (after.LoaderFilePresent == true)
            return new RestoreResult(false, "libncmkloader.so is still present in the installed app.", steps);

        Step("Verifying original integrity (pulling installed APK)");
        var tmp = Path.Combine(Path.GetTempPath(), "NCMK", "verify-installed.apk");
        if (after.ApkPaths.Count > 0 && adb.Pull(after.ApkPaths[0], tmp))
        {
            var installedSha = Hashing.Sha256File(tmp);
            File.Delete(tmp);
            if (!Hashing.Equal(installedSha, original.ApkSha256))
                return new RestoreResult(false, "Installed APK hash does not match the original backup.", steps);
        }
        else
        {
            _log.Warn("Could not pull installed APK for hash verification.");
        }

        _log.Success("RESTORE ORIGINAL completed: clean NightClub Simulator is installed.");
        return new RestoreResult(true, "Original game restored and verified.", steps);
    }

    /// <summary>
    /// EMERGENCY RESTORE: needs only an original.apk file. Does not rely on build.json,
    /// version profiles or the keystore.
    /// </summary>
    public RestoreResult Emergency(AdbClient adb, string? apkPath, IUserInteraction ui)
    {
        OriginalBackup? backup;
        if (apkPath != null)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(apkPath))!;
            backup = Path.GetFileName(apkPath) == BackupManager.OriginalFile
                ? _backups.All().FirstOrDefault(b => Path.GetFullPath(b.ApkPath) == Path.GetFullPath(apkPath)) ?? new OriginalBackup(dir, apkPath, null)
                : new OriginalBackup(dir, apkPath, null);
        }
        else
        {
            backup = _backups.All().FirstOrDefault();
        }
        if (backup == null)
            return new RestoreResult(false, "No original.apk found under Backups/. Pass the path explicitly.", Array.Empty<string>());

        _log.Warn("EMERGENCY RESTORE using " + backup.ApkPath);
        return RestoreOriginal(adb, backup, ui, modKitJavaHash: null, ModFilesPolicy.Disable, emergency: true);
    }

    public void HandleModFiles(AdbClient adb, IUserInteraction ui, ModFilesPolicy policy)
    {
        if (!adb.RemoteExists(GameConstants.DeviceModsRoot))
        {
            _log.Info("No NCMKMods folder on the device.");
            return;
        }

        if (policy == ModFilesPolicy.Ask)
        {
            var c = ui.Choose("Keep Mod Files?",
                $"{GameConstants.DeviceModsRoot} exists on the Quest. The original game ignores it.",
                new[] { "Keep", "Disable (rename to NCMKMods.disabled)", "Delete" });
            policy = c switch { 0 => ModFilesPolicy.Keep, 2 => ModFilesPolicy.Delete, _ => ModFilesPolicy.Disable };
        }

        switch (policy)
        {
            case ModFilesPolicy.Keep:
                _log.Info("Mod files kept.");
                break;
            case ModFilesPolicy.Disable:
                if (adb.RemoteExists(GameConstants.DeviceModsDisabled))
                {
                    var stamped = GameConstants.DeviceModsDisabled + "-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                    adb.Shell($"mv \"{GameConstants.DeviceModsRoot}\" \"{stamped}\"");
                    _log.Info("Mod files moved to " + stamped);
                }
                else
                {
                    adb.Shell($"mv \"{GameConstants.DeviceModsRoot}\" \"{GameConstants.DeviceModsDisabled}\"");
                    _log.Info("Mod files moved to " + GameConstants.DeviceModsDisabled);
                }
                break;
            case ModFilesPolicy.Delete:
                if (ui.Confirm("Delete mod files", $"Permanently delete {GameConstants.DeviceModsRoot} (mods, configs, logs)?", "Delete"))
                {
                    adb.Shell($"rm -rf \"{GameConstants.DeviceModsRoot}\"");
                    _log.Info("Mod files deleted.");
                }
                break;
        }

        // Folders written by older kits are reported, never touched automatically.
        foreach (var legacy in GameConstants.LegacyDeviceModsRoots.Where(adb.RemoteExists))
            _log.Info($"Note: {legacy} exists (mod folder of an older NightClubModKit build); it was left untouched.");
    }
}
