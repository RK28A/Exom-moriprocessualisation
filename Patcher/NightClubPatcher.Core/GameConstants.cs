namespace NightClubPatcher.Core;

/// <summary>
/// Hard-wired identity of the only game this toolkit is allowed to touch.
/// </summary>
public static class GameConstants
{
    public const string PackageName = "com.mandegames.NightClubSimulater";
    public const string DisplayName = "NightClub Simulator";

    public const string Abi = "arm64-v8a";
    public const string LibDir = "lib/arm64-v8a/";

    public const string LibMainEntry = LibDir + "libmain.so";
    public const string LibUnityEntry = LibDir + "libunity.so";
    public const string LibIl2CppEntry = LibDir + "libil2cpp.so";
    public const string MetadataEntry = "assets/bin/Data/Managed/Metadata/global-metadata.dat";
    public const string DataUnity3dEntry = "assets/bin/Data/data.unity3d";

    public static readonly string[] RequiredEntries =
    {
        LibMainEntry, LibUnityEntry, LibIl2CppEntry, MetadataEntry, DataUnity3dEntry,
    };

    public const string LoaderLibName = "libncmkloader.so";
    public const string LoaderEntry = LibDir + LoaderLibName;
    public const string ModLibPrefix = "libncmkmod_";
    public const string PatchMarkerEntry = "assets/NCMK/patch.json";

    // Names used by NightClubModKit (before the NCMK rename). Still detected and removed by Unpatch.
    public const string LegacyLoaderLibName = "libncsloader.so";
    public const string LegacyLoaderEntry = LibDir + LegacyLoaderLibName;
    public const string LegacyModLibPrefix = "libncsmod_";
    public const string LegacyPatchMarkerEntry = "assets/NightClubModKit/patch.json";

    public static readonly string[] AllLoaderLibNames = { LoaderLibName, LegacyLoaderLibName };
    public static readonly string[] AllLoaderEntries = { LoaderEntry, LegacyLoaderEntry };
    public static readonly string[] AllModLibPrefixes = { ModLibPrefix, LegacyModLibPrefix };
    public static readonly string[] AllPatchMarkerEntries = { PatchMarkerEntry, LegacyPatchMarkerEntry };

    /// <summary>True for lib/arm64-v8a/libncmkmod_*.so and the legacy lib/arm64-v8a/libncsmod_*.so.</summary>
    public static bool IsModLibEntry(string entryName) =>
        entryName.EndsWith(".so", StringComparison.Ordinal) &&
        AllModLibPrefixes.Any(p => entryName.StartsWith(LibDir + p, StringComparison.Ordinal));

    /// <summary>API version implemented by the native loader shipped with this patcher.</summary>
    public const int LoaderApi = 2;

    // On-device locations. The game has no storage permission, so the only folder both the
    // patcher (adb) and the game can use is the app's own external files directory.
    public const string DeviceExternalData = "/sdcard/Android/data/" + PackageName;
    public const string DeviceObbDir = "/sdcard/Android/obb/" + PackageName;
    public const string DeviceModsRoot = DeviceExternalData + "/files/NCMKMods";
    public const string DeviceModsDisabled = DeviceModsRoot + ".disabled";
    public const string DeviceModsDir = DeviceModsRoot + "/Mods";
    public const string DeviceConfigDir = DeviceModsRoot + "/Config";
    public const string DeviceSafeModeFlag = DeviceModsRoot + "/disable_mods";
    public const string DeviceLoaderLog = DeviceModsRoot + "/Logs/latest.log";

    /// <summary>Mod folders used by older kits (only read for diagnostics and restore).</summary>
    public static readonly string[] LegacyDeviceModsRoots =
    {
        "/sdcard/NightClubMods",
        DeviceExternalData + "/files/NightClubMods",
        "/sdcard/NCMKMods",
    };

    public static bool IsTargetPackage(string? package) =>
        string.Equals(package, PackageName, StringComparison.Ordinal);
}
