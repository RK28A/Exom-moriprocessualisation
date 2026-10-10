using NightClubPatcher.Core;
using NightClubPatcher.Core.Diagnostics;
using NightClubPatcher.Core.Mods;
using Xunit;

namespace NightClubPatcher.Tests;

public class NcmkTests
{
    [Fact]
    public void Mods_root_is_the_game_external_files_folder()
    {
        // The game has no storage permission: only its own Android/data folder is usable.
        Assert.Equal("/sdcard/Android/data/com.mandegames.NightClubSimulater/files/NCMKMods", GameConstants.DeviceModsRoot);
        Assert.StartsWith(GameConstants.DeviceModsRoot, GameConstants.DeviceSafeModeFlag);
        Assert.StartsWith(GameConstants.DeviceModsRoot, GameConstants.DeviceLoaderLog);
        Assert.Equal(2, GameConstants.LoaderApi);
    }

    [Theory]
    [InlineData("lib/arm64-v8a/libncmkmod_x.so", true)]
    [InlineData("lib/arm64-v8a/libncsmod_x.so", true)]
    [InlineData("lib/arm64-v8a/libncmkloader.so", false)]
    [InlineData("lib/armeabi-v7a/libncmkmod_x.so", false)]
    [InlineData("lib/arm64-v8a/libncmkmod_x.txt", false)]
    public void Mod_entries_match_current_and_legacy_prefixes(string entry, bool expected) =>
        Assert.Equal(expected, GameConstants.IsModLibEntry(entry));

    [Fact]
    public void Push_plan_replaces_mods_and_keeps_existing_config()
    {
        using var tmp = new TempDir();
        var root = tmp.File("DeviceMods");
        Directory.CreateDirectory(Path.Combine(root, "Mods"));
        Directory.CreateDirectory(Path.Combine(root, "Config"));
        File.WriteAllText(Path.Combine(root, "Mods", "libncmkmod_b.so"), "");
        File.WriteAllText(Path.Combine(root, "Mods", "libncsmod_a.so"), "");
        File.WriteAllText(Path.Combine(root, "Mods", "readme.txt"), "");
        File.WriteAllText(Path.Combine(root, "Mods", ".gitkeep"), "");
        File.WriteAllText(Path.Combine(root, "Config", "config.json"), "{}");
        File.WriteAllText(Path.Combine(root, "Config", "ncmk.debugoverlay.cfg"), "a=1");
        File.WriteAllText(Path.Combine(root, "Config", ".gitkeep"), "");

        var plan = DeviceModPusher.Plan(root);
        Assert.Equal(new[]
        {
            GameConstants.DeviceModsDir + "/libncmkmod_b.so",
            GameConstants.DeviceModsDir + "/libncsmod_a.so",
            GameConstants.DeviceConfigDir + "/config.json",
            GameConstants.DeviceConfigDir + "/ncmk.debugoverlay.cfg",
        }, plan.Select(p => p.RemotePath));
        Assert.All(plan.Where(p => p.RemotePath.Contains("/Mods/")), p => Assert.True(p.OverwriteExisting));
        Assert.All(plan.Where(p => p.RemotePath.Contains("/Config/")), p => Assert.False(p.OverwriteExisting));
        Assert.Empty(DeviceModPusher.Plan(tmp.File("missing")));
    }

    [Fact]
    public void Entitlement_failures_are_reported_from_logcat()
    {
        var lines = new[]
        {
            "10-10 12:00:00.000  1234  1250 I Unity   : MetaAccount.OnUseEntitlement: entitlement check passed",
            "10-10 12:00:01.000  1234  1250 I NCMK    : [NCMK] STAGE 08 Mods loaded",
            "10-10 12:00:02.000  1234  1250 E Unity   : MetaAccount.OnUseEntitlement: Entitlement check FAILED (user not entitled)",
        };
        var hits = LogcatFilter.EntitlementFailures(lines);
        Assert.Single(hits);
        Assert.Contains("FAILED", hits[0]);
        Assert.Matches(LogcatFilter.Relevant(), lines[2]);
    }
}
