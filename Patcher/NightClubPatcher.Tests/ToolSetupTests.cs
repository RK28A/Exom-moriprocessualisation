using System.IO.Compression;
using NightClubPatcher.Core.Signing;
using Xunit;

namespace NightClubPatcher.Tests;

public class ToolSetupTests
{
    private const string Sample = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <sdk:sdk-repository xmlns:sdk="http://schemas.android.com/sdk/android/repo/repository2/03">
          <channel id="channel-0">stable</channel>
          <channel id="channel-3">canary</channel>
          <remotePackage path="platform-tools">
            <revision><major>35</major><minor>0</minor><micro>2</micro></revision>
            <archives>
              <archive><complete><size>6</size><checksum type="sha1">aaa</checksum><url>platform-tools_r35.0.2-win.zip</url></complete><host-os>windows</host-os></archive>
              <archive><complete><size>6</size><checksum type="sha1">bbb</checksum><url>platform-tools_r35.0.2-linux.zip</url></complete><host-os>linux</host-os></archive>
            </archives>
          </remotePackage>
          <remotePackage path="build-tools;34.0.0">
            <revision><major>34</major><minor>0</minor><micro>0</micro></revision>
            <archives><archive><complete><size>1</size><checksum>c34</checksum><url>build-tools_r34-windows.zip</url></complete><host-os>windows</host-os></archive></archives>
          </remotePackage>
          <remotePackage path="build-tools;35.0.0">
            <revision><major>35</major><minor>0</minor><micro>0</micro></revision>
            <archives><archive><complete><size>1</size><checksum>c35</checksum><url>build-tools_r35_windows.zip</url></complete><host-os>windows</host-os></archive></archives>
          </remotePackage>
          <remotePackage path="build-tools;36.0.0-rc1">
            <revision><major>36</major><minor>0</minor><micro>0</micro><preview>1</preview></revision>
            <channelRef ref="channel-3"/>
            <archives><archive><complete><size>1</size><checksum>c36</checksum><url>build-tools_r36-rc1_windows.zip</url></complete><host-os>windows</host-os></archive></archives>
          </remotePackage>
        </sdk:sdk-repository>
        """;

    [Fact]
    public void Picks_latest_stable_package_for_host()
    {
        var repo = RemoteRepository.Parse(Sample.Trim());
        var pt = repo.Latest("platform-tools", "windows")!;
        Assert.Equal("platform-tools_r35.0.2-win.zip", pt.Url);
        Assert.Equal("aaa", pt.Sha1);
        Assert.Equal("bbb", repo.Latest("platform-tools", "linux")!.Sha1);

        var bt = repo.Latest("build-tools;", "windows")!;
        Assert.Equal("build-tools;35.0.0", bt.PackagePath);   // preview 36 ignored
        Assert.Equal(new Version(35, 0, 0), bt.Revision);
        Assert.Null(repo.Latest("build-tools;", "macosx"));
    }

    [Fact]
    public void Extract_strips_top_folder_and_blocks_path_traversal()
    {
        using var tmp = new TempDir();
        var ok = tmp.File("ok.zip");
        using (var z = ZipFile.Open(ok, ZipArchiveMode.Create))
        {
            z.CreateEntry("platform-tools/adb.exe").Open().Dispose();
            z.CreateEntry("platform-tools/lib/x.dll").Open().Dispose();
        }
        var dest = tmp.File("out");
        ToolSetup.ExtractZip(ok, dest, stripTopFolder: true);
        Assert.True(File.Exists(Path.Combine(dest, "adb.exe")));
        Assert.True(File.Exists(Path.Combine(dest, "lib", "x.dll")));

        var evil = tmp.File("evil.zip");
        using (var z = ZipFile.Open(evil, ZipArchiveMode.Create))
            z.CreateEntry("top/../../escaped.txt").Open().Dispose();
        Assert.Throws<InvalidDataException>(() => ToolSetup.ExtractZip(evil, tmp.File("out2"), stripTopFolder: true));
        Assert.False(File.Exists(tmp.File("escaped.txt")));
    }
}
