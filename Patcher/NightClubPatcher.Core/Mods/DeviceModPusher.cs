using NightClubPatcher.Core.Adb;
using NightClubPatcher.Core.Infrastructure;

namespace NightClubPatcher.Core.Mods;

public sealed record PushItem(string LocalPath, string RemotePath, bool OverwriteExisting);

public sealed record PushReport(bool Ok, int Pushed, int Skipped, IReadOnlyList<string> Errors)
{
    public string Summary => Ok
        ? $"{Pushed} file(s) pushed to {GameConstants.DeviceModsRoot}" + (Skipped > 0 ? $", {Skipped} existing config file(s) kept" : "")
        : $"{Errors.Count} error(s): " + string.Join("; ", Errors.Take(3));
}

/// <summary>
/// Copies external mods and config files to the folder the loader reads:
/// /sdcard/Android/data/&lt;package&gt;/files/NCMKMods. The game has no storage permission, so this
/// app-specific folder is the only one both adb and the game can use.
///
/// Local layout (kit folder):  DeviceMods/Mods/libncmkmod_*.so   -> NCMKMods/Mods/   (always replaced)
///                             DeviceMods/Config/*               -> NCMKMods/Config/ (only if absent on the device)
/// </summary>
public sealed class DeviceModPusher
{
    public const string DefaultConfigJson = "{\n  \"loader\": {\n    \"logging\": true,\n    \"safeMode\": false\n  },\n\n  \"mods\": {\n  }\n}\n";

    private readonly KitPaths _paths;
    private readonly KitLog _log;

    public DeviceModPusher(KitPaths paths, KitLog log)
    {
        _paths = paths;
        _log = log;
    }

    public static bool IsModFile(string fileName) =>
        fileName.EndsWith(".so", StringComparison.Ordinal) &&
        GameConstants.AllModLibPrefixes.Any(p => fileName.StartsWith(p, StringComparison.Ordinal));

    /// <summary>What would be pushed from <paramref name="localRoot"/> (DeviceMods/), in order.</summary>
    public static List<PushItem> Plan(string localRoot)
    {
        var items = new List<PushItem>();
        var mods = Path.Combine(localRoot, "Mods");
        if (Directory.Exists(mods))
            foreach (var f in Directory.EnumerateFiles(mods).Where(f => IsModFile(Path.GetFileName(f))).OrderBy(f => f, StringComparer.Ordinal))
                items.Add(new PushItem(f, GameConstants.DeviceModsDir + "/" + Path.GetFileName(f), true));

        var config = Path.Combine(localRoot, "Config");
        if (Directory.Exists(config))
            foreach (var f in Directory.EnumerateFiles(config).OrderBy(f => f, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(f);
                if (name.StartsWith('.') || name.Contains(' ')) continue;
                items.Add(new PushItem(f, GameConstants.DeviceConfigDir + "/" + name, false));
            }
        return items;
    }

    public PushReport Push(AdbClient adb, bool overwriteConfig = false)
    {
        var errors = new List<string>();
        int pushed = 0, skipped = 0;

        var mk = adb.Shell($"mkdir -p \"{GameConstants.DeviceModsDir}\" \"{GameConstants.DeviceConfigDir}\" \"{GameConstants.DeviceModsRoot}/Logs\" && echo ok");
        if (!mk.StdOut.Contains("ok"))
            return new PushReport(false, 0, 0, new[] { $"cannot create {GameConstants.DeviceModsRoot}: {mk.Combined.Trim()}" });

        var items = Plan(_paths.DeviceMods);
        // config.json is always present on the device after a push (never overwritten unless asked).
        if (!items.Any(i => i.RemotePath == GameConstants.DeviceConfigDir + "/config.json"))
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"ncmk-config-{Guid.NewGuid():N}.json");
            File.WriteAllText(tmp, DefaultConfigJson);
            items.Add(new PushItem(tmp, GameConstants.DeviceConfigDir + "/config.json", false));
        }

        foreach (var item in items)
        {
            if (!item.OverwriteExisting && !overwriteConfig && adb.RemoteExists(item.RemotePath))
            {
                _log.Info($"  kept {item.RemotePath} (already on the headset)");
                skipped++;
                continue;
            }
            if (adb.Push(item.LocalPath, item.RemotePath, TimeSpan.FromMinutes(5)))
            {
                _log.Info($"  pushed {Path.GetFileName(item.LocalPath)} -> {item.RemotePath}");
                pushed++;
            }
            else errors.Add($"push failed: {item.LocalPath} -> {item.RemotePath}");
        }
        return new PushReport(errors.Count == 0, pushed, skipped, errors);
    }
}
