using NightClubPatcher.Core.Adb;
using NightClubPatcher.Core.Android;
using NightClubPatcher.Core.Infrastructure;

namespace NightClubPatcher.Core.Recovery;

public sealed record InstallResult(bool Ok, string Message, bool Reinstalled = false, UserDataBackup? DataBackup = null);

/// <summary>
/// Installs an APK on the Quest. If the installed build is signed with a different key it never
/// uninstalls silently: the user confirms, and <see cref="DataMode"/> decides what happens to the
/// game data (back up + restore, back up only, nothing, or a clean install).
/// </summary>
public sealed class InstallService
{
    private readonly KitLog _log;
    private readonly UserDataService _userData;

    /// <summary>What a signature-mismatch reinstall does with the game's external data.</summary>
    public GameDataMode DataMode { get; set; } = GameDataMode.Keep;

    public InstallService(KitLog log, UserDataService userData)
    {
        _log = log;
        _userData = userData;
    }

    public InstallResult Install(AdbClient adb, string apk, IReadOnlyList<string> splits, IUserInteraction ui,
        string? originalJavaHash, string? modKitJavaHash, string purpose)
    {
        var signer = ApkSignatureReader.Read(apk);
        if (signer == null) return new InstallResult(false, "APK is not signed; refusing to install.");

        var before = DeviceGameState.Read(adb, originalJavaHash, modKitJavaHash);
        var mismatch = before.Installed && before.SignatureHashes.Count > 0 && !DeviceGameState.SignerMatches(before, signer.JavaHash);

        if (before.Installed) adb.ForceStop(GameConstants.PackageName);

        if (!mismatch)
        {
            _log.Step($"Installing {Path.GetFileName(apk)} ({purpose})");
            var r = DoInstall(adb, apk, splits);
            if (r.Ok) return Verify(adb, apk, new InstallResult(true, "Installed."));
            if (!r.Combined.Contains("INSTALL_FAILED_UPDATE_INCOMPATIBLE") && !r.Combined.Contains("signatures do not match"))
                return new InstallResult(false, "adb install failed: " + r.Combined);
            _log.Warn("Android reported a signature conflict.");
        }

        var mode = DataMode;
        var dataPlan = mode switch
        {
            GameDataMode.BackupOnly => "Game data mode: Backup only. Accessible data (/sdcard/Android/data and obb) will be backed up but NOT restored.",
            GameDataMode.Skip => "Game data mode: Skip. NO backup is made and nothing is restored: the game's external data is lost by the uninstall.",
            GameDataMode.Clean => "Game data mode: Clean install. Accessible data is backed up for safety, then the game starts with fresh data (nothing is restored).",
            _ => "Accessible data (/sdcard/Android/data and obb) will be backed up and restored.",
        };
        var choice = ui.Choose("Signature mismatch detected",
            "Signature mismatch detected." + Environment.NewLine + Environment.NewLine +
            "The installed build and the build to install use different keys." + Environment.NewLine +
            "Android requires uninstalling the current game first." + Environment.NewLine + Environment.NewLine +
            dataPlan + Environment.NewLine +
            "Internal data (/data/data) cannot be read without root and may be lost (Meta cloud backup may restore it).",
            new[] { "Cancel", mode.BacksUp() ? "Backup accessible data and reinstall" : "Reinstall without backup" });
        if (choice != 1) return new InstallResult(false, "Installation cancelled by user. Nothing was changed.");

        UserDataBackup? data = null;
        if (mode.BacksUp())
        {
            data = _userData.Backup(adb, ui);

            // Always show exactly what was (and was not) saved before anything is uninstalled.
            var complete = data.AccessibleDataComplete || data.SkippedByUser;
            var after = mode.Restores()
                ? "Continue = uninstall the current game, install the new build, restore the saved files."
                : "Continue = uninstall the current game and install the new build. The saved files are NOT restored (" +
                  mode.Label() + "); they stay in the backup folder.";
            var message = (data.SkippedByUser
                              ? "Game data backup skipped (your choice). Uninstalling removes the game's data from the headset; a partial copy stays on the PC:"
                              : complete
                              ? "All game data adb can reach was saved and verified file by file:"
                              : "WARNING: the game data backup is INCOMPLETE. Uninstalling now can lose your progress.") +
                          Environment.NewLine + Environment.NewLine + data.Describe() + Environment.NewLine + Environment.NewLine +
                          "The full file list is in inventory.txt in the backup folder." + Environment.NewLine +
                          (complete ? after : "Recommended: Cancel. Nothing has been uninstalled; your game and its data are untouched.");
            var options = complete
                ? new[] { "Cancel", mode.Restores() && !data.SkippedByUser ? "Uninstall, install and restore data" : "Uninstall and install" }
                : new[] { "Cancel (keep the current game untouched)", "Continue anyway - I accept losing the missing data" };
            if (ui.Choose(complete ? "Game data saved - confirm reinstall" : "Game data backup INCOMPLETE", message, options) != 1)
                return new InstallResult(false,
                    "Cancelled before uninstalling. Nothing was changed on the headset. Data backup kept in " + data.Directory, false, data);
        }
        else
        {
            _log.Warn("Game data mode Skip: no backup of the game data is made.");
        }

        _log.Step("Uninstalling the current build");
        var un = adb.Uninstall(GameConstants.PackageName);
        if (!un.Ok || !un.Combined.Contains("Success"))
            return new InstallResult(false, "Uninstall failed: " + un.Combined, false, data);

        if (mode == GameDataMode.Clean)
        {
            // Android normally removes these with the package; make sure nothing old is left behind.
            foreach (var dir in new[] { GameConstants.DeviceExternalData, GameConstants.DeviceObbDir })
                if (adb.RemoteExists(dir))
                {
                    adb.Shell($"rm -rf \"{dir}\"");
                    _log.Info("Clean install: removed leftover " + dir);
                }
        }

        _log.Step($"Installing {Path.GetFileName(apk)} ({purpose})");
        var inst = DoInstall(adb, apk, splits);
        if (!inst.Ok)
        {
            _log.Error("Install failed after uninstall: " + inst.Combined);
            return new InstallResult(false,
                "INSTALL FAILED after uninstall: " + inst.Combined + Environment.NewLine +
                (data != null ? "Your data backup is in " + data.Directory + ". " : "") + "Use RESTORE ORIGINAL or EMERGENCY RESTORE.", true, data);
        }

        var note = mode switch
        {
            GameDataMode.BackupOnly => "Reinstalled; game data backed up but not restored (Backup only).",
            GameDataMode.Skip => "Reinstalled without game data backup or restore (Skip).",
            GameDataMode.Clean => "Reinstalled clean; the game creates fresh data on first launch.",
            _ => "Reinstalled with data restore.",
        };
        if (mode.Restores() && data is { SkippedByUser: true })
        {
            note = "Reinstalled. Game data backup was skipped, so nothing was restored; the game starts with fresh data.";
            _log.Info(note);
        }
        else if (mode.Restores() && data != null)
        {
            // A failed restore never fails the install: the game is installed and can be launched;
            // the backup stays on the PC.
            bool restored;
            try { restored = _userData.Restore(adb, data); }
            catch (Exception ex)
            {
                restored = false;
                _log.Error("Game data restore failed: " + ex.Message);
            }
            if (!restored)
            {
                note = "Reinstalled. Game data restore was INCOMPLETE; the backup is kept in " + data.Directory + ".";
                _log.Warn(note);
            }
        }
        else if (data != null)
        {
            _log.Info("Game data not restored (" + mode.Label() + "). Backup kept in " + data.Directory);
        }
        return Verify(adb, apk, new InstallResult(true, note, true, data));
    }

    private static ProcessResult DoInstall(AdbClient adb, string apk, IReadOnlyList<string> splits) =>
        splits.Count == 0 ? adb.Install(apk) : adb.InstallMultiple(new[] { apk }.Concat(splits));

    private InstallResult Verify(AdbClient adb, string apk, InstallResult result)
    {
        if (!adb.IsPackageInstalled(GameConstants.PackageName))
            return result with { Ok = false, Message = "Package not found after installation." };
        var expected = Apk.ApkInspector.Analyze(apk, new Apk.ApkInspectorOptions { ComputeApkHash = false, ComputeFileHashes = false });
        var (_, code) = adb.InstalledVersion(GameConstants.PackageName);
        if (code != expected.Manifest.VersionCode)
            return result with { Ok = false, Message = $"Installed versionCode {code} != expected {expected.Manifest.VersionCode}." };
        _log.Success($"Verified: {GameConstants.PackageName} {expected.VersionLabel} is installed.");
        return result;
    }
}
