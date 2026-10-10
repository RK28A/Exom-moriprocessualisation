using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using NightClubPatcher.Core.Apk;

namespace NightClubPatcher.Core.Android;

public sealed record ApkSignerInfo(string Scheme, string CertSha256, string Subject, string JavaHash)
{
    /// <summary>
    /// android.content.pm.Signature.hashCode() == java.util.Arrays.hashCode(certDer), printed in hex by
    /// "dumpsys package". Lets us identify the installed signer without pulling the APK.
    /// </summary>
    public static string ComputeJavaHash(ReadOnlySpan<byte> certDer)
    {
        var h = 1;
        unchecked
        {
            foreach (var b in certDer) h = 31 * h + (sbyte)b;
        }
        return ((uint)h).ToString("x");
    }

    public override string ToString() => $"{Scheme} {Subject} sha256={CertSha256}";
}

/// <summary>
/// Extracts the signing certificate of an APK without Java: APK Signature Scheme v3 / v2
/// (APK Signing Block) and falls back to the v1 JAR signature (META-INF/*.RSA|DSA|EC).
/// </summary>
public static class ApkSignatureReader
{
    private const uint V2BlockId = 0x7109871a;
    private const uint V3BlockId = 0xf05368c0;
    private const uint V31BlockId = 0x1b93ad61;

    public static ApkSignerInfo? Read(ZipArchiveReader zip)
    {
        try
        {
            var block = ReadSigningBlock(zip);
            if (block != null)
            {
                foreach (var id in new[] { V31BlockId, V3BlockId, V2BlockId })
                {
                    if (!block.TryGetValue(id, out var value)) continue;
                    var cert = FirstCertificate(value);
                    if (cert != null)
                    {
                        var scheme = id == V2BlockId ? "v2" : id == V3BlockId ? "v3" : "v3.1";
                        return Describe(scheme, cert);
                    }
                }
            }
        }
        catch
        {
            // fall through to v1
        }

        var sigFile = zip.Entries.FirstOrDefault(e =>
            e.Name.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) &&
            !e.Name["META-INF/".Length..].Contains('/') &&
            (e.Name.EndsWith(".RSA", StringComparison.OrdinalIgnoreCase) ||
             e.Name.EndsWith(".DSA", StringComparison.OrdinalIgnoreCase) ||
             e.Name.EndsWith(".EC", StringComparison.OrdinalIgnoreCase)));
        if (sigFile == null) return null;
        try
        {
            var cms = new SignedCms();
            cms.Decode(zip.ReadAllBytes(sigFile));
            var cert = cms.SignerInfos.Count > 0 && cms.SignerInfos[0].Certificate != null
                ? cms.SignerInfos[0].Certificate!.RawData
                : cms.Certificates.Count > 0 ? cms.Certificates[0].RawData : null;
            return cert == null ? null : Describe("v1", cert);
        }
        catch
        {
            return null;
        }
    }

    public static ApkSignerInfo? Read(string apkPath)
    {
        using var zip = new ZipArchiveReader(apkPath);
        return Read(zip);
    }

    public static bool HasSigningBlock(ZipArchiveReader zip) => ReadSigningBlock(zip) != null;

    private static ApkSignerInfo Describe(string scheme, byte[] certDer)
    {
        var sha = Convert.ToHexString(SHA256.HashData(certDer)).ToLowerInvariant();
        string subject;
        try
        {
            using var x = new X509Certificate2(certDer);
            subject = x.Subject;
        }
        catch
        {
            subject = "(unparsable certificate)";
        }
        return new ApkSignerInfo(scheme, sha, subject, ApkSignerInfo.ComputeJavaHash(certDer));
    }

    private static Dictionary<uint, byte[]>? ReadSigningBlock(ZipArchiveReader zip)
    {
        var cdOff = zip.CentralDirectoryOffset;
        if (cdOff < 32) return null;
        var footer = zip.ReadRange(cdOff - 24, 24);
        if (System.Text.Encoding.ASCII.GetString(footer, 8, 16) != "APK Sig Block 42") return null;
        var sizeInFooter = BinaryPrimitives.ReadUInt64LittleEndian(footer);
        if (sizeInFooter < 24 || sizeInFooter > (ulong)(cdOff - 8) || sizeInFooter > int.MaxValue) return null;
        var blockStart = cdOff - (long)sizeInFooter - 8;
        var block = zip.ReadRange(blockStart, (int)(sizeInFooter + 8));
        if (BinaryPrimitives.ReadUInt64LittleEndian(block) != sizeInFooter) return null;

        var result = new Dictionary<uint, byte[]>();
        var p = 8;
        var end = block.Length - 24;
        while (p + 12 <= end)
        {
            var len = (long)BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(p));
            if (len < 4 || p + 8 + len > end) break;
            var id = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(p + 8));
            result[id] = block.AsSpan(p + 12, (int)len - 4).ToArray();
            p += 8 + (int)len;
        }
        return result;
    }

    /// <summary>signers → signer → signed data → (digests, certificates) → first certificate.</summary>
    private static byte[]? FirstCertificate(byte[] schemeBlock)
    {
        var signers = LengthPrefixed(schemeBlock, 0, out _);
        var signer = LengthPrefixed(signers, 0, out _);
        var signedData = LengthPrefixed(signer, 0, out _);
        LengthPrefixed(signedData, 0, out var afterDigests);
        var certs = LengthPrefixed(signedData, afterDigests, out _);
        return certs.Length == 0 ? null : LengthPrefixed(certs, 0, out _);
    }

    private static byte[] LengthPrefixed(byte[] buf, int offset, out int next)
    {
        var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset));
        if (len < 0 || offset + 4 + len > buf.Length) throw new InvalidDataException("Bad length prefix in signing block.");
        next = offset + 4 + len;
        return buf.AsSpan(offset + 4, len).ToArray();
    }
}
