using System.IO.Compression;
using System.Text;
using NightClubPatcher.Core;
using NightClubPatcher.Core.Apk;
using NightClubPatcher.Core.Backup;
using NightClubPatcher.Core.Infrastructure;
using NightClubPatcher.Core.Patching;
using NightClubPatcher.Core.Versions;
using Xunit;

namespace NightClubPatcher.Tests;

public class WorkflowTests
{
    private static (KitPaths paths, KitLog log) Kit(TempDir tmp)
    {
        var paths = new KitPaths(tmp.File("kit"));
        paths.EnsureCreated();
        return (paths, new KitLog(null));
    }

    [Fact]
    public void Backup_is_created_read_only_and_never_overwritten()
    {
        using var tmp = new TempDir();
        var (paths, log) = Kit(tmp);
        var mgr = new BackupManager(paths, log);
        var apk = FakeApk.Create(tmp.File("game.apk"));

        var first = mgr.ImportFromFile(apk, new ApkInspectorOptions());
        Assert.Equal(BackupOutcome.Created, first.Outcome);
        var original = first.Backup!.ApkPath;
        Assert.True(File.GetAttributes(original).HasFlag(FileAttributes.ReadOnly));
        Assert.True(File.Exists(Path.Combine(first.Backup.Directory, BackupManager.ShaFile)));
        Assert.True(File.Exists(Path.Combine(first.Backup.Directory, BackupManager.MetaFile)));
        Assert.True(first.Backup.Metadata!.Immutable);
        var originalBytes = File.ReadAllBytes(original);

        // Same version again (even a different file) must not replace the backup.
        var other = FakeApk.Create(tmp.File("other.apk"), extra: z => z.CreateEntry("assets/extra.txt"));
        var second = mgr.ImportFromFile(other, new ApkInspectorOptions());
        Assert.Equal(BackupOutcome.AlreadyExists, second.Outcome);
        Assert.Equal(originalBytes, File.ReadAllBytes(original));

        Assert.True(mgr.Verify(first.Backup).Ok);
    }

    [Fact]
    public void Modified_or_foreign_apk_is_never_backed_up_as_original()
    {
        using var tmp = new TempDir();
        var (paths, log) = Kit(tmp);
        var mgr = new BackupManager(paths, log);

        var modded = FakeApk.Create(tmp.File("modded.apk"), extra: z =>
        {
            using var s = z.CreateEntry(GameConstants.LoaderEntry).Open();
            s.Write(Fixtures.LoaderStubArm64);
        });
        Assert.Equal(BackupOutcome.RefusedModified, mgr.ImportFromFile(modded, new ApkInspectorOptions()).Outcome);

        var foreign = FakeApk.Create(tmp.File("foreign.apk"), package: "com.example.other");
        Assert.Equal(BackupOutcome.RefusedWrongPackage, mgr.ImportFromFile(foreign, new ApkInspectorOptions()).Outcome);

        Assert.Empty(mgr.All());
    }

    [Fact]
    public void Tampered_backup_fails_verification()
    {
        using var tmp = new TempDir();
        var (paths, log) = Kit(tmp);
        var mgr = new BackupManager(paths, log);
        var b = mgr.ImportFromFile(FakeApk.Create(tmp.File("game.apk")), new ApkInspectorOptions()).Backup!;
        File.SetAttributes(b.ApkPath, FileAttributes.Normal);
        File.AppendAllText(b.ApkPath, "x");
        var v = mgr.Verify(b);
        Assert.False(v.Ok);
        Assert.Contains(v.Problems, p => p.Contains("SHA-256 mismatch") || p.Contains("unreadable"));
    }

    [Fact]
    public void Emergency_lookup_works_without_backup_json()
    {
        using var tmp = new TempDir();
        var (paths, log) = Kit(tmp);
        var mgr = new BackupManager(paths, log);
        var b = mgr.ImportFromFile(FakeApk.Create(tmp.File("game.apk")), new ApkInspectorOptions()).Backup!;
        var meta = Path.Combine(b.Directory, BackupManager.MetaFile);
        File.SetAttributes(meta, FileAttributes.Normal);
        File.Delete(meta);

        var found = mgr.All().Single();
        Assert.Null(found.Metadata);
        Assert.True(mgr.Verify(found).Ok); // original.sha256 still proves integrity
    }

