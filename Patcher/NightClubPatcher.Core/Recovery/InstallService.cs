using NightClubPatcher.Core.Adb;
using NightClubPatcher.Core.Android;
using NightClubPatcher.Core.Infrastructure;

namespace NightClubPatcher.Core.Recovery;

public sealed record InstallResult(bool Ok, string Message, bool Reinstalled = false, UserDataBackup? DataBackup = null);

/// <summary>
/// Installs an APK on the Quest. If the installed build is signed with a different key it never
/// uninstalls silently: the user must choose "Backup accessible data and reinstall".
/// </summary>
public sealed class InstallService
{
    private readonly KitLog _log;
    private readonly UserDataService _userData;

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

        var choice = ui.Choose("Signature mismatch detected",
            "Signature mismatch detected." + Environment.NewLine + Environment.NewLine +
            "The installed build and the build to install use different keys." + Environment.NewLine +
            "Android requires uninstalling the current game first." + Environment.NewLine + Environment.NewLine +
            "Accessible data (/sdcard/Android/data and obb) will be backed up and restored." + Environment.NewLine +
            "Internal data (/data/data) cannot be read without root and may be lost (Meta cloud backup may restore it).",
            new[] { "Cancel", "Backup accessible data and reinstall" });
        if (choice != 1) return new InstallResult(false, "Installation cancelled by user. Nothing was changed.");

        var data = _userData.Backup(adb);
        if (data.Notes.Any(n => n.StartsWith("FAILED")))
        {
            if (ui.Choose("Data backup incomplete",
                    "Some data could not be backed up:" + Environment.NewLine + string.Join(Environment.NewLine, data.Notes) +
                    Environment.NewLine + Environment.NewLine + "Continue anyway?",
                    new[] { "Cancel", "Continue" }) != 1)
                return new InstallResult(false, "Cancelled: data backup incomplete. Nothing was uninstalled.", false, data);
        }

        _log.Step("Uninstalling the current build");
        var un = adb.Uninstall(GameConstants.PackageName);
        if (!un.Ok || !un.Combined.Contains("Success"))
            return new InstallResult(false, "Uninstall failed: " + un.Combined, false, data);

        _log.Step($"Installing {Path.GetFileName(apk)} ({purpose})");
        var inst = DoInstall(adb, apk, splits);
        if (!inst.Ok)
        {
            _log.Error("Install failed after uninstall: " + inst.Combined);
            return new InstallResult(false,
                "INSTALL FAILED after uninstall: " + inst.Combined + Environment.NewLine +
                "Your data backup is in " + data.Directory + ". Use RESTORE ORIGINAL or EMERGENCY RESTORE.", true, data);
        }

        _userData.Restore(adb, data);
        return Verify(adb, apk, new InstallResult(true, "Reinstalled with data restore.", true, data));
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
