using System.Buffers.Binary;
using System.Text;

namespace NightClubPatcher.Core.Android;

public sealed class ManifestInfo
{
    public string? Package { get; set; }
    public long? VersionCode { get; set; }
    public string? VersionName { get; set; }
    public int? MinSdk { get; set; }
    public int? TargetSdk { get; set; }
    public bool? ExtractNativeLibs { get; set; }
    public bool? Debuggable { get; set; }
    public List<string> Permissions { get; } = new();
    public List<string> Features { get; } = new();
    public List<string> MetaDataNames { get; } = new();
    public Dictionary<string, string> MetaData { get; } = new(StringComparer.Ordinal);

    /// <summary>Value of com.oculus.supportedDevices, e.g. "quest2|questpro|quest3|quest3s".</summary>
    public string? SupportedDevices => MetaData.TryGetValue("com.oculus.supportedDevices", out var v) ? v : null;
    public List<string> Activities { get; } = new();
    public List<string> AllStrings { get; } = new();
}

/// <summary>
/// Read-only parser for Android binary XML (compiled AndroidManifest.xml).
/// Attributes are identified by resource id first (robust against obfuscated attribute names)
/// and by name as a fallback.
/// </summary>
public static class AxmlManifest
{
    private const uint AttrVersionCode = 0x0101021b;
    private const uint AttrVersionName = 0x0101021c;
    private const uint AttrMinSdk = 0x0101020c;
    private const uint AttrTargetSdk = 0x01010270;
    private const uint AttrExtractNativeLibs = 0x010104ea;
    private const uint AttrDebuggable = 0x0101000f;
    private const uint AttrName = 0x01010003;
    private const uint AttrValue = 0x01010024;

    private const byte TypeReference = 0x01, TypeString = 0x03, TypeIntDec = 0x10, TypeIntHex = 0x11, TypeBool = 0x12;

