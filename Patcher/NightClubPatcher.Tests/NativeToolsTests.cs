using System.Formats.Tar;
using System.IO.Compression;
using NightClubPatcher.Core.Infrastructure;
using NightClubPatcher.Core.Signing;
using Xunit;

namespace NightClubPatcher.Tests;

public class NativeToolsTests
{
    private static string Exe(string n) => OperatingSystem.IsWindows() ? n + ".exe" : n;

    private static void Touch(string path, string content = "")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static KitPaths Kit(TempDir tmp)
    {
        var paths = new KitPaths(tmp.File("kit"));
        paths.EnsureCreated();
        return paths;
    }

    [Fact]
    public void Bundled_native_tools_are_found_first_and_ndk_version_is_read()
    {
        using var tmp = new TempDir();
        var paths = Kit(tmp);
        var tools = new ToolLocator(paths) { BundledOnly = true };
        Assert.Null(tools.FindNdk());
        Assert.Null(tools.FindCMake());
        Assert.Null(tools.FindNinja());

        Touch(Path.Combine(paths.Tools, "ndk", "build", "cmake", "android.toolchain.cmake"));
        Touch(Path.Combine(paths.Tools, "ndk", "source.properties"), "Pkg.Desc = Android NDK\nPkg.Revision = 27.2.12479018\n");
        Touch(Path.Combine(paths.Tools, "cmake", "bin", Exe("cmake")));
        Touch(Path.Combine(paths.Tools, "ninja", Exe("ninja")));

        Assert.Equal(Path.Combine(paths.Tools, "ndk"), tools.FindNdk());
        Assert.Equal("27.2.12479018", ToolLocator.NdkVersion(tools.FindNdk()!));
        Assert.Equal(Path.Combine(paths.Tools, "cmake", "bin", Exe("cmake")), tools.FindCMake());
        Assert.Equal(Path.Combine(paths.Tools, "ninja", Exe("ninja")), tools.FindNinja());
        Assert.True(tools.IsBundled(tools.FindCMake()));
    }

    [Fact]
    public void Incomplete_ndk_folder_is_not_accepted()
    {
        using var tmp = new TempDir();
        var paths = Kit(tmp);
        Touch(Path.Combine(paths.Tools, "ndk", "source.properties"), "Pkg.Revision = 27.0.0\n");
        Assert.Null(new ToolLocator(paths) { BundledOnly = true }.FindNdk());
    }

    [Fact]
    public void Tools_manifest_round_trips_versions_and_details()
    {
        using var tmp = new TempDir();
        var file = tmp.File("Tools/tools.json");
        var m = ToolsManifest.Load(file);
        m.Set("ndk", "27.2.12479018", new Dictionary<string, string?> { ["sha256"] = "abc", ["url"] = "https://x" });
        m.Set("cmake", "3.31.7");
        var again = ToolsManifest.Load(file);
        Assert.Equal("27.2.12479018", again.Version("ndk"));
        Assert.Equal("3.31.7", again.Version("cmake"));
        Assert.Equal("abc", again.Detail("ndk", "sha256"));
        Assert.Null(again.Version("ninja"));

        File.WriteAllText(file, "{ not json");
        Assert.Null(ToolsManifest.Load(file).Version("ndk")); // corrupt manifest = re-detect, no crash
    }

    [Fact]
    public void Official_checksum_formats_are_parsed()
    {
        const string sums = """
            14e15d0b445dbeac686acc13fe13b3135e8307f69ccf4c5c91403996ce5aa2d4  cmake-3.31.7-linux-x86_64.tar.gz
            ab1c7f46a1b1314f9fcb766c2573148679af599d94c5566bc12b8b35bb563362  cmake-3.31.7-windows-x86_64.zip
            """;
        Assert.Equal("ab1c7f46a1b1314f9fcb766c2573148679af599d94c5566bc12b8b35bb563362",
            ToolSetup.ParseChecksumFile(sums, "cmake-3.31.7-windows-x86_64.zip"));
        Assert.Null(ToolSetup.ParseChecksumFile(sums, "cmake-3.31.7-windows-arm64.zip"));

        const string release = """
            {"tag_name":"v1.12.1","assets":[
              {"name":"ninja-linux.zip","digest":"sha256:6F98805688D19672BD699FBBFA2C2CF0FC054AC3DF1F0E6A47664D963D530255"},
              {"name":"ninja-win.zip","digest":null}]}
            """;
        Assert.Equal("6f98805688d19672bd699fbbfa2c2cf0fc054ac3df1f0e6a47664d963d530255", ToolSetup.ParseGitHubAssetDigest(release, "ninja-linux.zip"));
        Assert.Null(ToolSetup.ParseGitHubAssetDigest(release, "ninja-win.zip"));
    }

