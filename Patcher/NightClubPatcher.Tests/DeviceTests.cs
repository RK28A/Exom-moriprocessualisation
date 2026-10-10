using NightClubPatcher.Core;
using NightClubPatcher.Core.Adb;
using NightClubPatcher.Core.Android;
using NightClubPatcher.Core.Infrastructure;
using Xunit;

namespace NightClubPatcher.Tests;

public class DeviceTests
{
    [Theory]
    [InlineData("Quest 2", "hollywood", QuestModel.Quest2)]
    [InlineData("Quest Pro", "seacliff", QuestModel.QuestPro)]
    [InlineData("Quest 3", "eureka", QuestModel.Quest3)]
    [InlineData("Quest 3S", "panther", QuestModel.Quest3S)]
    [InlineData("Quest 3S Xbox Edition", "panther", QuestModel.Quest3S)]
    [InlineData("Quest_3S", "", QuestModel.Quest3S)]      // adb devices -l spelling, no codename
    [InlineData("Quest_3", "", QuestModel.Quest3)]        // must not be confused with 3S
    [InlineData("Quest", "monterey", QuestModel.Quest1)]
    [InlineData("Pixel 8", "shiba", QuestModel.NotQuest)]
    public void Identifies_headsets(string model, string codename, QuestModel expected)
    {
        Assert.Equal(expected, QuestDevices.Identify(model, codename).Model);
    }

    [Fact]
    public void Future_quest_is_treated_as_supported_unknown()
    {
        var q = QuestDevices.Identify("Quest 4", "nextgen", "Oculus");
        Assert.Equal(QuestModel.Unknown, q.Model);
        Assert.True(q.Supported);
        Assert.Equal("Meta Quest 4", q.DisplayName);
    }

    [Fact]
    public void Parses_adb_devices_output()
    {
        const string output = """
            List of devices attached
            2G0YC1ZF8B0123          device usb:1-1 product:eureka model:Quest_3 device:eureka transport_id:3
            340YC10G9X0456          device usb:1-2 product:panther model:Quest_3S device:panther transport_id:4
            192.168.1.40:5555       unauthorized transport_id:5
            1WMHH000000789          offline usb:1-3 product:hollywood model:Quest_2 device:hollywood transport_id:6

            """;
        var devices = AdbClient.ParseDevices(output);
        Assert.Equal(4, devices.Count);
        Assert.Equal(QuestModel.Quest3, devices[0].Headset.Model);
        Assert.Equal("Quest 3", devices[0].Model);
        Assert.True(devices[0].Ready);
        Assert.Equal(QuestModel.Quest3S, devices[1].Headset.Model);
        Assert.Contains("Meta Quest 3S", devices[1].Label);
        Assert.False(devices[2].Ready);
        Assert.True(devices[2].IsWireless);
        Assert.Contains("UNAUTHORIZED", devices[2].Label);
        Assert.Equal(QuestModel.Quest2, devices[3].Headset.Model);
        Assert.Equal("offline", devices[3].State);
    }

    [Fact]
    public void Manifest_supported_devices_are_read()
    {
        var m = AxmlManifest.Parse(AxmlWriter.Manifest(GameConstants.PackageName, "1.1.0", 212));
        Assert.Equal(AxmlWriter.DefaultSupportedDevices, m.SupportedDevices);
    }

    [Fact]
    public void Compatibility_against_manifest()
    {
        var q3s = QuestDevices.Identify("Quest 3S", "panther");
        Assert.True(QuestDevices.Check(q3s, "quest2|questpro|quest3|quest3s").ListedInManifest);
        Assert.Empty(QuestDevices.Check(q3s, "quest2|questpro|quest3|quest3s").Warnings);

        var notListed = QuestDevices.Check(q3s, "quest2|questpro|quest3");
        Assert.True(notListed.Supported);          // sideload still allowed...
        Assert.False(notListed.ListedInManifest);   // ...but the user is warned
        Assert.Single(notListed.Warnings);

        Assert.True(QuestDevices.Check(QuestDevices.Identify("Quest 3", "eureka"), "eureka|quest2").ListedInManifest);
        Assert.True(QuestDevices.Check(q3s, null).Supported);
        Assert.False(QuestDevices.Check(QuestDevices.Identify("Quest", "monterey"), null).Supported);
        Assert.False(QuestDevices.Check(QuestDevices.NotQuest, null).Supported);
    }

    [Fact]
    public void Non_interactive_patch_without_adb_falls_back_to_backup()
    {
        using var tmp = new TempDir();
        var paths = new KitPaths(tmp.File("kit"));
        var svc = new ModKitService(paths, new KitLog(null));
        var ui = new NonInteractive(svc.Log);
        if (svc.Adb.Available) return; // only meaningful on machines without adb

        Assert.False(svc.SelectDevice(ui, DevicePurpose.PatchAndInstall));
        Assert.False(svc.SelectDevice(ui, DevicePurpose.PatchOnly));     // no backup yet
        svc.Backups.ImportFromFile(FakeApk.Create(tmp.File("game.apk")), new Core.Apk.ApkInspectorOptions());
        Assert.True(svc.SelectDevice(ui, DevicePurpose.PatchOnly));      // build-only from backup
    }
}
