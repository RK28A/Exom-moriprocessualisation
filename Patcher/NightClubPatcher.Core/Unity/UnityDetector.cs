using System.Text;
using System.Text.RegularExpressions;
using NightClubPatcher.Core.Apk;

namespace NightClubPatcher.Core.Unity;

public static partial class UnityDetector
{
    [GeneratedRegex(@"\b(20\d\d|6\d\d\d)\.\d+\.\d+[abfpx]\d+\b")]
    private static partial Regex UnityVersionRegex();

    /// <summary>
    /// Detects the Unity version from the UnityFS header of data.unity3d, falling back to scanning
    /// libunity.so for a version string.
    /// </summary>
    public static (string? version, string source) Detect(ZipArchiveReader zip)
    {
        var data = zip.Find(GameConstants.DataUnity3dEntry);
        if (data != null)
        {
            var v = FromUnityFsHeader(zip.ReadPrefix(data, 256));
            if (v != null) return (v, "data.unity3d header");
        }

        foreach (var name in new[] { "assets/bin/Data/globalgamemanagers", "assets/bin/Data/data.unity3d" })
        {
            var e = zip.Find(name);
            if (e == null) continue;
            var m = UnityVersionRegex().Match(Encoding.ASCII.GetString(zip.ReadPrefix(e, 4096)));
            if (m.Success) return (m.Value, name);
        }

        var lib = zip.Find(GameConstants.LibUnityEntry);
        if (lib != null)
        {
            var v = ScanForVersion(zip.OpenRead(lib));
            if (v != null) return (v, "libunity.so strings");
        }
        return (null, "not found");
    }

    /// <summary>UnityFS header: "UnityFS\0" u32be formatVersion "5.x.x\0" "6000.2.2f1\0" ...</summary>
    public static string? FromUnityFsHeader(byte[] head)
    {
        if (head.Length < 16) return null;
        var sig = ReadCString(head, 0, out var p);
        if (sig is not ("UnityFS" or "UnityWeb" or "UnityRaw")) return null;
        p += 4;
        ReadCString(head, p, out p);                       // player version, e.g. 5.x.x
        var engine = ReadCString(head, p, out _);
        return UnityVersionRegex().IsMatch(engine) ? engine : null;
    }

    private static string ReadCString(byte[] b, int start, out int next)
    {
        var end = start;
        while (end < b.Length && b[end] != 0) end++;
        next = Math.Min(end + 1, b.Length);
        return Encoding.ASCII.GetString(b, start, end - start);
    }

    private static string? ScanForVersion(Stream s)
    {
        using (s)
        {
            var counts = new Dictionary<string, int>();
            var buf = new byte[1 << 20];
            var carry = "";
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0)
            {
                var text = carry + Encoding.Latin1.GetString(buf, 0, n);
                foreach (Match m in UnityVersionRegex().Matches(text))
                    counts[m.Value] = counts.GetValueOrDefault(m.Value) + 1;
                carry = text.Length > 32 ? text[^32..] : text;
            }
            return counts.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
        }
    }
}
