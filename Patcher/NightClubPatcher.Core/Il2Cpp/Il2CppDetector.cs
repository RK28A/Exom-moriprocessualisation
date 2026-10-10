using System.Buffers.Binary;
using NightClubPatcher.Core.Apk;

namespace NightClubPatcher.Core.Il2Cpp;

public sealed record Il2CppInfo(bool HasLibIl2Cpp, bool HasMetadata, bool MetadataMagicValid, int? MetadataVersion)
{
    public bool IsIl2Cpp => HasLibIl2Cpp && HasMetadata && MetadataMagicValid;
}

public static class Il2CppDetector
{
    public const uint MetadataMagic = 0xFAB11BAF;

    public static Il2CppInfo Detect(ZipArchiveReader zip)
    {
        var lib = zip.Contains(GameConstants.LibIl2CppEntry);
        var meta = zip.Find(GameConstants.MetadataEntry);
        if (meta == null) return new Il2CppInfo(lib, false, false, null);
        var head = zip.ReadPrefix(meta, 8);
        if (head.Length < 8) return new Il2CppInfo(lib, true, false, null);
        var magicOk = BinaryPrimitives.ReadUInt32LittleEndian(head) == MetadataMagic;
        int? ver = magicOk ? BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(4)) : null;
        return new Il2CppInfo(lib, true, magicOk, ver);
    }
}
