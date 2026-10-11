using NightClubPatcher.Core;
using NightClubPatcher.Core.Diagnostics;
using NightClubPatcher.Core.Infrastructure;
using NightClubPatcher.Core.Mods;
using NightClubPatcher.Core.Patching;
using NightClubPatcher.Core.Signing;
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

    [Fact]
    public void Mod_catalog_follows_the_cmake_rules()
    {
        using var tmp = new TempDir();
        var paths = new KitPaths(tmp.File("kit"));
        var builder = new NativeBuilder(paths, new ToolLocator(paths), new KitLog(null));
        Directory.CreateDirectory(Path.Combine(paths.Root, "Native"));
        File.WriteAllText(Path.Combine(paths.Root, "Native", "CMakeLists.txt"), "");
        Directory.CreateDirectory(Path.Combine(paths.Root, "SDK", "Core"));
        File.WriteAllText(Path.Combine(paths.Root, "SDK", "Core", "ncmk_mod_api.h"), "");
        Assert.Contains("kit\\Mods is missing, re-run Build-Windows.ps1", builder.MissingSourcesMessage());

        void Mod(string folder, string? json, params string[] files)
        {
            var d = Path.Combine(paths.Root, "Mods", folder);
            Directory.CreateDirectory(d);
            if (json != null) File.WriteAllText(Path.Combine(d, "mod.json"), json);
            foreach (var f in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(d, f))!);
                File.WriteAllText(Path.Combine(d, f), "");
            }
        }
        Mod("Pulse", "{\"library\":\"libncmkmod_pulse.so\"}", "pulse.cpp", "ui/panel.cpp");
        Mod("Glow Fx", "{}", "glow.cpp");                     // default name from the folder
        Mod("NoJson", null, "x.cpp");                         // ignored
        Mod("Off", "{}", "off.cpp", ".disabled");
        Mod("Empty", "{}");
        Assert.Null(builder.MissingSourcesMessage());

        var mods = builder.DiscoverMods();
        Assert.Equal(new[] { "Empty", "Glow Fx", "Off", "Pulse" }, mods.Select(m => m.Name));
        var pulse = mods.Single(m => m.Name == "Pulse");
        Assert.Equal(("libncmkmod_pulse.so", 2, true), (pulse.Library, pulse.SourceCount, pulse.WillBuild));
        Assert.Equal("libncmkmod_glow_fx.so", mods.Single(m => m.Name == "Glow Fx").Library);
        Assert.False(mods.Single(m => m.Name == "Off").WillBuild);
        Assert.False(mods.Single(m => m.Name == "Empty").WillBuild);

        Mod("Pulse2", "{\"library\":\"libncmkmod_pulse.so\"}", "p.cpp");
        Mod("Bad", "{\"library\":\"libfoo.so\"}", "b.cpp");
        mods = builder.DiscoverMods();
        Assert.Contains("same output libncmkmod_pulse.so as Mods/Pulse2", mods.Single(m => m.Name == "Pulse").Error);
        Assert.Contains("must look like libncmkmod_<name>.so", mods.Single(m => m.Name == "Bad").Error);
    }
}
