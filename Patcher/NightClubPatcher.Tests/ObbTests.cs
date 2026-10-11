using NightClubPatcher.Core;
using NightClubPatcher.Core.Adb;
using NightClubPatcher.Core.Infrastructure;
using NightClubPatcher.Core.Recovery;
using Xunit;

namespace NightClubPatcher.Tests;

public class ObbTests
{
    private const string Obb = "/sdcard/Android/obb/" + GameConstants.PackageName;

    [Fact]
    public void Listing_and_md5_output_are_parsed()
    {
        var files = ObbService.ParseListing($"1500|{Obb}/main.212.{GameConstants.PackageName}.obb\nfind: x: Permission denied\n7|{Obb}/sub/a.bin\n", Obb);
        Assert.Equal(2, files.Count);
        Assert.Equal($"main.212.{GameConstants.PackageName}.obb", files[0].RelativePath);
        Assert.Equal(1500, files[0].Size);
        Assert.Equal("sub/a.bin", files[1].RelativePath);
        var md5 = ObbService.ParseMd5("d41d8cd98f00b204e9800998ecf8427e  ./main.212.x.obb\r\nnoise\n");
        Assert.Equal("d41d8cd98f00b204e9800998ecf8427e", md5["main.212.x.obb"]);
    }

    /// <summary>A bash "adb" mapping /sdcard to a local folder (Linux/macOS only).</summary>
    private sealed class FakeQuest : IDisposable
    {
        public readonly TempDir Tmp = new();
        public string Root => Tmp.File("device");
        public string DeviceObb => Path.Combine(Root, "sdcard", "Android", "obb", GameConstants.PackageName);
        public readonly ObbService Service;
        public readonly AdbClient Adb;
        public readonly KitPaths Paths;

        public FakeQuest()
        {
            var adb = Tmp.File("adb");
            File.WriteAllText(adb, """
                #!/usr/bin/env bash
                ROOT="$FAKE_ROOT"
                map() { local p="$1"; p="${p//\/storage\/emulated\/0/$ROOT/sdcard}"; p="${p//\/sdcard/$ROOT/sdcard}"; printf '%s' "$p"; }
                unmap() { sed "s|$ROOT/sdcard|/sdcard|g"; }
                [ "$1" = "-s" ] && shift 2
                cmd="$1"; shift
                case "$cmd" in
                  shell) sh -c "$(map "$1")" 2>&1 | unmap; exit 0 ;;
                  pull)
                    if [ -n "$FAIL_PULL" ]; then echo "adb: error: failed to copy" >&2; exit 1; fi
                    m="$(map "$1")"; mkdir -p "$(dirname "$2")"; cp "$m" "$2"; exit $? ;;
                  push)
                    m="$(map "$2")"; mkdir -p "$(dirname "$m")"; cp "$1" "$m"; exit $? ;;
                  *) exit 0 ;;
                esac
                """.Replace("\r\n", "\n"));
            File.SetUnixFileMode(adb, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.CreateDirectory(DeviceObb);
            File.WriteAllBytes(Path.Combine(DeviceObb, $"main.212.{GameConstants.PackageName}.obb"),
                Enumerable.Range(0, 200_000).Select(i => (byte)(i * 7)).ToArray());
            Environment.SetEnvironmentVariable("FAKE_ROOT", Root);
            Paths = new KitPaths(Tmp.File("kit"));
            Paths.EnsureCreated();
            var log = new KitLog(null);
            Service = new ObbService(Paths, log);
            Adb = new AdbClient(adb, log);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("FAKE_ROOT", null);
            Environment.SetEnvironmentVariable("FAIL_PULL", null);
            Tmp.Dispose();
        }
    }

    private static bool CanRunFakeAdb => !OperatingSystem.IsWindows() && File.Exists("/bin/bash") && File.Exists("/usr/bin/find");

    [Fact]
    public void Obb_is_saved_verified_and_put_back_after_the_uninstall_deleted_it()
    {
        if (!CanRunFakeAdb) return;
        using var q = new FakeQuest();
        var saved = q.Service.Save(q.Adb);
        Assert.True(saved.Ok, saved.Message);
        Assert.True(saved.DeviceHadObb);
        Assert.Single(saved.Files);
        Assert.NotNull(saved.Files[0].Md5);                // verified by MD5 against the headset
        Assert.Single(q.Service.SavedFor(212));
        Assert.Empty(q.Service.SavedFor(213));

        // Second save: identical file is reused, not copied again.
        Assert.True(q.Service.Save(q.Adb).Ok);

        Directory.Delete(q.DeviceObb, true);                // what "adb uninstall" does
        Assert.True(q.Service.Restore(q.Adb, saved.Files));
        var back = Path.Combine(q.DeviceObb, $"main.212.{GameConstants.PackageName}.obb");
        Assert.Equal(200_000, new FileInfo(back).Length);
        Assert.Equal(ObbService.LocalMd5(Path.Combine(q.Service.LocalDir, $"main.212.{GameConstants.PackageName}.obb")), ObbService.LocalMd5(back));
    }

    [Fact]
    public void Failed_copy_is_reported_and_nothing_is_kept()
    {
        if (!CanRunFakeAdb) return;
        using var q = new FakeQuest();
        Environment.SetEnvironmentVariable("FAIL_PULL", "1");
        var saved = q.Service.Save(q.Adb);
        Assert.False(saved.Ok);
        Assert.Empty(q.Service.SavedFor(212));
    }

    [Fact]
    public void Empty_obb_folder_is_not_an_error()
    {
        if (!CanRunFakeAdb) return;
        using var q = new FakeQuest();
        Directory.Delete(q.DeviceObb, true);
        var saved = q.Service.Save(q.Adb);
        Assert.True(saved.Ok);
        Assert.False(saved.DeviceHadObb);
    }
}
