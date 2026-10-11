using System.Formats.Tar;
using NightClubPatcher.Core;
using NightClubPatcher.Core.Adb;
using NightClubPatcher.Core.Infrastructure;
using NightClubPatcher.Core.Recovery;
using Xunit;

namespace NightClubPatcher.Tests;

public class GameDataBackupTests
{
    private const string Data = "/sdcard/Android/data/" + GameConstants.PackageName;

    [Fact]
    public void Inventory_output_is_parsed_and_errors_are_kept()
    {
        var output = $"""
            120|{Data}/files/save.dat
            7|{Data}/files/sub dir/profile.json
            find: '{Data}/files/locked': Permission denied
            __NCMK_END__:1
            """;
        var (files, errors) = UserDataService.ParseInventory(output, Data);
        Assert.Equal(new[] { "files/save.dat", "files/sub dir/profile.json" }, files.Select(f => f.RelativePath));
        Assert.Equal(120, files[0].Size);
        Assert.Single(errors);
        Assert.Contains("Permission denied", errors[0]);
    }

    [Fact]
    public void Local_verification_checks_presence_and_size()
    {
        using var tmp = new TempDir();
        var report = new FolderBackupReport { Label = "Android/data", RemotePath = Data, LocalPath = tmp.File("data/pkg"), Exists = true, InventoryComplete = true };
        report.Inventory.Add(new RemoteFile("files/a.dat", 3));
        report.Inventory.Add(new RemoteFile("files/b.dat", 5));
        report.Inventory.Add(new RemoteFile("files/c.dat", 1));
        Directory.CreateDirectory(tmp.File("data/pkg/files"));
        File.WriteAllText(tmp.File("data/pkg/files/a.dat"), "abc");
        File.WriteAllText(tmp.File("data/pkg/files/b.dat"), "ab");    // truncated
        UserDataService.VerifyLocal(report);
        Assert.Equal(new[] { "files/a.dat" }, report.Saved);
        Assert.Equal(2, report.Missing.Count);
        Assert.False(report.Complete);
        Assert.Contains("INCOMPLETE", report.Summary);
        Assert.Contains("files/c.dat", report.Summary);
    }

    [Fact]
    public void Absent_or_empty_folders_count_as_complete()
    {
        var absent = new FolderBackupReport { Label = "Android/obb", RemotePath = "/x", LocalPath = "/y", Exists = false };
        Assert.True(absent.Complete);
        Assert.Contains("does not exist", absent.Summary);
        var unlisted = new FolderBackupReport { Label = "Android/data", RemotePath = "/x", LocalPath = "/y", Exists = true, InventoryComplete = false };
        Assert.False(unlisted.Complete); // could not list = cannot prove anything was saved
    }

