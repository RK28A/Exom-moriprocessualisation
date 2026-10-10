using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using NightClubPatcher.Core.Infrastructure;

namespace NightClubPatcher.Core.Apk;

public enum EntryCompression
{
    /// <summary>Same method as the entry being replaced (deflate for new entries).</summary>
    Auto,
    Stored,
    Deflated,
}

public sealed record NewEntry(string Name, byte[] Data, EntryCompression Compression = EntryCompression.Auto);

/// <summary>Describes how a source APK must be transformed. The source file is never modified.</summary>
public sealed class ApkRewritePlan
{
    public Dictionary<string, NewEntry> Replace { get; } = new(StringComparer.Ordinal);
    public List<NewEntry> Add { get; } = new();
    public HashSet<string> Remove { get; } = new(StringComparer.Ordinal);
    public List<Func<string, bool>> RemoveWhere { get; } = new();

    /// <summary>Drop v1 (JAR) signature files. Required whenever the APK will be re-signed.</summary>
    public bool StripV1Signature { get; set; } = true;

    public void ReplaceEntry(string name, byte[] data, EntryCompression c = EntryCompression.Auto) =>
        Replace[name] = new NewEntry(name, data, c);

    public void AddEntry(string name, byte[] data, EntryCompression c = EntryCompression.Auto) =>
        Add.Add(new NewEntry(name, data, c));

    public void AddOrReplace(string name, byte[] data, EntryCompression c, ZipArchiveReader source)
    {
        if (source.Contains(name)) ReplaceEntry(name, data, c);
        else AddEntry(name, data, c);
    }

    internal bool ShouldRemove(string name)
    {
        if (Remove.Contains(name)) return true;
        if (StripV1Signature && IsV1SignatureFile(name)) return true;
        return RemoveWhere.Any(f => f(name));
    }

    public static bool IsV1SignatureFile(string name)
    {
        if (!name.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)) return false;
        var file = name["META-INF/".Length..];
        if (file.Contains('/')) return false;
        if (file.Equals("MANIFEST.MF", StringComparison.OrdinalIgnoreCase)) return true;
        var ext = Path.GetExtension(file).ToUpperInvariant();
        return ext is ".SF" or ".RSA" or ".DSA" or ".EC";
    }
}

/// <summary>
/// Rebuilds an APK as a ZIP without decoding resources (FAST PATCH). Untouched entries are copied
/// raw, so their compressed bytes are identical to the original. Stored entries are aligned
/// like zipalign (4 bytes, native libraries on a 16 KiB boundary which also satisfies 4 KiB pages).
/// The APK Signing Block of the source is intentionally dropped; the result must be re-signed.
/// </summary>
public static class ApkRewriter
{
    public const int DefaultAlignment = 4;
    public const int NativeLibAlignment = 16384;

    private static readonly ushort DosTime = 0;              // 00:00:00
    private static readonly ushort DosDate = (1 << 5) | 1;   // 1980-01-01

    private sealed class Pending
    {
        public required string Name;
        public ZipEntryInfo? Source;
        public byte[]? RawData;          // compressed (or stored) payload for new/replaced entries
        public ushort Method;
        public uint Crc;
        public long CompressedSize;
        public long UncompressedSize;
        public ushort Flags;
        public ushort Time;
        public ushort Date;
        public ushort VersionMadeBy;
        public uint ExternalAttr;
        public ushort InternalAttr;
        public byte[] CentralExtra = Array.Empty<byte>();
        public byte[] Comment = Array.Empty<byte>();
        public long LocalOffset;
    }

    public static void Rewrite(string inputApk, string outputApk, ApkRewritePlan plan)
    {
        using var src = new ZipArchiveReader(inputApk);
        Rewrite(src, outputApk, plan);
    }

