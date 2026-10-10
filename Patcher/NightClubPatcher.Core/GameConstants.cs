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

    public const string LoaderLibName = "libncsloader.so";
    public const string LoaderEntry = LibDir + LoaderLibName;
    public const string ModLibPrefix = "libncsmod_";
    public const string PatchMarkerEntry = "assets/NightClubModKit/patch.json";

    /// <summary>API version implemented by the native loader shipped with this patcher.</summary>
    public const int LoaderApi = 1;

    // On-device locations.
    public const string DeviceModsRoot = "/sdcard/NightClubMods";
    public const string DeviceModsDisabled = "/sdcard/NightClubMods.disabled";
    public const string DeviceSafeModeFlag = DeviceModsRoot + "/disable_mods";
    public const string DeviceLoaderLog = DeviceModsRoot + "/Logs/latest.log";
    public const string DeviceExternalData = "/sdcard/Android/data/" + PackageName;
    public const string DeviceObbDir = "/sdcard/Android/obb/" + PackageName;
    public const string DeviceFallbackModsRoot = DeviceExternalData + "/files/NightClubMods";

    public static bool IsTargetPackage(string? package) =>
        string.Equals(package, PackageName, StringComparison.Ordinal);
}
