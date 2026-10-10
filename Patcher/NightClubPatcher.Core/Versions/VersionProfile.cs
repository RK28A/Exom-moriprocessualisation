using NightClubPatcher.Core.Infrastructure;

namespace NightClubPatcher.Core.Versions;

public sealed class FileFingerprints
{
    public string? LibMain { get; set; }
    public string? LibUnity { get; set; }
    public string? LibIl2Cpp { get; set; }
    public string? GlobalMetadata { get; set; }

    public bool IsPinned =>
        !string.IsNullOrWhiteSpace(LibMain) && !string.IsNullOrWhiteSpace(LibUnity) &&
        !string.IsNullOrWhiteSpace(LibIl2Cpp) && !string.IsNullOrWhiteSpace(GlobalMetadata);
}

/// <summary>Versions/&lt;versionName&gt;-&lt;versionCode&gt;.json</summary>
public sealed class VersionProfile
{
    public string Package { get; set; } = GameConstants.PackageName;
    public string VersionName { get; set; } = "";
    public long VersionCode { get; set; }
    public string UnityVersion { get; set; } = "";
    public string Architecture { get; set; } = GameConstants.Abi;
    public string Backend { get; set; } = "IL2CPP";
    public string? XrRuntime { get; set; }

    /// <summary>global-metadata.dat version (31 for Unity 6000.x). A different value means a different IL2CPP build.</summary>
    public int? MetadataVersion { get; set; }

    /// <summary>Major managed libraries observed in the build (informational, for mod authors).</summary>
    public List<string>? Stack { get; set; }

    /// <summary>Facts read from the real APK that shape how NCMK works (informational).</summary>
    public List<string>? Facts { get; set; }

    /// <summary>SHA-256 of the four critical files of the original (unmodified) game.</summary>
    public FileFingerprints Hashes { get; set; } = new();

    /// <summary>SHA-256 of the store APK, when known.</summary>
    public string? OriginalApkSha256 { get; set; }

    /// <summary>SHA-256 of the developer signing certificate, when known.</summary>
    public string? OriginalSignerSha256 { get; set; }

    /// <summary>True once someone confirmed the loader runs correctly on this build.</summary>
    public bool Verified { get; set; }

    public string? Notes { get; set; }

    public string FileName => $"{KitPaths.Sanitize(VersionName)}-{VersionCode}.json";
}

public enum Compatibility
{
    /// <summary>Package, version and all pinned file hashes match a profile.</summary>
    KnownBuild,
    /// <summary>Version matches a profile whose fingerprints were never recorded.</summary>
    ProfileNotPinned,
    /// <summary>Version matches but at least one file hash differs.</summary>
    HashMismatch,
    /// <summary>No profile for this version (likely a game update).</summary>
    UnknownVersion,
    /// <summary>Not NightClub Simulator at all.</summary>
    WrongPackage,
}

public sealed record CompatibilityResult(Compatibility Status, VersionProfile? Profile, IReadOnlyList<string> Details)
{
    public bool AllowsAutomaticPatch => Status == Compatibility.KnownBuild;

    public string Summary => Status switch
    {
        Compatibility.KnownBuild => "KNOWN BUILD - fingerprints match a supported version.",
        Compatibility.ProfileNotPinned => "Supported version, but file fingerprints have not been pinned yet. Run 'Pin fingerprints' on a verified original backup.",
        Compatibility.HashMismatch => "Version number matches but game files differ from the supported build. The APK may be modified or rebuilt.",
        Compatibility.UnknownVersion => "Unknown NightClub version detected. Compatibility has not been verified.",
        Compatibility.WrongPackage => "This APK is not NightClub Simulator. Patching refused.",
        _ => Status.ToString(),
    };
}