    [Fact]
    public void Device_tar_is_extracted_safely()
    {
        using var tmp = new TempDir();
        var good = tmp.File("good.tar");
        using (var fs = File.Create(good))
        using (var w = new TarWriter(fs))
            w.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "com.pkg/files/save.dat") { DataStream = new MemoryStream(new byte[] { 1, 2 }) });
        UserDataService.ExtractTarSafely(good, tmp.File("out"));
        Assert.Equal(2, new FileInfo(tmp.File("out/com.pkg/files/save.dat")).Length);

        var evil = tmp.File("evil.tar");
        using (var fs = File.Create(evil))
        using (var w = new TarWriter(fs))
            w.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "../escape.txt") { DataStream = new MemoryStream(new byte[] { 1 }) });
        Assert.Throws<InvalidDataException>(() => UserDataService.ExtractTarSafely(evil, tmp.File("out2")));
    }

    // ------------------------------------------------------------------ end-to-end with a fake adb

    /// <summary>
    /// A bash "adb" that maps /sdcard and /storage/emulated/0 to a local folder and can be told to
    /// fail specific methods (FAIL_PULL regex, FAIL_TAR, FAIL_CP, FAIL_FILE regex).
    /// </summary>
    private sealed class FakeQuest : IDisposable
    {
        public readonly TempDir Tmp = new();
        public string Root => Tmp.File("device");
        public string AdbPath => Tmp.File("adb");

        public FakeQuest()
        {
            File.WriteAllText(AdbPath, """
                #!/usr/bin/env bash
                ROOT="$FAKE_ROOT"
                map() { local p="$1"; p="${p//\/storage\/emulated\/0/$ROOT/sdcard}"; p="${p//\/sdcard/$ROOT/sdcard}"; p="${p//\/data\/local\/tmp/$ROOT/tmp}"; printf '%s' "$p"; }
                unmap() { sed "s|$ROOT/sdcard|/sdcard|g; s|$ROOT/tmp|/data/local/tmp|g"; }
                [ "$1" = "-s" ] && shift 2
                cmd="$1"; shift
                case "$cmd" in
                  shell)
                    sh -c "$(map "$1")" 2>&1 | unmap; exit 0 ;;
                  exec-out)
                    if [ -n "$FAIL_TAR" ] && [[ "$1" == tar* ]]; then echo "tar: denied" >&2; exit 1; fi
                    sh -c "$(map "$1")"; exit $? ;;
                  pull)
                    src="$1"; dst="$2"
                    if [ -n "$FAIL_PULL" ] && [[ "$src" =~ $FAIL_PULL ]]; then echo "adb: error: failed to copy '$src'" >&2; exit 1; fi
                    if [ -n "$FAIL_FILE" ] && [[ "$src" =~ $FAIL_FILE ]]; then echo "adb: error: open failed: Permission denied" >&2; exit 1; fi
                    m="$(map "$src")"
                    if [ -d "$m" ]; then
                      if [ -d "$dst" ]; then cp -r "$m" "$dst/"; else cp -r "$m" "$dst"; fi
                    else
                      mkdir -p "$(dirname "$dst")"; cp "$m" "$dst"
                    fi
                    exit $? ;;
                  push)
                    m="$(map "$2")"; mkdir -p "$m"; cp -r "$1" "$m"; exit $? ;;
                  version) echo "Android Debug Bridge version 1.0.41"; echo "Version 37.0.1-fake"; exit 0 ;;
                  *) exit 0 ;;
                esac
                """.Replace("\r\n", "\n"));
            File.SetUnixFileMode(AdbPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.CreateDirectory(Path.Combine(Root, "tmp"));
            Write("sdcard/Android/data/" + GameConstants.PackageName + "/files/save.dat", 1200);
            Write("sdcard/Android/data/" + GameConstants.PackageName + "/files/Unity/profile secret.json", 64);
            Write("sdcard/Android/data/" + GameConstants.PackageName + "/files/settings.cfg", 10);
            Write("sdcard/Android/obb/" + GameConstants.PackageName + "/main.212.obb", 4096);
            Environment.SetEnvironmentVariable("FAKE_ROOT", Root);
        }

        private void Write(string rel, int size)
        {
            var p = Path.Combine(Root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, Enumerable.Range(0, size).Select(i => (byte)i).ToArray());
        }

        public UserDataService? Service;
        public AdbClient? Adb;

        public UserDataBackup RunBackup(string? failPull = null, bool failTar = false, bool failCp = false, string? failFile = null)
        {
            Environment.SetEnvironmentVariable("FAIL_PULL", failPull);
            Environment.SetEnvironmentVariable("FAIL_TAR", failTar ? "1" : null);
            Environment.SetEnvironmentVariable("FAIL_FILE", failFile);
            if (failCp) File.WriteAllText(Path.Combine(Root, "sdcard", "Download"), "not a directory: mkdir -p fails");
            var paths = new KitPaths(Tmp.File("kit"));
            paths.EnsureCreated();
            var log = new KitLog(null);
            Service = new UserDataService(paths, log);
            Adb = new AdbClient(AdbPath, log);
            return Service.Backup(Adb);
        }

        public void Dispose()
        {
            foreach (var v in new[] { "FAKE_ROOT", "FAIL_PULL", "FAIL_TAR", "FAIL_FILE" }) Environment.SetEnvironmentVariable(v, null);
            Tmp.Dispose();
        }
    }

    private static bool CanRunFakeAdb => !OperatingSystem.IsWindows() && File.Exists("/bin/bash") && File.Exists("/usr/bin/find");

    [Fact]
    public void Folder_pull_fails_but_tar_stream_saves_everything()
    {
        if (!CanRunFakeAdb) return;
        using var q = new FakeQuest();
        var b = q.RunBackup(failPull: "Android/data/[^/]+$");   // like the reported "FAILED to pull Android/data"
        Assert.True(b.ExternalData.Exists);
        Assert.Equal(3, b.ExternalData.Inventory.Count);
        Assert.True(b.ExternalData.Complete, b.ExternalData.Summary);
        Assert.Equal("tar stream (adb exec-out)", b.ExternalData.Method);
        Assert.True(b.Obb.Complete);
        Assert.True(b.AccessibleDataComplete);
        Assert.True(File.Exists(Path.Combine(b.Directory, "inventory.txt")));
        Assert.Contains("profile secret.json", File.ReadAllText(Path.Combine(b.Directory, "inventory.txt")));
    }

    [Fact]
    public void Staging_copy_is_used_when_pull_and_tar_fail()
    {
        if (!CanRunFakeAdb) return;
        using var q = new FakeQuest();
        var b = q.RunBackup(failPull: "Android/data/[^/]+$", failTar: true);
        Assert.True(b.ExternalData.Complete, b.ExternalData.Summary);
        Assert.Equal("staging copy in /sdcard/Download", b.ExternalData.Method);
        Assert.False(Directory.Exists(Path.Combine(q.Root, "sdcard", "Download", "NCMK-staging")) &&
                     Directory.EnumerateFileSystemEntries(Path.Combine(q.Root, "sdcard", "Download", "NCMK-staging")).Any());
    }

    [Fact]
    public void Unreadable_file_is_reported_exactly_and_backup_is_incomplete()
    {
        if (!CanRunFakeAdb) return;
        using var q = new FakeQuest();
        var b = q.RunBackup(failPull: "Android/data/[^/]+$", failTar: true, failCp: true, failFile: "secret");
        Assert.False(b.ExternalData.Complete);
        Assert.False(b.AccessibleDataComplete);
        Assert.Equal(2, b.ExternalData.Saved.Count);                   // file-by-file saved the readable ones
        Assert.Contains(b.ExternalData.Missing, m => m.Contains("profile secret.json"));
        Assert.Contains("INCOMPLETE", b.Describe());
        Assert.Contains("missing: files/Unity/profile secret.json", b.Describe());
        Assert.Contains("/data/data", b.Describe());
    }

    [Theory]
    [InlineData("keep", GameDataMode.Keep)]
    [InlineData("backup-only", GameDataMode.BackupOnly)]
    [InlineData("Skip", GameDataMode.Skip)]
    [InlineData("clean", GameDataMode.Clean)]
    [InlineData("Normal restore", GameDataMode.Keep)]
    [InlineData("Clean install", GameDataMode.Clean)]
    public void Game_data_modes_parse(string text, GameDataMode expected)
    {
        Assert.True(GameDataModes.TryParse(text, out var m));
        Assert.Equal(expected, m);
        Assert.True(GameDataModes.TryParse(m.CliName(), out var back));
        Assert.Equal(m, back);
        Assert.Equal(m == GameDataMode.Keep, m.Restores());
        Assert.Equal(m != GameDataMode.Skip, m.BacksUp());
    }

    [Fact]
    public void Unknown_game_data_mode_is_rejected() => Assert.False(GameDataModes.TryParse("wipe", out _));

    [Fact]
    public void Md5_output_is_parsed()
    {
        var map = UserDataService.ParseMd5("d41d8cd98f00b204e9800998ecf8427e  ./files/a b.txt\r\nnot a line\n0cc175b9c0f1b6a831c399e269772661  ./files/x\n__NCMK_END__:0");
        Assert.NotNull(map);
        Assert.Equal(2, map!.Count);
        Assert.Equal("d41d8cd98f00b204e9800998ecf8427e", map["files/a b.txt"]);
        Assert.Null(UserDataService.ParseMd5("sh: md5sum: not found\n__NCMK_END__:127"));
    }

    [Theory]
    [InlineData("files/save.dat", true)]
    [InlineData("files/Unity/abc/Analytics/values", true)]
    [InlineData("files/NCMKMods/Config/ncmk.debugoverlay.cfg", true)]
    [InlineData("cache/UnityShaderCache/x.bin", false)]
    [InlineData("files/il2cpp/Metadata/global-metadata.dat", false)]
    [InlineData("files/NCMKMods/Mods/libncmkmod_old.so", false)]
    [InlineData("files/NCMKMods/Logs/latest.log", false)]
    [InlineData("files/NCMKMods/disable_mods", false)]
    [InlineData("files/vulkan_pso_cache.bin", false)]
    public void Caches_and_ncmk_binaries_are_never_restored(string path, bool restorable) =>
        Assert.Equal(restorable, UserDataService.IsRestorable(path));

    [Fact]
    public void Restore_tar_uses_gnu_entries_under_the_package_folder()
    {
        using var tmp = new TempDir();
        var local = tmp.File(Path.Combine("data", GameConstants.PackageName));
        Directory.CreateDirectory(Path.Combine(local, "files", "sub"));
        File.WriteAllText(Path.Combine(local, "files", "sub", "a.txt"), "hello");
        var report = new FolderBackupReport { Label = "Android/data", RemotePath = Data, LocalPath = local };
        var tar = tmp.File("r.tar");
        UserDataService.WriteRestoreTar(tar, report, GameConstants.PackageName, new[] { new RemoteFile("files/sub/a.txt", 5) });
        using var fs = File.OpenRead(tar);
        using var reader = new TarReader(fs);
        var names = new List<string>();
        while (reader.GetNextEntry() is { } e)
        {
            Assert.Equal(TarEntryFormat.Gnu, e.Format);
            names.Add(e.Name);
        }
        Assert.Equal(new[] { GameConstants.PackageName + "/files/", GameConstants.PackageName + "/files/sub/", GameConstants.PackageName + "/files/sub/a.txt" }, names);
    }

    [Fact]
    public void Restore_skips_caches_and_never_pushes_a_partial_file()
    {
        if (!CanRunFakeAdb) return;
        using var q = new FakeQuest();
        var pkgDir = Path.Combine(q.Root, "sdcard", "Android", "data", GameConstants.PackageName);
        Directory.CreateDirectory(Path.Combine(pkgDir, "files", "il2cpp"));
        File.WriteAllText(Path.Combine(pkgDir, "files", "il2cpp", "cache.dat"), "stale build cache");
        var b = q.RunBackup();
        Assert.True(b.ExternalData.Complete, b.ExternalData.Summary);
        Assert.True(b.ExternalData.ContentVerified);

        // A leftover partial file in the backup folder (e.g. from an interrupted pull) must not reach the headset.
        File.WriteAllText(Path.Combine(b.ExternalData.LocalPath, "files", "partial.tmp"), "half");
        Directory.Delete(pkgDir, true);
        Directory.Delete(Path.Combine(q.Root, "sdcard", "Android", "obb", GameConstants.PackageName), true);

        Assert.True(q.Service!.Restore(q.Adb!, b));
        Assert.True(File.Exists(Path.Combine(pkgDir, "files", "save.dat")));
        Assert.False(File.Exists(Path.Combine(pkgDir, "files", "partial.tmp")));
        Assert.False(Directory.Exists(Path.Combine(pkgDir, "files", "il2cpp")));
        Assert.False(Directory.Exists(Path.Combine(pkgDir, GameConstants.PackageName)));   // no nested <pkg>/<pkg>
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(b.ExternalData.LocalPath)!, ".restore-*"));
    }

    [Fact]
    public void Restore_reports_a_file_whose_backup_copy_changed()
    {
        if (!CanRunFakeAdb) return;
        using var q = new FakeQuest();
        var b = q.RunBackup();
        File.WriteAllText(Path.Combine(b.ExternalData.LocalPath, "files", "settings.cfg"), "corrupted!!");  // size changed
        Assert.False(q.Service!.Restore(q.Adb!, b));
    }

    [Fact]
    public void Restore_puts_every_saved_file_back_and_verifies_it()
    {
        if (!CanRunFakeAdb) return;
        using var q = new FakeQuest();
        var b = q.RunBackup(failPull: "Android/data/[^/]+$");
        Assert.True(b.AccessibleDataComplete);

        // Simulate the uninstall wiping the game's external data.
        Directory.Delete(Path.Combine(q.Root, "sdcard", "Android", "data", GameConstants.PackageName), true);
        Directory.Delete(Path.Combine(q.Root, "sdcard", "Android", "obb", GameConstants.PackageName), true);

        Assert.True(q.Service!.Restore(q.Adb!, b));
        var save = Path.Combine(q.Root, "sdcard", "Android", "data", GameConstants.PackageName, "files", "save.dat");
        Assert.Equal(1200, new FileInfo(save).Length);
        Assert.True(File.Exists(Path.Combine(q.Root, "sdcard", "Android", "obb", GameConstants.PackageName, "main.212.obb")));
    }
}
