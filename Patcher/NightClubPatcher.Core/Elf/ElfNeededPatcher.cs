using System.Buffers.Binary;
using System.Text;

namespace NightClubPatcher.Core.Elf;

public sealed record ElfPatchResult(bool Changed, string Message, byte[] Data);

/// <summary>
/// Adds a DT_NEEDED entry to a shared library without disturbing any existing code or data.
///
/// Strategy (same idea as patchelf / LIEF "add_library"):
///  1. Copy the existing .dynstr and append the new library name. All existing string offsets
///     (symbol names, version names, other DT_NEEDED) remain valid because the old table is a prefix.
///  2. Copy the existing .dynamic array and insert a DT_NEEDED entry; DT_STRTAB / DT_STRSZ are
///     redirected to the new string table.
///  3. Append both to the end of the file inside a brand new PT_LOAD segment (RW) placed after the
///     highest mapped address. The program header slot comes from PT_NOTE, which the dynamic loader
///     does not need, so the program header table does not have to grow or move.
///  4. Update PT_DYNAMIC and the .dynamic / .dynstr section headers. Android's bionic linker
///     (target SDK >= 26) rejects libraries whose .dynamic section header disagrees with PT_DYNAMIC,
///     and it reads DT_NEEDED through the section header string table before mapping, so both
///     must point at the new copies.
/// </summary>
public static class ElfNeededPatcher
{
    public static ElfPatchResult AddNeeded(byte[] original, string library)
    {
        if (string.IsNullOrWhiteSpace(library) || library.Contains('\0'))
            throw new ArgumentException("Invalid library name.", nameof(library));

        var elf = new ElfFile((byte[])original.Clone());
        var needed = elf.GetNeeded();
        if (needed.Contains(library, StringComparer.Ordinal))
            return new ElfPatchResult(false, $"{library} is already a DT_NEEDED dependency.", original);

        var dynPh = elf.Dynamic ?? throw new InvalidDataException("No PT_DYNAMIC segment.");
        var loads = elf.ProgramHeaders.Where(p => p.Type == ElfConst.PT_LOAD).ToList();
        if (loads.Count == 0) throw new InvalidDataException("No PT_LOAD segments.");

        var slot = elf.ProgramHeaders.FirstOrDefault(p => p.Type == ElfConst.PT_NOTE)
                   ?? elf.ProgramHeaders.FirstOrDefault(p => p.Type == ElfConst.PT_NULL)
                   ?? throw new NotSupportedException(
                       "libmain.so has no PT_NOTE program header that can be reused for the new segment.");

        // --- Build new .dynstr -------------------------------------------------------------
        var (strOff, strSize) = elf.DynStr();
        var newStr = new byte[strSize + Encoding.UTF8.GetByteCount(library) + 1];
        Array.Copy(elf.Data, strOff, newStr, 0, strSize);
        var nameOffset = (ulong)strSize;
        Encoding.UTF8.GetBytes(library, 0, library.Length, newStr, (int)strSize);

        // --- Build new .dynamic ------------------------------------------------------------
        var dyn = elf.ReadDynamic();
        if (dyn.Count == 0 || dyn[^1].Tag != ElfConst.DT_NULL)
            throw new InvalidDataException("Dynamic section is not terminated by DT_NULL.");
        var entries = dyn.Take(dyn.Count - 1).ToList();      // without DT_NULL
        var insertAt = entries.FindLastIndex(d => d.Tag == ElfConst.DT_NEEDED) + 1;
        entries.Insert(insertAt, new ElfDyn(ElfConst.DT_NEEDED, nameOffset));

        // --- Layout of the new segment ---------------------------------------------------
        ulong align = Math.Max(0x1000UL, loads.Max(l => l.Align));
        ulong maxEnd = loads.Max(l => l.VAddr + l.MemSize);
        ulong fileOff = AlignUp((ulong)elf.Data.Length, align);
        ulong vaddr = AlignUp(maxEnd, align);

        ulong dynSize = (ulong)(entries.Count + 1) * 16;
        ulong strRel = AlignUp(dynSize, 8);
        ulong segSize = strRel + (ulong)newStr.Length;
        ulong newStrVaddr = vaddr + strRel;

        for (var i = 0; i < entries.Count; i++)
        {
            entries[i] = entries[i].Tag switch
            {
                ElfConst.DT_STRTAB => entries[i] with { Value = newStrVaddr },
                ElfConst.DT_STRSZ => entries[i] with { Value = (ulong)newStr.Length },
                _ => entries[i],
            };
        }

        var output = new byte[fileOff + segSize];
        Array.Copy(elf.Data, output, elf.Data.Length);
        var p = (int)fileOff;
        foreach (var e in entries)
        {
            BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(p), e.Tag);
            BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(p + 8), e.Value);
            p += 16;
        }
        // trailing DT_NULL is already zero
        Array.Copy(newStr, 0, output, (long)(fileOff + strRel), newStr.Length);

        elf.ReplaceData(output);

        // --- Program headers -----------------------------------------------------------
        slot = elf.ProgramHeaders[slot.Index];
        slot.Type = ElfConst.PT_LOAD;
        slot.Flags = ElfConst.PF_R | ElfConst.PF_W;
        slot.Offset = fileOff;
        slot.VAddr = vaddr;
        slot.PAddr = vaddr;
        slot.FileSize = segSize;
        slot.MemSize = segSize;
        slot.Align = align;
        elf.WriteProgramHeader(slot);

        var dynamic = elf.ProgramHeaders[dynPh.Index];
        dynamic.Offset = fileOff;
        dynamic.VAddr = vaddr;
        dynamic.PAddr = vaddr;
        dynamic.FileSize = dynSize;
        dynamic.MemSize = dynSize;
        elf.WriteProgramHeader(dynamic);

        // The ELF spec requires PT_LOAD entries sorted by p_vaddr: move the new segment right
        // after the last pre-existing PT_LOAD (only non-LOAD entries shift).
        var order = elf.ProgramHeaders.ToList();
        order.Remove(slot);
        var lastLoad = order.FindLastIndex(p => p.Type == ElfConst.PT_LOAD);
        order.Insert(lastLoad + 1, slot);
        for (var i = 0; i < order.Count; i++)
        {
            order[i].Index = i;
            elf.WriteProgramHeader(order[i]);
        }

        // --- Section headers ------------------------------------------------------------
        var dynSec = elf.SectionHeaders.FirstOrDefault(s => s.Type == ElfConst.SHT_DYNAMIC);
        if (dynSec != null)
        {
            var strSec = dynSec.Link < elf.SectionHeaders.Count ? elf.SectionHeaders[(int)dynSec.Link] : null;
            dynSec.Offset = fileOff;
            dynSec.Addr = vaddr;
            dynSec.Size = dynSize;
            elf.WriteSectionHeader(dynSec);
            if (strSec != null && strSec.Type == ElfConst.SHT_STRTAB)
            {
                strSec.Offset = fileOff + strRel;
                strSec.Addr = newStrVaddr;
                strSec.Size = (ulong)newStr.Length;
                elf.WriteSectionHeader(strSec);
            }
        }

        // Final self-check: re-parse from scratch.
        var check = new ElfFile(elf.Data);
        var after = check.GetNeeded();
        if (!after.Contains(library) || !needed.All(after.Contains))
            throw new InvalidOperationException("ELF verification failed after patching.");

        return new ElfPatchResult(true,
            $"Added DT_NEEDED {library} (new segment @0x{vaddr:X}, file offset 0x{fileOff:X}).", elf.Data);
    }

    private static ulong AlignUp(ulong v, ulong a) => (v + a - 1) / a * a;
}
