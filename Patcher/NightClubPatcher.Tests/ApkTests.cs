using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using NightClubPatcher.Core;
using NightClubPatcher.Core.Android;
using NightClubPatcher.Core.Apk;
using Xunit;

namespace NightClubPatcher.Tests;

public class ApkTests
{
    [Fact]
    public void Axml_manifest_is_parsed()
    {
        var m = AxmlManifest.Parse(AxmlWriter.Manifest(GameConstants.PackageName, "1.1.0", 212, extractNativeLibs: false));
        Assert.Equal(GameConstants.PackageName, m.Package);
        Assert.Equal("1.1.0", m.VersionName);
        Assert.Equal(212, m.VersionCode);
        Assert.Equal(29, m.MinSdk);
        Assert.False(m.ExtractNativeLibs);
        Assert.Contains("com.oculus.feature.PASSTHROUGH", m.Features);
        Assert.Contains("com.unity3d.player.UnityPlayerGameActivity", m.Activities);
    }

    [Fact]
    public void Inspector_detects_nightclub_build()
    {
        using var tmp = new TempDir();
        var apk = FakeApk.Create(tmp.File("base.apk"));
        var a = ApkInspector.Analyze(apk);
        Assert.True(a.IsTargetPackage);
        Assert.True(a.LooksComplete);
        Assert.True(a.IsArm64);
        Assert.True(a.Il2Cpp.IsIl2Cpp);
        Assert.Equal(31, a.Il2Cpp.MetadataVersion);
        Assert.Equal("6000.2.2f1", a.UnityVersion);
        Assert.NotNull(a.XrRuntime);
        Assert.Equal("v1", a.Signer?.Scheme);
        Assert.Equal(FakeApk.DeveloperCertSha256, a.Signer?.CertSha256);
        Assert.False(a.IsPatchedByModKit);
        Assert.Empty(a.ModificationIndicators);
        Assert.Equal(4, a.FileHashes.Count);
    }

    [Fact]
    public void Java_hash_matches_Arrays_hashCode()
    {
        // Arrays.hashCode(new byte[]{1,2,3}) == 30817 == 0x7861 ; negative bytes are signed in Java.
        Assert.Equal("7861", ApkSignerInfo.ComputeJavaHash(new byte[] { 1, 2, 3 }));
        Assert.Equal(((uint)(31 * 31 + 31 * -1 + -2)).ToString("x"), ApkSignerInfo.ComputeJavaHash(new byte[] { 0xFF, 0xFE }));
    }

    [Fact]
    public void Rewriter_copies_raw_entries_aligns_libraries_and_strips_v1_signature()
    {
        using var tmp = new TempDir();
        var src = FakeApk.Create(tmp.File("in.apk"));
        var outApk = tmp.File("out.apk");
        var newLib = Fixtures.LoaderStubArm64;

        var plan = new ApkRewritePlan();
        plan.ReplaceEntry("classes.dex", Encoding.ASCII.GetBytes("replaced"));
        plan.AddEntry(GameConstants.LoaderEntry, newLib, EntryCompression.Stored);
        plan.AddEntry("assets/new.txt", Encoding.ASCII.GetBytes("hello"));
        plan.Remove.Add("resources.arsc");
        ApkRewriter.Rewrite(src, outApk, plan);

        using (var check = ZipFile.OpenRead(outApk))
        {
            Assert.Null(check.GetEntry("META-INF/CERT.RSA"));
            Assert.Null(check.GetEntry("META-INF/MANIFEST.MF"));
            Assert.Null(check.GetEntry("resources.arsc"));
            Assert.Equal("replaced", new StreamReader(check.GetEntry("classes.dex")!.Open()).ReadToEnd());
            Assert.Equal("hello", new StreamReader(check.GetEntry("assets/new.txt")!.Open()).ReadToEnd());
            using var ms = new MemoryStream();
            check.GetEntry(GameConstants.LoaderEntry)!.Open().CopyTo(ms);
            Assert.Equal(newLib, ms.ToArray());
        }

        using var srcZip = new ZipArchiveReader(src);
        using var outZip = new ZipArchiveReader(outApk);
        foreach (var e in outZip.Entries)
        {
            if (e.IsStored)
            {
                var align = e.Name.EndsWith(".so") ? ApkRewriter.NativeLibAlignment : ApkRewriter.DefaultAlignment;
                Assert.True(e.DataOffset % align == 0, $"{e.Name} data at {e.DataOffset} not aligned to {align}");
            }
            var orig = srcZip.Find(e.Name);
            if (orig != null && e.Name != "classes.dex")
            {
                Assert.Equal(srcZip.ReadRaw(orig), outZip.ReadRaw(e)); // byte-for-byte raw copy
                Assert.Equal(orig.Crc32, e.Crc32);
            }
        }
        Assert.True(outZip.Find(GameConstants.LoaderEntry)!.IsStored);
    }

