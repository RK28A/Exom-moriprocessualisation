using NightClubPatcher.Core.Adb;
using NightClubPatcher.Core.Android;
using NightClubPatcher.Core.Infrastructure;

namespace NightClubPatcher.Core.Recovery;

public sealed record InstallResult(bool Ok, string Message, bool Reinstalled = false);

/// <summary>
/// Installs an APK on the Quest. If the installed build is signed with a different key it never
/// uninstalls silently: the user confirms the uninstall + reinstall. The game's OBB files (game
/// content, deleted by Android with the package) are saved before and put back after; save data is
/// not backed up (the game only keeps a few settings; it starts fresh after a reinstall).
/// </summary>
public sealed class InstallService
{
    private readonly KitLog _log;
    private readonly ObbService _obb;

    public InstallService(KitLog log, ObbService obb)
    {
        _log = log;
        _obb = obb;
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
            if (r.Ok)
            {
                var note = EnsureObb(adb, apk);
                return Verify(adb, apk, new InstallResult(true, "Installed." + note));
            }
            if (!r.Combined.Contains("INSTALL_FAILED_UPDATE_INCOMPATIBLE") && !r.Combined.Contains("signatures do not match"))
                return new InstallResult(false, "adb install failed: " + r.Combined);
            _log.Warn("Android reported a signature conflict.");
        }

        // The OBB (game content) is deleted with the package: save it first, put it back after.
        var deviceObb = _obb.ListDevice(adb);
        var versionCode = VersionCodeOf(apk);
        var savedObb = deviceObb.Count == 0 && versionCode != null ? _obb.SavedFor(versionCode.Value) : new List<ObbFile>();
        var obbNote = deviceObb.Count > 0
            ? $"The game's OBB files ({deviceObb.Count}, {ObbService.FormatSize(deviceObb.Sum(f => f.Size))}) are saved first and put back after the reinstall."
            : savedObb.Count > 0
                ? $"No OBB file on the headset: the {savedObb.Count} saved OBB file(s) of version {versionCode} are put back after the reinstall."
                : "WARNING: no OBB file on the headset and none saved. If the game needs one it will stay on its Loading screen: " +
                  "reinstall the game from the store first, then patch again (NCMK saves the OBB before uninstalling).";

        var choice = ui.Choose("Signature mismatch detected",
            "Signature mismatch detected." + Environment.NewLine + Environment.NewLine +
            "The installed build and the build to install use different keys." + Environment.NewLine +
            "Android requires uninstalling the current game first." + Environment.NewLine + Environment.NewLine +
            obbNote + Environment.NewLine +
            "The game's local data (settings) is reset by the uninstall; it is not backed up.",
            new[] { "Cancel", "Uninstall and reinstall" });
        if (choice != 1) return new InstallResult(false, "Installation cancelled by user. Nothing was changed.");

        var obbFiles = savedObb;
        if (deviceObb.Count > 0)
        {
            var saved = _obb.Save(adb);
            if (!saved.Ok)
                return new InstallResult(false, "The game's OBB files could not be saved, so nothing was uninstalled." + Environment.NewLine + saved.Message);
            obbFiles = saved.Files.ToList();
        }

        _log.Step("Uninstalling the current build");
        var un = adb.Uninstall(GameConstants.PackageName);
        if (!un.Ok || !un.Combined.Contains("Success"))
            return new InstallResult(false, "Uninstall failed: " + un.Combined);

        _log.Step($"Installing {Path.GetFileName(apk)} ({purpose})");
        var inst = DoInstall(adb, apk, splits);
        if (!inst.Ok)
        {
            _log.Error("Install failed after uninstall: " + inst.Combined);
            return new InstallResult(false,
                "INSTALL FAILED after uninstall: " + inst.Combined + Environment.NewLine + "Use RESTORE ORIGINAL or EMERGENCY RESTORE.", true);
        }
        var message = "Reinstalled (fresh game data).";
        if (obbFiles.Count > 0)
        {
            // A failed copy never fails the install: the game is installed, the saved files stay on the PC.
            bool restored;
            try { restored = _obb.Restore(adb, obbFiles); }
            catch (Exception ex)
            {
                restored = false;
                _log.Error("OBB restore failed: " + ex.Message);
            }
            message += restored
                ? $" OBB files put back ({obbFiles.Count})."
                : $" WARNING: the OBB files could not all be put back; they are saved in {_obb.LocalDir}. Use Install again to retry.";
        }
        GrantStorageRead(adb);
        return Verify(adb, apk, new InstallResult(true, message, true));
    }

    /// <summary>
    /// After an in-place install: when the headset has no OBB but NCMK saved one for this version
    /// (e.g. it was lost by an earlier reinstall), put it back.
    /// </summary>
    private string EnsureObb(AdbClient adb, string apk)
    {
        try
        {
            if (_obb.ListDevice(adb).Count > 0) return "";
            var code = VersionCodeOf(apk);
            var saved = code != null ? _obb.SavedFor(code.Value) : new List<ObbFile>();
            if (saved.Count == 0) return "";
            _log.Warn($"The headset has no OBB file; putting back the {saved.Count} saved file(s) of version {code}.");
            var ok = _obb.Restore(adb, saved);
            GrantStorageRead(adb);
            return ok ? $" OBB files put back ({saved.Count})." : $" WARNING: OBB files could not all be put back (saved in {_obb.LocalDir}).";
        }
        catch (Exception ex)
        {
            _log.Warn("OBB check failed: " + ex.Message);
            return "";
        }
    }

    /// <summary>
    /// Unity logs "No permission to read external storage. Skipping OBB loading." when it may not read the
    /// OBB, and the headset's permission prompt skips re-signed builds. READ_EXTERNAL_STORAGE is declared by
    /// the game; granting it over adb is what a user would do in the settings. Best effort.
    /// </summary>
    private void GrantStorageRead(AdbClient adb)
    {
        var r = adb.Shell($"pm grant {GameConstants.PackageName} android.permission.READ_EXTERNAL_STORAGE 2>&1");
        var output = r.StdOut.Trim();
        if (output.Length == 0) _log.Info("Storage read permission granted to the game (for its OBB).");
        else _log.Warn("Could not grant the storage read permission: " + output);
    }

    private static long? VersionCodeOf(string apk)
    {
        try
        {
            return Apk.ApkInspector.Analyze(apk, new Apk.ApkInspectorOptions { ComputeApkHash = false, ComputeFileHashes = false }).Manifest.VersionCode;
        }
        catch
        {
            return null;
        }
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