    public static void Rewrite(ZipArchiveReader src, string outputApk, ApkRewritePlan plan)
    {
        var pending = new List<Pending>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var e in src.Entries)
        {
            if (!seen.Add(e.Name)) continue;   // never write duplicate names
            if (plan.ShouldRemove(e.Name) && !plan.Replace.ContainsKey(e.Name)) continue;

            if (plan.Replace.TryGetValue(e.Name, out var rep))
            {
                var method = rep.Compression switch
                {
                    EntryCompression.Stored => (ushort)0,
                    EntryCompression.Deflated => (ushort)8,
                    _ => e.Method == 0 ? (ushort)0 : (ushort)8,
                };
                var p = Encode(rep.Name, rep.Data, method);
                p.Time = e.ModTime;
                p.Date = e.ModDate;
                p.VersionMadeBy = e.VersionMadeBy;
                p.ExternalAttr = e.ExternalAttr;
                p.InternalAttr = e.InternalAttr;
                pending.Add(p);
            }
            else
            {
                if (e.Method != 0 && e.Method != 8)
                    throw new NotSupportedException($"Entry '{e.Name}' uses unsupported compression method {e.Method}.");
                pending.Add(new Pending
                {
                    Name = e.Name,
                    Source = e,
                    Method = e.Method,
                    Crc = e.Crc32,
                    CompressedSize = e.CompressedSize,
                    UncompressedSize = e.UncompressedSize,
                    Flags = (ushort)(e.Flags & 0x0800),
                    Time = e.ModTime,
                    Date = e.ModDate,
                    VersionMadeBy = e.VersionMadeBy,
                    ExternalAttr = e.ExternalAttr,
                    InternalAttr = e.InternalAttr,
                    CentralExtra = StripAlignmentExtra(e.CentralExtra),
                    Comment = e.Comment,
                });
            }
        }

