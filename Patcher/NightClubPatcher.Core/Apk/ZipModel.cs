using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace NightClubPatcher.Core.Apk;

public sealed class ZipEntryInfo
{
    public required string Name { get; init; }
    public ushort VersionMadeBy { get; init; }
    public ushort VersionNeeded { get; init; }
    public ushort Flags { get; init; }
    public ushort Method { get; init; }
    public ushort ModTime { get; init; }
    public ushort ModDate { get; init; }
    public uint Crc32 { get; init; }
    public long CompressedSize { get; init; }
    public long UncompressedSize { get; init; }
    public ushort InternalAttr { get; init; }
    public uint ExternalAttr { get; init; }
    public long LocalHeaderOffset { get; init; }
    public byte[] CentralExtra { get; init; } = Array.Empty<byte>();
    public byte[] Comment { get; init; } = Array.Empty<byte>();

    /// <summary>Offset of the first byte of (compressed) data in the archive.</summary>
    public long DataOffset { get; set; }

    public bool IsDirectory => Name.EndsWith('/');
    public bool IsStored => Method == 0;
}

/// <summary>
/// Minimal read-only ZIP / APK reader that exposes raw entry data. Raw access is required so
/// unmodified entries can be copied byte-for-byte when an APK is rebuilt, and so the
/// APK Signing Block (between the last entry and the central directory) can be read.
/// </summary>
public sealed class ZipArchiveReader : IDisposable
{
    private readonly FileStream _fs;
    private readonly Dictionary<string, ZipEntryInfo> _byName = new(StringComparer.Ordinal);

    public string Path { get; }
    public IReadOnlyList<ZipEntryInfo> Entries { get; }
    public long CentralDirectoryOffset { get; }
    public long CentralDirectorySize { get; }
    public long EocdOffset { get; }
    public long Length => _fs.Length;

