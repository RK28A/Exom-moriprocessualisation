using System.Buffers.Binary;
using System.Text;

namespace NightClubPatcher.Core.Elf;

public static class ElfConst
{
    public const uint PT_NULL = 0, PT_LOAD = 1, PT_DYNAMIC = 2, PT_INTERP = 3, PT_NOTE = 4, PT_PHDR = 6;
    public const uint PF_X = 1, PF_W = 2, PF_R = 4;
    public const uint SHT_STRTAB = 3, SHT_DYNAMIC = 6;
    public const long DT_NULL = 0, DT_NEEDED = 1, DT_STRTAB = 5, DT_STRSZ = 10, DT_SONAME = 14;
    public const ushort EM_X86_64 = 62, EM_AARCH64 = 183;
}

public sealed class ElfProgramHeader
{
    public int Index;
    public uint Type;
    public uint Flags;
    public ulong Offset, VAddr, PAddr, FileSize, MemSize, Align;
}

public sealed class ElfSectionHeader
{
    public int Index;
    public uint NameOffset;
    public string Name = "";
    public uint Type;
    public ulong Flags, Addr, Offset, Size;
    public uint Link, Info;
    public ulong AddrAlign, EntSize;
}

public readonly record struct ElfDyn(long Tag, ulong Value);

/// <summary>Minimal ELF64 little-endian reader/writer sufficient for dependency-table patching.</summary>
public sealed class ElfFile
{
    public byte[] Data { get; private set; }
    public ushort Machine { get; private set; }
    public ushort Type { get; private set; }
    public List<ElfProgramHeader> ProgramHeaders { get; } = new();
    public List<ElfSectionHeader> SectionHeaders { get; } = new();

    private ulong _phoff, _shoff;
    private ushort _phentsize, _shentsize, _shstrndx;

    public ElfFile(byte[] data)
    {
        Data = data;
        Parse();
    }

    public static ElfFile Load(string path) => new(File.ReadAllBytes(path));

    public static bool LooksLikeElf(ReadOnlySpan<byte> d) =>
        d.Length >= 4 && d[0] == 0x7F && d[1] == (byte)'E' && d[2] == (byte)'L' && d[3] == (byte)'F';

    public bool IsAarch64 => Machine == ElfConst.EM_AARCH64;

    public string MachineName => Machine switch
    {
        ElfConst.EM_AARCH64 => "AArch64 (arm64-v8a)",
        ElfConst.EM_X86_64 => "x86-64",
        40 => "ARM (armeabi-v7a)",
        3 => "x86",
        _ => $"machine {Machine}",
    };

    private void Parse()
    {
        var d = Data.AsSpan();
        if (!LooksLikeElf(d)) throw new InvalidDataException("Not an ELF file.");
        if (d[4] != 2) throw new NotSupportedException("Only 64-bit ELF files are supported.");
        if (d[5] != 1) throw new NotSupportedException("Only little-endian ELF files are supported.");
        Type = U16(16);
        Machine = U16(18);
        _phoff = U64(32);
        _shoff = U64(40);
        _phentsize = U16(54);
        var phnum = U16(56);
        _shentsize = U16(58);
        var shnum = U16(60);
        _shstrndx = U16(62);

        ProgramHeaders.Clear();
        for (var i = 0; i < phnum; i++)
        {
            var o = (int)_phoff + i * _phentsize;
            ProgramHeaders.Add(new ElfProgramHeader
            {
                Index = i,
                Type = U32(o),
                Flags = U32(o + 4),
                Offset = U64(o + 8),
                VAddr = U64(o + 16),
                PAddr = U64(o + 24),
                FileSize = U64(o + 32),
                MemSize = U64(o + 40),
                Align = U64(o + 48),
            });
        }

        SectionHeaders.Clear();
        if (_shoff != 0 && shnum > 0 && (long)_shoff + (long)shnum * _shentsize <= Data.Length)
        {
            for (var i = 0; i < shnum; i++)
            {
                var o = (int)_shoff + i * _shentsize;
                SectionHeaders.Add(new ElfSectionHeader
                {
                    Index = i,
                    NameOffset = U32(o),
                    Type = U32(o + 4),
                    Flags = U64(o + 8),
                    Addr = U64(o + 16),
                    Offset = U64(o + 24),
                    Size = U64(o + 32),
                    Link = U32(o + 40),
                    Info = U32(o + 44),
                    AddrAlign = U64(o + 48),
                    EntSize = U64(o + 56),
                });
            }
            if (_shstrndx < SectionHeaders.Count)
            {
                var strtab = SectionHeaders[_shstrndx];
                foreach (var s in SectionHeaders)
                    s.Name = ReadCString((long)strtab.Offset + s.NameOffset);
            }
        }
    }

