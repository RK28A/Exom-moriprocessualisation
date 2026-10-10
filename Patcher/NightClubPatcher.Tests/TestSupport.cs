using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using NightClubPatcher.Core;

namespace NightClubPatcher.Tests;

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ncs-tests-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                System.IO.File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Path, true);
        }
        catch
        {
            // ignore
        }
    }
}

internal static class Fixtures
{
    public static string Dir => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures");
    public static byte[] LibMainArm64 => File.ReadAllBytes(System.IO.Path.Combine(Dir, "libmain_arm64.so"));
    public static byte[] LoaderStubArm64 => File.ReadAllBytes(System.IO.Path.Combine(Dir, "libncsloader_stub_arm64.so"));
}

/// <summary>Builds compiled (binary) AndroidManifest.xml documents for tests.</summary>
internal sealed class AxmlWriter
{
    private readonly List<string> _strings = new();
    private readonly List<uint> _resIds = new();
    private readonly MemoryStream _body = new();

    // Attribute names with resource ids must come first in the pool.
    public AxmlWriter(params (string name, uint id)[] attrs)
    {
        foreach (var (n, id) in attrs)
        {
            _strings.Add(n);
            _resIds.Add(id);
        }
    }

    private int S(string s)
    {
        var i = _strings.IndexOf(s);
        if (i >= 0) return i;
        _strings.Add(s);
        return _strings.Count - 1;
    }

    public record Attr(string Name, string? Str, byte Type, uint Data, string? Ns = null);

    public void Start(string element, params Attr[] attrs)
    {
        var size = 36 + attrs.Length * 20;
        var c = new byte[size];
        BinaryPrimitives.WriteUInt16LittleEndian(c, 0x0102);
        BinaryPrimitives.WriteUInt16LittleEndian(c.AsSpan(2), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(c.AsSpan(4), (uint)size);
        BinaryPrimitives.WriteInt32LittleEndian(c.AsSpan(8), 1);
        BinaryPrimitives.WriteInt32LittleEndian(c.AsSpan(12), -1);
        BinaryPrimitives.WriteInt32LittleEndian(c.AsSpan(16), -1);
        BinaryPrimitives.WriteInt32LittleEndian(c.AsSpan(20), S(element));
        BinaryPrimitives.WriteUInt16LittleEndian(c.AsSpan(24), 20);
        BinaryPrimitives.WriteUInt16LittleEndian(c.AsSpan(26), 20);
        BinaryPrimitives.WriteUInt16LittleEndian(c.AsSpan(28), (ushort)attrs.Length);
        for (var i = 0; i < attrs.Length; i++)
        {
            var a = c.AsSpan(36 + i * 20);
            var at = attrs[i];
            BinaryPrimitives.WriteInt32LittleEndian(a, at.Ns == null ? -1 : S(at.Ns));
            BinaryPrimitives.WriteInt32LittleEndian(a[4..], S(at.Name));
            BinaryPrimitives.WriteInt32LittleEndian(a[8..], at.Str == null ? -1 : S(at.Str));
            BinaryPrimitives.WriteUInt16LittleEndian(a[12..], 8);
            a[15] = at.Type;
            BinaryPrimitives.WriteUInt32LittleEndian(a[16..], at.Str != null && at.Type == 0x03 ? (uint)S(at.Str) : at.Data);
        }
        _body.Write(c);
    }

    public void End(string element)
    {
        var c = new byte[24];
        BinaryPrimitives.WriteUInt16LittleEndian(c, 0x0103);
        BinaryPrimitives.WriteUInt16LittleEndian(c.AsSpan(2), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(c.AsSpan(4), 24);
        BinaryPrimitives.WriteInt32LittleEndian(c.AsSpan(12), -1);
        BinaryPrimitives.WriteInt32LittleEndian(c.AsSpan(16), -1);
        BinaryPrimitives.WriteInt32LittleEndian(c.AsSpan(20), S(element));
        _body.Write(c);
    }

    public byte[] Build()
    {
        // String pool (UTF-16)
        var data = new MemoryStream();
        var offsets = new List<int>();
        foreach (var s in _strings)
        {
            offsets.Add((int)data.Length);
            var b = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)s.Length);
            data.Write(b);
            data.Write(Encoding.Unicode.GetBytes(s));
            data.Write(new byte[2]);
        }
        while (data.Length % 4 != 0) data.WriteByte(0);
        var poolHeader = 28;
        var stringsStart = poolHeader + offsets.Count * 4;
        var poolSize = stringsStart + (int)data.Length;
        var pool = new byte[poolSize];
        BinaryPrimitives.WriteUInt16LittleEndian(pool, 0x0001);
        BinaryPrimitives.WriteUInt16LittleEndian(pool.AsSpan(2), (ushort)poolHeader);
        BinaryPrimitives.WriteUInt32LittleEndian(pool.AsSpan(4), (uint)poolSize);
        BinaryPrimitives.WriteUInt32LittleEndian(pool.AsSpan(8), (uint)offsets.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(pool.AsSpan(20), (uint)stringsStart);
        for (var i = 0; i < offsets.Count; i++) BinaryPrimitives.WriteUInt32LittleEndian(pool.AsSpan(poolHeader + i * 4), (uint)offsets[i]);
        data.ToArray().CopyTo(pool, stringsStart);

        var resMap = new byte[8 + _resIds.Count * 4];
        BinaryPrimitives.WriteUInt16LittleEndian(resMap, 0x0180);
        BinaryPrimitives.WriteUInt16LittleEndian(resMap.AsSpan(2), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(resMap.AsSpan(4), (uint)resMap.Length);
        for (var i = 0; i < _resIds.Count; i++) BinaryPrimitives.WriteUInt32LittleEndian(resMap.AsSpan(8 + i * 4), _resIds[i]);

        var body = _body.ToArray();
        var total = 8 + pool.Length + resMap.Length + body.Length;
        var doc = new byte[total];
        BinaryPrimitives.WriteUInt16LittleEndian(doc, 0x0003);
        BinaryPrimitives.WriteUInt16LittleEndian(doc.AsSpan(2), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(doc.AsSpan(4), (uint)total);
        pool.CopyTo(doc, 8);
        resMap.CopyTo(doc, 8 + pool.Length);
        body.CopyTo(doc, 8 + pool.Length + resMap.Length);
        return doc;
    }

    public static byte[] Manifest(string package, string versionName, long versionCode, bool? extractNativeLibs = false, bool debuggable = false)
    {
        var w = new AxmlWriter(("versionCode", 0x0101021b), ("versionName", 0x0101021c), ("extractNativeLibs", 0x010104ea),
            ("name", 0x01010003), ("debuggable", 0x0101000f), ("minSdkVersion", 0x0101020c));
        const string android = "http://schemas.android.com/apk/res/android";
        w.Start("manifest",
            new Attr("versionCode", null, 0x10, (uint)versionCode, android),
            new Attr("versionName", versionName, 0x03, 0, android),
            new Attr("package", package, 0x03, 0));
        w.Start("uses-sdk", new Attr("minSdkVersion", null, 0x10, 29, android));
        w.End("uses-sdk");
        w.Start("uses-feature", new Attr("name", "com.oculus.feature.PASSTHROUGH", 0x03, 0, android));
        w.End("uses-feature");
        var appAttrs = new List<Attr>();
        if (extractNativeLibs != null) appAttrs.Add(new Attr("extractNativeLibs", null, 0x12, extractNativeLibs.Value ? 0xFFFFFFFF : 0, android));
        if (debuggable) appAttrs.Add(new Attr("debuggable", null, 0x12, 0xFFFFFFFF, android));
        w.Start("application", appAttrs.ToArray());
        w.Start("meta-data", new Attr("name", "com.oculus.intent.category.VR", 0x03, 0, android));
        w.End("meta-data");
        w.Start("activity", new Attr("name", "com.unity3d.player.UnityPlayerGameActivity", 0x03, 0, android));
        w.End("activity");
        w.End("application");
        w.End("manifest");
        return w.Build();
    }
}

internal static class FakeApk
{
    private static readonly Lazy<(byte[] pkcs7, string certSha256, byte[] certDer)> Developer = new(() => MakeSignature("CN=MandeGames Test"));

    public static string DeveloperCertSha256 => Developer.Value.certSha256;
    public static byte[] DeveloperCertDer => Developer.Value.certDer;

    /// <summary>A real PKCS#7 signature block (META-INF/CERT.RSA) produced with a throwaway key.</summary>
    public static (byte[] pkcs7, string certSha256, byte[] certDer) MakeSignature(string subject)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        var cms = new SignedCms(new ContentInfo(Encoding.ASCII.GetBytes("Signature-Version: 1.0\r\n")), detached: true);
        cms.ComputeSignature(new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, cert) { IncludeOption = X509IncludeOption.EndCertOnly });
        return (cms.Encode(), Convert.ToHexString(SHA256.HashData(cert.RawData)).ToLowerInvariant(), cert.RawData);
    }

    public static byte[] UnityFsHeader(string unityVersion)
    {
        var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("UnityFS\0"));
        var v = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(v, 8);
        ms.Write(v);
        ms.Write(Encoding.ASCII.GetBytes("5.x.x\0" + unityVersion + "\0"));
        ms.Write(new byte[512]);
        return ms.ToArray();
    }