    [Fact]
    public void Rewriter_never_modifies_its_input()
    {
        using var tmp = new TempDir();
        var src = FakeApk.Create(tmp.File("in.apk"));
        var before = File.ReadAllBytes(src);
        var plan = new ApkRewritePlan();
        plan.ReplaceEntry(GameConstants.LibMainEntry, new byte[] { 1, 2, 3 });
        ApkRewriter.Rewrite(src, tmp.File("out.apk"), plan);
        Assert.Equal(before, File.ReadAllBytes(src));
    }

    [Fact]
    public void Unity_version_from_unityfs_header()
    {
        Assert.Equal("6000.2.2f1", Core.Unity.UnityDetector.FromUnityFsHeader(FakeApk.UnityFsHeader("6000.2.2f1")));
        Assert.Null(Core.Unity.UnityDetector.FromUnityFsHeader(new byte[64]));
    }

    [Fact]
    public void Zip_reader_rejects_garbage()
    {
        using var tmp = new TempDir();
        var f = tmp.File("x.apk");
        File.WriteAllBytes(f, new byte[100]);
        Assert.Throws<InvalidDataException>(() => new ZipArchiveReader(f));
    }

    [Fact]
    public void Reads_signing_block_certificate_v2()
    {
        // Build a minimal APK Signing Block holding a v2 signer with our developer certificate.
        using var tmp = new TempDir();
        var plain = FakeApk.Create(tmp.File("plain.apk"), v1SignatureFiles: false);
        var cert = FakeApk.DeveloperCertDer;

        static byte[] Lp(params byte[][] parts)
        {
            var body = parts.SelectMany(p => p).ToArray();
            var r = new byte[4 + body.Length];
            BinaryPrimitives.WriteInt32LittleEndian(r, body.Length);
            body.CopyTo(r, 4);
            return r;
        }
        var signedData = Lp(Lp(), Lp(Lp(cert)), Lp());            // digests, certificates, attrs
        var signer = Lp(signedData, Lp(), Lp());                    // signed data, signatures, public key
        var v2Value = Lp(signer);                                   // signers
        var pairLen = 4 + v2Value.Length;
        var pairs = new byte[8 + pairLen];
        BinaryPrimitives.WriteInt64LittleEndian(pairs, pairLen);
        BinaryPrimitives.WriteUInt32LittleEndian(pairs.AsSpan(8), 0x7109871a);
        v2Value.CopyTo(pairs, 12);
        var size = (ulong)(pairs.Length + 8 + 16);
        var block = new MemoryStream();
        var u64 = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(u64, size);
        block.Write(u64);
        block.Write(pairs);
        block.Write(u64);
        block.Write(Encoding.ASCII.GetBytes("APK Sig Block 42"));

        var bytes = File.ReadAllBytes(plain);
        using var zr = new ZipArchiveReader(plain);
        var cdOff = (int)zr.CentralDirectoryOffset;
        var eocdOff = (int)zr.EocdOffset;
        zr.Dispose();
        var blockBytes = block.ToArray();
        var outBytes = bytes[..cdOff].Concat(blockBytes).Concat(bytes[cdOff..]).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(eocdOff + blockBytes.Length + 16), (uint)(cdOff + blockBytes.Length));
        var signedPath = tmp.File("v2.apk");
        File.WriteAllBytes(signedPath, outBytes);

        var info = ApkSignatureReader.Read(signedPath);
        Assert.NotNull(info);
        Assert.Equal("v2", info!.Scheme);
        Assert.Equal(FakeApk.DeveloperCertSha256, info.CertSha256);
    }
}