    public ZipArchiveReader(string path)
    {
        Path = path;
        _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            (EocdOffset, CentralDirectoryOffset, CentralDirectorySize, var count) = ReadEocd();
            Entries = ReadCentralDirectory(count);
            foreach (var e in Entries) _byName.TryAdd(e.Name, e);
        }
        catch
        {
            _fs.Dispose();
            throw;
        }
    }

    private (long eocd, long cdOffset, long cdSize, int count) ReadEocd()
    {
        var len = _fs.Length;
        if (len < 22) throw new InvalidDataException("File is too small to be a ZIP/APK.");
        var max = (int)Math.Min(len, 22 + 0xFFFF);
        var buf = new byte[max];
        _fs.Position = len - max;
        _fs.ReadExactly(buf);
        for (var i = max - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i)) != 0x06054b50) continue;
            var commentLen = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(i + 20));
            if (i + 22 + commentLen != max) continue;
            var count = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(i + 10));
            var cdSize = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i + 12));
            var cdOff = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i + 16));
            if (count == 0xFFFF || cdSize == 0xFFFFFFFF || cdOff == 0xFFFFFFFF)
                throw new NotSupportedException("ZIP64 archives are not supported (APK larger than 4 GB?).");
            return (len - max + i, cdOff, cdSize, count);
        }
        throw new InvalidDataException("End of central directory not found: not a valid ZIP/APK.");
    }

    private List<ZipEntryInfo> ReadCentralDirectory(int count)
    {
        var cd = new byte[CentralDirectorySize];
        _fs.Position = CentralDirectoryOffset;
        _fs.ReadExactly(cd);
        var list = new List<ZipEntryInfo>(count);
        var p = 0;
        for (var i = 0; i < count; i++)
        {
            var s = cd.AsSpan(p);
            if (BinaryPrimitives.ReadUInt32LittleEndian(s) != 0x02014b50)
                throw new InvalidDataException($"Bad central directory signature at entry {i}.");
            var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(s[28..]);
            var extraLen = BinaryPrimitives.ReadUInt16LittleEndian(s[30..]);
            var commentLen = BinaryPrimitives.ReadUInt16LittleEndian(s[32..]);
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(s[8..]);
            var nameBytes = s.Slice(46, nameLen);
            var name = (flags & 0x800) != 0 ? Encoding.UTF8.GetString(nameBytes) : Encoding.UTF8.GetString(nameBytes);
            var e = new ZipEntryInfo
            {
                Name = name,
                VersionMadeBy = BinaryPrimitives.ReadUInt16LittleEndian(s[4..]),
                VersionNeeded = BinaryPrimitives.ReadUInt16LittleEndian(s[6..]),
                Flags = flags,
                Method = BinaryPrimitives.ReadUInt16LittleEndian(s[10..]),
                ModTime = BinaryPrimitives.ReadUInt16LittleEndian(s[12..]),
                ModDate = BinaryPrimitives.ReadUInt16LittleEndian(s[14..]),
                Crc32 = BinaryPrimitives.ReadUInt32LittleEndian(s[16..]),
                CompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(s[20..]),
                UncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(s[24..]),
                InternalAttr = BinaryPrimitives.ReadUInt16LittleEndian(s[36..]),
                ExternalAttr = BinaryPrimitives.ReadUInt32LittleEndian(s[38..]),
                LocalHeaderOffset = BinaryPrimitives.ReadUInt32LittleEndian(s[42..]),
                CentralExtra = s.Slice(46 + nameLen, extraLen).ToArray(),
                Comment = s.Slice(46 + nameLen + extraLen, commentLen).ToArray(),
            };
            if (e.CompressedSize == 0xFFFFFFFF || e.UncompressedSize == 0xFFFFFFFF || e.LocalHeaderOffset == 0xFFFFFFFF)
                throw new NotSupportedException($"ZIP64 entry '{name}' is not supported.");
            e.DataOffset = ReadDataOffset(e.LocalHeaderOffset);
            list.Add(e);
            p += 46 + nameLen + extraLen + commentLen;
        }
        return list;
    }

    private long ReadDataOffset(long localHeaderOffset)
    {
        Span<byte> h = stackalloc byte[30];
        _fs.Position = localHeaderOffset;
        _fs.ReadExactly(h);
        if (BinaryPrimitives.ReadUInt32LittleEndian(h) != 0x04034b50)
            throw new InvalidDataException($"Bad local header signature at 0x{localHeaderOffset:X}.");
        var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(h[26..]);
        var extraLen = BinaryPrimitives.ReadUInt16LittleEndian(h[28..]);
        return localHeaderOffset + 30 + nameLen + extraLen;
    }

    public ZipEntryInfo? Find(string name) => _byName.TryGetValue(name, out var e) ? e : null;

    public bool Contains(string name) => _byName.ContainsKey(name);

    public byte[] ReadRaw(ZipEntryInfo e)
    {
        var buf = new byte[e.CompressedSize];
        lock (_fs)
        {
            _fs.Position = e.DataOffset;
            _fs.ReadExactly(buf);
        }
        return buf;
    }

    public void CopyRaw(ZipEntryInfo e, Stream destination)
    {
        lock (_fs)
        {
            _fs.Position = e.DataOffset;
            var remaining = e.CompressedSize;
            var buf = new byte[1 << 20];
            while (remaining > 0)
            {
                var n = _fs.Read(buf, 0, (int)Math.Min(buf.Length, remaining));
                if (n <= 0) throw new EndOfStreamException();
                destination.Write(buf, 0, n);
                remaining -= n;
            }
        }
    }

    /// <summary>Opens a decompressed stream over the entry. The returned stream owns its own file handle.</summary>
    public Stream OpenRead(ZipEntryInfo e)
    {
        var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var sub = new SubStream(fs, e.DataOffset, e.CompressedSize);
        return e.Method switch
        {
            0 => sub,
            8 => new DeflateStream(sub, CompressionMode.Decompress),
            _ => throw new NotSupportedException($"Compression method {e.Method} of '{e.Name}' is not supported."),
        };
    }

    public byte[] ReadAllBytes(ZipEntryInfo e)
    {
        using var s = OpenRead(e);
        using var ms = new MemoryStream((int)Math.Min(e.UncompressedSize, int.MaxValue));
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public byte[] ReadAllBytes(string name) =>
        ReadAllBytes(Find(name) ?? throw new FileNotFoundException($"Entry '{name}' not found in APK."));

    public byte[] ReadPrefix(ZipEntryInfo e, int count)
    {
        using var s = OpenRead(e);
        var buf = new byte[Math.Min(count, e.UncompressedSize)];
        var read = 0;
        while (read < buf.Length)
        {
            var n = s.Read(buf, read, buf.Length - read);
            if (n <= 0) break;
            read += n;
        }
        return buf.AsSpan(0, read).ToArray();
    }

    public void Extract(ZipEntryInfo e, string destination)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(destination))!);
        using var s = OpenRead(e);
        using var o = File.Create(destination);
        s.CopyTo(o);
    }

    /// <summary>Returns the bytes located between the end of the last entry and the central directory.</summary>
    public byte[] ReadPreCentralDirectoryBytes(int maxBytes)
    {
        var start = Math.Max(0, CentralDirectoryOffset - maxBytes);
        var buf = new byte[CentralDirectoryOffset - start];
        lock (_fs)
        {
            _fs.Position = start;
            _fs.ReadExactly(buf);
        }
        return buf;
    }

    public byte[] ReadRange(long offset, int count)
    {
        var buf = new byte[count];
        lock (_fs)
        {
            _fs.Position = offset;
            _fs.ReadExactly(buf);
        }
        return buf;
    }

    public void Dispose() => _fs.Dispose();
}

/// <summary>Read-only window over a parent stream. Disposes the parent.</summary>
internal sealed class SubStream : Stream
{
    private readonly Stream _inner;
    private readonly long _start;
    private readonly long _length;
    private long _pos;

    public SubStream(Stream inner, long start, long length)
    {
        _inner = inner;
        _start = start;
        _length = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _pos; set => _pos = value; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var remaining = _length - _pos;
        if (remaining <= 0) return 0;
        if (count > remaining) count = (int)remaining;
        _inner.Position = _start + _pos;
        var n = _inner.Read(buffer, offset, count);
        _pos += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _pos = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _pos + offset,
            _ => _length + offset,
        };
        return _pos;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