    public static byte[] Metadata()
    {
        var b = new byte[256];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 0xFAB11BAF);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), 31);
        return b;
    }

    /// <summary>Creates an APK-shaped ZIP that looks like NightClub Simulator 1.1.0 (212).</summary>
    public static string Create(string path, string package = GameConstants.PackageName, string versionName = "1.1.0",
        long versionCode = 212, Action<ZipArchive>? extra = null, bool v1SignatureFiles = true)
    {
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        void Add(string name, byte[] data, CompressionLevel level = CompressionLevel.Optimal)
        {
            var e = zip.CreateEntry(name, level);
            using var s = e.Open();
            s.Write(data);
        }
        Add("AndroidManifest.xml", AxmlWriter.Manifest(package, versionName, versionCode));
        Add("classes.dex", Encoding.ASCII.GetBytes("dex\n035\0fake"));
        Add("resources.arsc", new byte[64], CompressionLevel.NoCompression);
        Add(GameConstants.LibMainEntry, Fixtures.LibMainArm64, CompressionLevel.NoCompression);
        Add(GameConstants.LibUnityEntry, Fixtures.LoaderStubArm64, CompressionLevel.NoCompression);
        Add(GameConstants.LibIl2CppEntry, Fixtures.LoaderStubArm64, CompressionLevel.NoCompression);
        Add(GameConstants.LibDir + "libOVRPlugin.so", Fixtures.LoaderStubArm64, CompressionLevel.NoCompression);
        Add(GameConstants.MetadataEntry, Metadata());
        Add(GameConstants.DataUnity3dEntry, UnityFsHeader("6000.2.2f1"));
        if (v1SignatureFiles)
        {
            Add("META-INF/MANIFEST.MF", Encoding.ASCII.GetBytes("Manifest-Version: 1.0\r\n"));
            Add("META-INF/CERT.SF", Encoding.ASCII.GetBytes("Signature-Version: 1.0\r\n"));
            Add("META-INF/CERT.RSA", Developer.Value.pkcs7);
        }
        extra?.Invoke(zip);
        return path;
    }
}