    [Fact]
    public void Ndk_prefers_the_newest_stable_r27()
    {
        const string xml = """
            <sdk:sdk-repository xmlns:sdk="http://schemas.android.com/sdk/android/repo/repository2/03">
              <remotePackage path="ndk;26.3.11579264"><revision><major>26</major><minor>3</minor><micro>11579264</micro></revision>
                <archives><archive><complete><size>1</size><checksum>a</checksum><url>ndk26-windows.zip</url></complete><host-os>windows</host-os></archive></archives></remotePackage>
              <remotePackage path="ndk;27.2.12479018"><revision><major>27</major><minor>2</minor><micro>12479018</micro></revision>
                <archives><archive><complete><size>1</size><checksum>b</checksum><url>android-ndk-r27c-windows.zip</url></complete><host-os>windows</host-os></archive></archives></remotePackage>
              <remotePackage path="ndk;28.0.13004108"><revision><major>28</major><minor>0</minor><micro>13004108</micro></revision>
                <archives><archive><complete><size>1</size><checksum>c</checksum><url>android-ndk-r28-windows.zip</url></complete><host-os>windows</host-os></archive></archives></remotePackage>
            </sdk:sdk-repository>
            """;
        var repo = RemoteRepository.Parse(xml);
        Assert.Equal("android-ndk-r27c-windows.zip", repo.LatestWithPrefix(ToolSetup.PreferredNdkPrefix, "windows")!.Url);
        Assert.Equal("ndk;28.0.13004108", repo.Latest("ndk;", "windows")!.PackagePath);
    }

    [Fact]
    public void Swap_keeps_the_installed_tool_when_the_new_one_cannot_be_moved_in()
    {
        using var tmp = new TempDir();
        var target = tmp.File("Tools/cmake");
        Touch(Path.Combine(target, "bin", "old.txt"), "old");
        var missingStaging = tmp.File("Tools/.staging/does-not-exist");
        Assert.ThrowsAny<IOException>(() => ToolSetup.SwapInto(missingStaging, target));
        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "bin", "old.txt")));

        var staging = tmp.File("Tools/.staging/cmake");
        Touch(Path.Combine(staging, "bin", "new.txt"), "new");
        ToolSetup.SwapInto(staging, target);
        Assert.True(File.Exists(Path.Combine(target, "bin", "new.txt")));
        Assert.False(File.Exists(Path.Combine(target, "bin", "old.txt")));
        Assert.False(Directory.Exists(target + ".old"));
    }

    [Fact]
    public void Tar_gz_extraction_strips_top_folder_and_blocks_traversal()
    {
        using var tmp = new TempDir();
        var good = tmp.File("cmake.tar.gz");
        WriteTarGz(good, ("cmake-3.31.7-linux-x86_64/bin/cmake", "x"), ("cmake-3.31.7-linux-x86_64/share/a.txt", "y"));
        ToolSetup.ExtractArchive(good, tmp.File("out"), stripTopFolder: true);
        Assert.Equal("x", File.ReadAllText(tmp.File("out/bin/cmake")));

        var evil = tmp.File("evil.tar.gz");
        WriteTarGz(evil, ("top/../../escaped.txt", "z"));
        Assert.Throws<InvalidDataException>(() => ToolSetup.ExtractArchive(evil, tmp.File("out2"), stripTopFolder: true));
        Assert.False(File.Exists(tmp.File("escaped.txt")));
    }

    private static void WriteTarGz(string path, params (string name, string content)[] files)
    {
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Fastest);
        using var tar = new TarWriter(gz, TarEntryFormat.Pax);
        foreach (var (name, content) in files)
        {
            var e = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)) };
            tar.WriteEntry(e);
        }
    }
}
