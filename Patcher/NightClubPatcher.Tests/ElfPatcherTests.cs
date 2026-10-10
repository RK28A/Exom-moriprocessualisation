using System.Diagnostics;
using NightClubPatcher.Core.Elf;
using Xunit;

namespace NightClubPatcher.Tests;

public class ElfPatcherTests
{
    [Fact]
    public void Fixture_is_an_lld_style_arm64_library()
    {
        var elf = new ElfFile(Fixtures.LibMainArm64);
        Assert.True(elf.IsAarch64);
        Assert.Equal(new[] { "libc.so", "libm.so", "libdl.so", "liblog.so", "libandroid.so" }, elf.GetNeeded());
        Assert.Contains(elf.ProgramHeaders, p => p.Type == ElfConst.PT_NOTE);
    }

    [Fact]
    public void AddNeeded_appends_dependency_and_keeps_existing_ones()
    {
        var r = ElfNeededPatcher.AddNeeded(Fixtures.LibMainArm64, "libncsloader.so");
        Assert.True(r.Changed);
        var elf = new ElfFile(r.Data);
        Assert.Equal(new[] { "libc.so", "libm.so", "libdl.so", "liblog.so", "libandroid.so", "libncsloader.so" }, elf.GetNeeded());
        Assert.Equal("libmain.so", elf.GetSoName());
        Assert.True(elf.IsAarch64);
    }

    [Fact]
    public void AddNeeded_is_idempotent()
    {
        var once = ElfNeededPatcher.AddNeeded(Fixtures.LibMainArm64, "libncsloader.so").Data;
        var twice = ElfNeededPatcher.AddNeeded(once, "libncsloader.so");
        Assert.False(twice.Changed);
        Assert.Equal(once, twice.Data);
    }

    [Fact]
    public void Original_bytes_are_untouched_except_headers()
    {
        var original = Fixtures.LibMainArm64;
        var patched = ElfNeededPatcher.AddNeeded(original, "libncsloader.so").Data;
        var o = new ElfFile(original);
        // Every PT_LOAD of the original keeps its exact file content.
        foreach (var load in o.ProgramHeaders.Where(p => p.Type == ElfConst.PT_LOAD && p.Offset != 0))
        {
            // The first LOAD contains the program header table, which is allowed to change.
            Assert.Equal(original.AsSpan((int)load.Offset, (int)load.FileSize).ToArray(),
                patched.AsSpan((int)load.Offset, (int)load.FileSize).ToArray());
        }
    }

    [Fact]
    public void Patched_layout_satisfies_bionic_loader_rules()
    {
        var elf = new ElfFile(ElfNeededPatcher.AddNeeded(Fixtures.LibMainArm64, "libncsloader.so").Data);
        var loads = elf.ProgramHeaders.Where(p => p.Type == ElfConst.PT_LOAD).ToList();

        // PT_LOAD sorted by vaddr, non-overlapping, offset/vaddr congruent modulo alignment.
        for (var i = 1; i < loads.Count; i++)
        {
            Assert.True(loads[i].VAddr >= loads[i - 1].VAddr + loads[i - 1].MemSize);
            Assert.True(elf.ProgramHeaders.IndexOf(loads[i]) > elf.ProgramHeaders.IndexOf(loads[i - 1]));
        }
        foreach (var l in loads)
        {
            Assert.Equal(l.Offset % l.Align, l.VAddr % l.Align);
            Assert.True(l.Offset + l.FileSize <= (ulong)elf.Data.Length);
        }

        // .dynamic section header must match PT_DYNAMIC (bionic rejects mismatches for targetSdk >= 26).
        var dyn = elf.Dynamic!;
        var dynSec = elf.SectionHeaders.Single(s => s.Type == ElfConst.SHT_DYNAMIC);
        Assert.Equal(dyn.Offset, dynSec.Offset);
        Assert.Equal(dyn.FileSize, dynSec.Size);
        Assert.Contains(loads, l => dyn.VAddr >= l.VAddr && dyn.VAddr + dyn.MemSize <= l.VAddr + l.FileSize && (l.Flags & ElfConst.PF_W) != 0);

        // .dynstr (sh_link of .dynamic) must match DT_STRTAB/DT_STRSZ.
        var strSec = elf.SectionHeaders[(int)dynSec.Link];
        var (strOff, strSize) = elf.DynStr();
        Assert.Equal((ulong)strOff, strSec.Offset);
        Assert.Equal((ulong)strSize, strSec.Size);
    }

    [Fact]
    public void Readelf_accepts_patched_library_when_available()
    {
        var readelf = new[] { "/usr/bin/readelf", "/usr/bin/llvm-readelf" }.FirstOrDefault(File.Exists);
        if (readelf == null) return;
        using var tmp = new TempDir();
        var path = tmp.File("libmain.so");
        File.WriteAllBytes(path, ElfNeededPatcher.AddNeeded(Fixtures.LibMainArm64, "libncsloader.so").Data);
        var psi = new ProcessStartInfo(readelf, $"-d -l -S -W \"{path}\"") { RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        Assert.Contains("Shared library: [libncsloader.so]", stdout);
        Assert.DoesNotContain("Warning", stderr);
    }

    [Fact]
    public void Rejects_non_elf()
    {
        Assert.ThrowsAny<Exception>(() => ElfNeededPatcher.AddNeeded(new byte[64], "libncsloader.so"));
    }
}