    [Fact]
    public void Compatibility_gate()
    {
        using var tmp = new TempDir();
        var (paths, _) = Kit(tmp);
        var store = new ProfileStore(paths);
        var apk = ApkInspector.Analyze(FakeApk.Create(tmp.File("game.apk")));

        Assert.Equal(Compatibility.UnknownVersion, store.Evaluate(apk).Status);

        store.Save(new VersionProfile { VersionName = "1.1.0", VersionCode = 212, UnityVersion = "6000.2.2f1" });
        Assert.Equal(Compatibility.ProfileNotPinned, store.Evaluate(apk).Status);

        store.Pin(apk);
        var known = store.Evaluate(apk);
        Assert.Equal(Compatibility.KnownBuild, known.Status);
        Assert.True(known.AllowsAutomaticPatch);

        var update = ApkInspector.Analyze(FakeApk.Create(tmp.File("update.apk"), versionName: "1.1.1", versionCode: 213));
        var r = store.Evaluate(update);
        Assert.Equal(Compatibility.UnknownVersion, r.Status);
        Assert.False(r.AllowsAutomaticPatch);
        Assert.Contains(r.Details, d => d.Contains("1.1.1"));

        var foreign = ApkInspector.Analyze(FakeApk.Create(tmp.File("x.apk"), package: "com.example.other"));
        Assert.Equal(Compatibility.WrongPackage, store.Evaluate(foreign).Status);
    }

    [Fact]
    public void Repository_version_profile_matches_target_game()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "NightClubModKit.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var p = Json.Read<VersionProfile>(Path.Combine(dir!, "Versions", "1.1.0-212.json"))!;
        Assert.Equal(GameConstants.PackageName, p.Package);
        Assert.Equal("1.1.0", p.VersionName);
        Assert.Equal(212, p.VersionCode);
        Assert.Equal("6000.2.2f1", p.UnityVersion);
        Assert.Equal("arm64-v8a", p.Architecture);
        Assert.Equal("IL2CPP", p.Backend);
    }

    [Fact]
    public void Fast_patch_produces_loader_injected_apk_and_unpatch_inputs_are_recoverable()
    {
        using var tmp = new TempDir();
        var src = FakeApk.Create(tmp.File("original.apk"));
        var before = File.ReadAllBytes(src);
        var original = ApkInspector.Analyze(src);
        var ws = PatchWorkspace.Create("test");
        var modPath = tmp.File("libncsmod_helloworld.so");
        File.WriteAllBytes(modPath, Fixtures.LoaderStubArm64);

        List<string> injected;
        using (var zip = new ZipArchiveReader(src))
            (injected, _) = PatchEngine.BuildUnsignedPatchedApk(zip, original, Fixtures.LoaderStubArm64, new[] { modPath }, ws, new KitLog(null));

        Assert.Equal(before, File.ReadAllBytes(src)); // original never edited
        var patched = ApkInspector.Analyze(Path.Combine(ws.Patched, "unsigned.apk"));
        Assert.True(patched.IsTargetPackage);
        Assert.True(patched.HasLoader);
        Assert.True(patched.LibMainNeedsLoader);
        Assert.NotNull(patched.Marker);
        Assert.Equal(original.ApkSha256, patched.Marker!.OriginalApkSha256);
        Assert.Contains("libncsmod_helloworld.so", patched.EmbeddedMods);
        Assert.Equal(new[] { "libncsloader.so", "libncsmod_helloworld.so" }, injected);
        Assert.Null(patched.Signer);           // old signature stripped, must be re-signed
        Assert.True(patched.IsPatchedByModKit);

        // Untouched game files are identical.
        Assert.Equal(original.Hash(GameConstants.LibIl2CppEntry), patched.Hash(GameConstants.LibIl2CppEntry));
        Assert.Equal(original.Hash(GameConstants.MetadataEntry), patched.Hash(GameConstants.MetadataEntry));
        Assert.NotEqual(original.Hash(GameConstants.LibMainEntry), patched.Hash(GameConstants.LibMainEntry));

        // Native libraries are stored and page aligned (required with extractNativeLibs=false).
        using (var outZip = new ZipArchiveReader(Path.Combine(ws.Patched, "unsigned.apk")))
        {
            foreach (var e in outZip.Entries.Where(e => e.Name.EndsWith(".so")))
            {
                Assert.True(e.IsStored, e.Name);
                Assert.Equal(0, e.DataOffset % 4096);
            }
        }
        Directory.Delete(ws.Root, true); // the reader is closed first: Windows refuses to delete open files
    }

    [Fact]
    public void Patch_marker_round_trips()
    {
        var m = new PatchMarker { ToolVersion = "0.1.0", InjectedLibraries = { "libncsloader.so" }, PatchedAt = DateTimeOffset.UnixEpoch };
        var back = System.Text.Json.JsonSerializer.Deserialize<PatchMarker>(Encoding.UTF8.GetBytes(Json.Serialize(m)), Json.Options)!;
        Assert.Equal("libncsloader.so", back.InjectedLibraries.Single());
    }
}
