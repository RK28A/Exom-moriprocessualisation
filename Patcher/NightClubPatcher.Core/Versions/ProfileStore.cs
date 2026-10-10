using NightClubPatcher.Core.Apk;
using NightClubPatcher.Core.Infrastructure;

namespace NightClubPatcher.Core.Versions;

public sealed class ProfileStore
{
    private readonly KitPaths _paths;

    public ProfileStore(KitPaths paths) => _paths = paths;

    public List<VersionProfile> All()
    {
        if (!Directory.Exists(_paths.Versions)) return new();
        var list = new List<VersionProfile>();
        foreach (var f in Directory.EnumerateFiles(_paths.Versions, "*.json"))
        {
            try
            {
                var p = Json.Read<VersionProfile>(f);
                if (p != null && GameConstants.IsTargetPackage(p.Package)) list.Add(p);
            }
            catch
            {
                // ignore malformed profile
            }
        }
        return list.OrderBy(p => p.VersionCode).ToList();
    }

    public VersionProfile? Find(string? versionName, long? versionCode) =>
        All().FirstOrDefault(p => p.VersionCode == versionCode && p.VersionName == versionName);

    public void Save(VersionProfile profile) => Json.Write(Path.Combine(_paths.Versions, profile.FileName), profile);

    public CompatibilityResult Evaluate(ApkAnalysis apk)
    {
        var details = new List<string>();
        if (!GameConstants.IsTargetPackage(apk.Manifest.Package))
        {
            details.Add($"Package '{apk.Manifest.Package}' != '{GameConstants.PackageName}'.");
            return new CompatibilityResult(Compatibility.WrongPackage, null, details);
        }

        var profile = Find(apk.Manifest.VersionName, apk.Manifest.VersionCode);
        if (profile == null)
        {
            var supported = string.Join(", ", All().Select(p => $"{p.VersionName} ({p.VersionCode})"));
            details.Add($"Installed: {apk.Manifest.VersionName} ({apk.Manifest.VersionCode})");
            details.Add($"Supported: {(supported.Length > 0 ? supported : "none")}");
            return new CompatibilityResult(Compatibility.UnknownVersion, null, details);
        }

        if (!string.IsNullOrEmpty(profile.UnityVersion) && apk.UnityVersion != null && profile.UnityVersion != apk.UnityVersion)
            details.Add($"Unity version {apk.UnityVersion} differs from profile ({profile.UnityVersion}).");

        if (!profile.Hashes.IsPinned)
        {
            details.Add("Profile has no pinned file hashes.");
            return new CompatibilityResult(Compatibility.ProfileNotPinned, profile, details);
        }

        var mismatch = false;
        void Check(string label, string? expected, string? actual)
        {
            if (Hashing.Equal(expected, actual)) return;
            mismatch = true;
            details.Add($"{label}: expected {expected}, found {actual ?? "missing"}");
        }

        // libmain.so is modified by the patcher itself, so only check it for original APKs.
        if (!apk.IsPatchedByModKit) Check("libmain.so", profile.Hashes.LibMain, apk.Hash(GameConstants.LibMainEntry));
        Check("libunity.so", profile.Hashes.LibUnity, apk.Hash(GameConstants.LibUnityEntry));
        Check("libil2cpp.so", profile.Hashes.LibIl2Cpp, apk.Hash(GameConstants.LibIl2CppEntry));
        Check("global-metadata.dat", profile.Hashes.GlobalMetadata, apk.Hash(GameConstants.MetadataEntry));

        return new CompatibilityResult(mismatch ? Compatibility.HashMismatch : Compatibility.KnownBuild, profile, details);
    }

    /// <summary>Records the fingerprints of a verified original APK into its version profile.</summary>
    public VersionProfile Pin(ApkAnalysis original)
    {
        if (!GameConstants.IsTargetPackage(original.Manifest.Package))
            throw new InvalidOperationException("Refusing to pin fingerprints of a foreign package.");
        if (original.IsPatchedByModKit || original.ModificationIndicators.Count > 0)
            throw new InvalidOperationException("Refusing to pin fingerprints of a modified APK.");

        var profile = Find(original.Manifest.VersionName, original.Manifest.VersionCode) ?? new VersionProfile
        {
            VersionName = original.Manifest.VersionName ?? "unknown",
            VersionCode = original.Manifest.VersionCode ?? 0,
        };
        profile.UnityVersion = original.UnityVersion ?? profile.UnityVersion;
        profile.Architecture = original.IsArm64 ? GameConstants.Abi : profile.Architecture;
        profile.Backend = original.Il2Cpp.IsIl2Cpp ? "IL2CPP" : "Mono";
        profile.XrRuntime = original.XrRuntime ?? profile.XrRuntime;
        profile.Hashes = new FileFingerprints
        {
            LibMain = original.Hash(GameConstants.LibMainEntry),
            LibUnity = original.Hash(GameConstants.LibUnityEntry),
            LibIl2Cpp = original.Hash(GameConstants.LibIl2CppEntry),
            GlobalMetadata = original.Hash(GameConstants.MetadataEntry),
        };
        profile.OriginalApkSha256 = original.ApkSha256;
        profile.OriginalSignerSha256 = original.Signer?.CertSha256;
        Save(profile);
        return profile;
    }
}