    public static ManifestInfo Parse(byte[] data)
    {
        var info = new ManifestInfo();
        var d = data.AsSpan();
        if (d.Length < 8 || BinaryPrimitives.ReadUInt16LittleEndian(d) != 0x0003)
            throw new InvalidDataException("AndroidManifest.xml is not compiled binary XML.");

        string[] strings = Array.Empty<string>();
        uint[] resIds = Array.Empty<uint>();
        int pos = BinaryPrimitives.ReadUInt16LittleEndian(d[2..]);
        var stack = new Stack<string>();

        while (pos + 8 <= d.Length)
        {
            var type = BinaryPrimitives.ReadUInt16LittleEndian(d[pos..]);
            var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(d[(pos + 2)..]);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[(pos + 4)..]);
            if (size <= 0 || pos + size > d.Length) break;
            var chunk = d.Slice(pos, size);

            switch (type)
            {
                case 0x0001:
                    strings = ReadStringPool(chunk);
                    info.AllStrings.AddRange(strings);
                    break;
                case 0x0180:
                    resIds = new uint[(size - headerSize) / 4];
                    for (var i = 0; i < resIds.Length; i++)
                        resIds[i] = BinaryPrimitives.ReadUInt32LittleEndian(chunk[(headerSize + i * 4)..]);
                    break;
                case 0x0102:
                    HandleStartElement(chunk, strings, resIds, info, stack);
                    break;
                case 0x0103:
                    if (stack.Count > 0) stack.Pop();
                    break;
            }
            pos += size;
        }
        return info;
    }

    private static void HandleStartElement(ReadOnlySpan<byte> c, string[] strings, uint[] resIds, ManifestInfo info, Stack<string> stack)
    {
        var nameIdx = (int)BinaryPrimitives.ReadUInt32LittleEndian(c[20..]);
        var attrStart = BinaryPrimitives.ReadUInt16LittleEndian(c[24..]);
        var attrSize = BinaryPrimitives.ReadUInt16LittleEndian(c[26..]);
        var attrCount = BinaryPrimitives.ReadUInt16LittleEndian(c[28..]);
        var element = Str(strings, nameIdx) ?? "";
        var parent = stack.Count > 0 ? stack.Peek() : "";
        stack.Push(element);

        var attrs = new List<(string name, uint resId, string? str, uint data, byte dataType)>();
        for (var i = 0; i < attrCount; i++)
        {
            var a = c[(16 + attrStart + i * attrSize)..];
            var an = (int)BinaryPrimitives.ReadUInt32LittleEndian(a[4..]);
            var raw = (int)BinaryPrimitives.ReadUInt32LittleEndian(a[8..]);
            var dataType = a[15];
            var value = BinaryPrimitives.ReadUInt32LittleEndian(a[16..]);
            var resId = an >= 0 && an < resIds.Length ? resIds[an] : 0;
            string? str = raw >= 0 ? Str(strings, raw) : null;
            if (str == null && dataType == TypeString) str = Str(strings, (int)value);
            attrs.Add((Str(strings, an) ?? "", resId, str, value, dataType));
        }

        string? Get(uint id, string name, out uint data, out byte dt)
        {
            foreach (var a in attrs)
            {
                if ((id != 0 && a.resId == id) || (a.resId == 0 && a.name == name))
                {
                    data = a.data;
                    dt = a.dataType;
                    return a.str;
                }
            }
            data = 0;
            dt = 0;
            return null;
        }

        bool Has(uint id, string name) => attrs.Any(a => (id != 0 && a.resId == id) || (a.resId == 0 && a.name == name));

        switch (element)
        {
            case "manifest":
                info.Package = attrs.FirstOrDefault(a => a.name == "package").str;
                if (Has(AttrVersionCode, "versionCode"))
                {
                    var s = Get(AttrVersionCode, "versionCode", out var v, out var dt);
                    info.VersionCode = dt is TypeIntDec or TypeIntHex ? v : long.TryParse(s, out var l) ? l : null;
                }
                if (Has(AttrVersionName, "versionName"))
                {
                    var s = Get(AttrVersionName, "versionName", out var v, out var dt);
                    info.VersionName = s ?? (dt == TypeReference ? $"@0x{v:X8}" : v.ToString());
                }
                break;
            case "uses-sdk":
                if (Has(AttrMinSdk, "minSdkVersion")) { Get(AttrMinSdk, "minSdkVersion", out var v, out _); info.MinSdk = (int)v; }
                if (Has(AttrTargetSdk, "targetSdkVersion")) { Get(AttrTargetSdk, "targetSdkVersion", out var v, out _); info.TargetSdk = (int)v; }
                break;
            case "application":
                if (Has(AttrExtractNativeLibs, "extractNativeLibs"))
                {
                    Get(AttrExtractNativeLibs, "extractNativeLibs", out var v, out _);
                    info.ExtractNativeLibs = v != 0;
                }
                if (Has(AttrDebuggable, "debuggable"))
                {
                    Get(AttrDebuggable, "debuggable", out var v, out _);
                    info.Debuggable = v != 0;
                }
                break;
            case "uses-permission":
                AddName(info.Permissions);
                break;
            case "uses-feature":
                AddName(info.Features);
                break;
            case "meta-data":
                AddName(info.MetaDataNames);
                var key = Get(AttrName, "name", out _, out _);
                if (!string.IsNullOrEmpty(key) && Has(AttrValue, "value"))
                {
                    var str = Get(AttrValue, "value", out var raw, out var type);
                    info.MetaData[key] = str ?? (type == TypeBool ? (raw != 0).ToString().ToLowerInvariant() : raw.ToString());
                }
                break;
            case "activity":
            case "activity-alias":
                if (parent == "application") AddName(info.Activities);
                break;
        }

        void AddName(List<string> target)
        {
            var n = Get(AttrName, "name", out _, out _);
            if (!string.IsNullOrEmpty(n)) target.Add(n);
        }
    }

    private static string? Str(string[] pool, int idx) => idx >= 0 && idx < pool.Length ? pool[idx] : null;

    private static string[] ReadStringPool(ReadOnlySpan<byte> c)
    {
        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(c[2..]);
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(c[8..]);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(c[16..]);
        var stringsStart = (int)BinaryPrimitives.ReadUInt32LittleEndian(c[20..]);
        var utf8 = (flags & 0x100) != 0;
        var result = new string[count];
        for (var i = 0; i < count; i++)
        {
            var off = (int)BinaryPrimitives.ReadUInt32LittleEndian(c[(headerSize + i * 4)..]);
            var p = stringsStart + off;
            try
            {
                if (utf8)
                {
                    // utf16 length (skip), then utf8 byte length
                    p += (c[p] & 0x80) != 0 ? 2 : 1;
                    int len = c[p];
                    if ((len & 0x80) != 0) { len = ((len & 0x7F) << 8) | c[p + 1]; p += 2; }
                    else p += 1;
                    result[i] = Encoding.UTF8.GetString(c.Slice(p, len));
                }
                else
                {
                    int len = BinaryPrimitives.ReadUInt16LittleEndian(c[p..]);
                    if ((len & 0x8000) != 0)
                    {
                        len = ((len & 0x7FFF) << 16) | BinaryPrimitives.ReadUInt16LittleEndian(c[(p + 2)..]);
                        p += 4;
                    }
                    else p += 2;
                    result[i] = Encoding.Unicode.GetString(c.Slice(p, len * 2));
                }
            }
            catch
            {
                result[i] = "";
            }
        }
        return result;
    }
}
