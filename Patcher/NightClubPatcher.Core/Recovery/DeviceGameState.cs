using System.Text.RegularExpressions;
using NightClubPatcher.Core.Adb;

namespace NightClubPatcher.Core.Recovery;

public enum InstallationKind { NotInstalled, Original, ModdedByModKit, UnknownSigner, Unknown }

public sealed class InstalledGameState
{
    public bool Installed { get; init; }
    public string? VersionName { get; init; }
    public long? VersionCode { get; init; }
    public List<string> ApkPaths { get; init; } = new();
    public List<string> SignatureHashes { get; init; } = new();
    public InstallationKind Kind { get; set; } = InstallationKind.Unknown;
    public bool? LoaderFilePresent { get; set; }
    public string Describe() => Kind switch
    {
        InstallationKind.NotInstalled => "Not installed",
        InstallationKind.Original => "Original",
        InstallationKind.ModdedByModKit => "Modded (NightClubModKit)",
        InstallationKind.UnknownSigner => "Unknown signature (modified by another tool?)",
        _ => "Unknown",
    };
}

public static partial class DeviceGameState
{
    [GeneratedRegex(@"signatures:\[([0-9a-fA-F, ]+)\]")]
    private static partial Regex SignaturesRegex();

    /// <summary>
    /// Reads the installed package state cheaply (no APK pull). The signer is identified by
    /// comparing Signature.hashCode() printed by dumpsys with the known certificates.
    /// </summary>
    public static InstalledGameState Read(AdbClient adb, string? originalJavaHash, string? modKitJavaHash)
    {
        if (!adb.IsPackageInstalled(GameConstants.PackageName))
            return new InstalledGameState { Installed = false, Kind = InstallationKind.NotInstalled };

        var (vn, vc) = adb.InstalledVersion(GameConstants.PackageName);
        var dump = adb.Shell($"dumpsys package {GameConstants.PackageName}").StdOut;
        var sigs = new List<string>();
        var m = SignaturesRegex().Match(dump);
        if (m.Success)
            sigs.AddRange(m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToLowerInvariant().TrimStart('0')));

        var state = new InstalledGameState
        {
            Installed = true,
            VersionName = vn,
            VersionCode = vc,
            ApkPaths = adb.PackagePaths(GameConstants.PackageName),
            SignatureHashes = sigs,
        };

        if (state.ApkPaths.Count > 0)
        {
            var dir = state.ApkPaths[0][..state.ApkPaths[0].LastIndexOf('/')];
            var ls = adb.Shell($"ls {dir}/lib/arm64/ 2>/dev/null");
            if (ls.Ok && ls.StdOut.Length > 0)
                state.LoaderFilePresent = ls.StdOut.Contains(GameConstants.LoaderLibName);
        }

        static string Norm(string? h) => (h ?? "").ToLowerInvariant().TrimStart('0');
        if (modKitJavaHash != null && sigs.Contains(Norm(modKitJavaHash))) state.Kind = InstallationKind.ModdedByModKit;
        else if (originalJavaHash != null && sigs.Contains(Norm(originalJavaHash))) state.Kind = InstallationKind.Original;
        else if (sigs.Count > 0 && (originalJavaHash != null || modKitJavaHash != null)) state.Kind = InstallationKind.UnknownSigner;
        else state.Kind = state.LoaderFilePresent == true ? InstallationKind.ModdedByModKit : InstallationKind.Unknown;
        return state;
    }

    public static bool SignerMatches(InstalledGameState state, string? javaHash) =>
        javaHash != null && state.SignatureHashes.Contains(javaHash.ToLowerInvariant().TrimStart('0'));
}