    public ElfProgramHeader? Dynamic => ProgramHeaders.FirstOrDefault(p => p.Type == ElfConst.PT_DYNAMIC);

    public List<ElfDyn> ReadDynamic()
    {
        var dyn = Dynamic ?? throw new InvalidDataException("ELF has no PT_DYNAMIC segment.");
        var list = new List<ElfDyn>();
        for (var o = (long)dyn.Offset; o + 16 <= (long)(dyn.Offset + dyn.FileSize); o += 16)
        {
            var tag = (long)U64((int)o);
            var val = U64((int)o + 8);
            list.Add(new ElfDyn(tag, val));
            if (tag == ElfConst.DT_NULL) break;
        }
        return list;
    }

    /// <summary>Translates a virtual address into a file offset using the PT_LOAD segments.</summary>
    public long VAddrToOffset(ulong vaddr)
    {
        foreach (var p in ProgramHeaders.Where(p => p.Type == ElfConst.PT_LOAD))
            if (vaddr >= p.VAddr && vaddr < p.VAddr + p.FileSize)
                return (long)(vaddr - p.VAddr + p.Offset);
        throw new InvalidDataException($"Virtual address 0x{vaddr:X} is not backed by a PT_LOAD segment.");
    }

    public (long offset, long size) DynStr()
    {
        var dyn = ReadDynamic();
        var strtab = dyn.FirstOrDefault(d => d.Tag == ElfConst.DT_STRTAB);
        var strsz = dyn.FirstOrDefault(d => d.Tag == ElfConst.DT_STRSZ);
        if (strtab.Tag != ElfConst.DT_STRTAB || strsz.Tag != ElfConst.DT_STRSZ)
            throw new InvalidDataException("Dynamic section has no DT_STRTAB/DT_STRSZ.");
        return (VAddrToOffset(strtab.Value), (long)strsz.Value);
    }

    public List<string> GetNeeded()
    {
        var (strOff, _) = DynStr();
        return ReadDynamic().Where(d => d.Tag == ElfConst.DT_NEEDED)
            .Select(d => ReadCString(strOff + (long)d.Value)).ToList();
    }

    public string? GetSoName()
    {
        var (strOff, _) = DynStr();
        var so = ReadDynamic().FirstOrDefault(d => d.Tag == ElfConst.DT_SONAME);
        return so.Tag == ElfConst.DT_SONAME ? ReadCString(strOff + (long)so.Value) : null;
    }

    public string ReadCString(long offset)
    {
        var end = offset;
        while (end < Data.Length && Data[end] != 0) end++;
        return Encoding.UTF8.GetString(Data, (int)offset, (int)(end - offset));
    }

    internal void ReplaceData(byte[] data)
    {
        Data = data;
        Parse();
    }

    internal void WriteProgramHeader(ElfProgramHeader p)
    {
        var o = (int)_phoff + p.Index * _phentsize;
        W32(o, p.Type);
        W32(o + 4, p.Flags);
        W64(o + 8, p.Offset);
        W64(o + 16, p.VAddr);
        W64(o + 24, p.PAddr);
        W64(o + 32, p.FileSize);
        W64(o + 40, p.MemSize);
        W64(o + 48, p.Align);
    }

    internal void WriteSectionHeader(ElfSectionHeader s)
    {
        var o = (int)_shoff + s.Index * _shentsize;
        W64(o + 16, s.Addr);
        W64(o + 24, s.Offset);
        W64(o + 32, s.Size);
    }

    private ushort U16(int o) => BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(o));
    private uint U32(int o) => BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(o));
    private ulong U64(int o) => BinaryPrimitives.ReadUInt64LittleEndian(Data.AsSpan(o));
    private void W32(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Data.AsSpan(o), v);
    private void W64(int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(Data.AsSpan(o), v);
}