        foreach (var add in plan.Add)
        {
            if (!seen.Add(add.Name))
                throw new InvalidOperationException($"Entry '{add.Name}' already exists; use Replace instead of Add.");
            var method = add.Compression switch
            {
                EntryCompression.Stored => (ushort)0,
                EntryCompression.Deflated => (ushort)8,
                _ => add.Name.EndsWith(".so", StringComparison.Ordinal) ? (ushort)0 : (ushort)8,
            };
            pending.Add(Encode(add.Name, add.Data, method));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputApk))!);
        var tmp = outputApk + ".partial";
        using (var outFs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            foreach (var p in pending)
            {
                p.LocalOffset = outFs.Position;
                WriteLocalHeader(outFs, p);
                if (p.RawData != null) outFs.Write(p.RawData);
                else src.CopyRaw(p.Source!, outFs);
            }

            var cdStart = outFs.Position;
            foreach (var p in pending) WriteCentralHeader(outFs, p);
            var cdSize = outFs.Position - cdStart;

            if (pending.Count > 0xFFFF || cdStart > uint.MaxValue)
                throw new NotSupportedException("Output would require ZIP64, which is not supported.");

            Span<byte> eocd = stackalloc byte[22];
            BinaryPrimitives.WriteUInt32LittleEndian(eocd, 0x06054b50);
            BinaryPrimitives.WriteUInt16LittleEndian(eocd[4..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(eocd[6..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(eocd[8..], (ushort)pending.Count);
            BinaryPrimitives.WriteUInt16LittleEndian(eocd[10..], (ushort)pending.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(eocd[12..], (uint)cdSize);
            BinaryPrimitives.WriteUInt32LittleEndian(eocd[16..], (uint)cdStart);
            BinaryPrimitives.WriteUInt16LittleEndian(eocd[20..], 0);
            outFs.Write(eocd);
        }
        File.Move(tmp, outputApk, overwrite: true);
    }

    private static Pending Encode(string name, byte[] data, ushort method)
    {
        byte[] raw;
        if (method == 0)
        {
            raw = data;
        }
        else
        {
            using var ms = new MemoryStream();
            using (var d = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                d.Write(data);
            raw = ms.ToArray();
        }
        return new Pending
        {
            Name = name,
            RawData = raw,
            Method = method,
            Crc = Crc32.Compute(data),
            CompressedSize = raw.Length,
            UncompressedSize = data.Length,
            Flags = 0x0800,
            Time = DosTime,
            Date = DosDate,
            VersionMadeBy = 20,
            ExternalAttr = 0,
        };
    }

    public static int AlignmentFor(string name, ushort method)
    {
        if (method != 0) return 1;
        return name.EndsWith(".so", StringComparison.Ordinal) ? NativeLibAlignment : DefaultAlignment;
    }

    private static void WriteLocalHeader(Stream s, Pending p)
    {
        var name = Encoding.UTF8.GetBytes(p.Name);
        var align = AlignmentFor(p.Name, p.Method);
        var headerEnd = p.LocalOffset + 30 + name.Length;
        var pad = align > 1 ? (int)((align - headerEnd % align) % align) : 0;

        Span<byte> h = stackalloc byte[30];
        BinaryPrimitives.WriteUInt32LittleEndian(h, 0x04034b50);
        BinaryPrimitives.WriteUInt16LittleEndian(h[4..], p.Method == 0 ? (ushort)10 : (ushort)20);
        BinaryPrimitives.WriteUInt16LittleEndian(h[6..], p.Flags);
        BinaryPrimitives.WriteUInt16LittleEndian(h[8..], p.Method);
        BinaryPrimitives.WriteUInt16LittleEndian(h[10..], p.Time);
        BinaryPrimitives.WriteUInt16LittleEndian(h[12..], p.Date);
        BinaryPrimitives.WriteUInt32LittleEndian(h[14..], p.Crc);
        BinaryPrimitives.WriteUInt32LittleEndian(h[18..], (uint)p.CompressedSize);
        BinaryPrimitives.WriteUInt32LittleEndian(h[22..], (uint)p.UncompressedSize);
        BinaryPrimitives.WriteUInt16LittleEndian(h[26..], (ushort)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(h[28..], (ushort)pad);
        s.Write(h);
        s.Write(name);
        if (pad > 0) s.Write(new byte[pad]);
    }

    private static void WriteCentralHeader(Stream s, Pending p)
    {
        var name = Encoding.UTF8.GetBytes(p.Name);
        Span<byte> h = stackalloc byte[46];
        BinaryPrimitives.WriteUInt32LittleEndian(h, 0x02014b50);
        BinaryPrimitives.WriteUInt16LittleEndian(h[4..], p.VersionMadeBy);
        BinaryPrimitives.WriteUInt16LittleEndian(h[6..], p.Method == 0 ? (ushort)10 : (ushort)20);
        BinaryPrimitives.WriteUInt16LittleEndian(h[8..], p.Flags);
        BinaryPrimitives.WriteUInt16LittleEndian(h[10..], p.Method);
        BinaryPrimitives.WriteUInt16LittleEndian(h[12..], p.Time);
        BinaryPrimitives.WriteUInt16LittleEndian(h[14..], p.Date);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], p.Crc);
        BinaryPrimitives.WriteUInt32LittleEndian(h[20..], (uint)p.CompressedSize);
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], (uint)p.UncompressedSize);
        BinaryPrimitives.WriteUInt16LittleEndian(h[28..], (ushort)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(h[30..], (ushort)p.CentralExtra.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(h[32..], (ushort)p.Comment.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(h[34..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(h[36..], p.InternalAttr);
        BinaryPrimitives.WriteUInt32LittleEndian(h[38..], p.ExternalAttr);
        BinaryPrimitives.WriteUInt32LittleEndian(h[42..], (uint)p.LocalOffset);
        s.Write(h);
        s.Write(name);
        s.Write(p.CentralExtra);
        s.Write(p.Comment);
    }

    /// <summary>Removes the 0xD935 alignment extra field written by newer zipalign/apksigner.</summary>
    private static byte[] StripAlignmentExtra(byte[] extra)
    {
        if (extra.Length < 4) return extra;
        var result = new List<byte>(extra.Length);
        var i = 0;
        while (i + 4 <= extra.Length)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(i));
            var len = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(i + 2));
            if (i + 4 + len > extra.Length) { result.AddRange(extra.AsSpan(i).ToArray()); break; }
            if (id != 0xD935) result.AddRange(extra.AsSpan(i, 4 + len).ToArray());
            i += 4 + len;
        }
        return result.ToArray();
    }
}
