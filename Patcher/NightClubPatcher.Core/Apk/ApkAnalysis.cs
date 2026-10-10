using System.Text.Json;
using NightClubPatcher.Core.Android;
using NightClubPatcher.Core.Elf;
using NightClubPatcher.Core.Il2Cpp;
using NightClubPatcher.Core.Infrastructure;
using NightClubPatcher.Core.Unity;

namespace NightClubPatcher.Core.Apk;

public sealed class PatchMarker
{
    public string Tool { get; set; } = "NightClubModKit";
    public string ToolVersion { get; set; } = "";
    public string Mode { get; set; } = "FastPatch";
    public int LoaderApi { get; set; } = GameConstants.LoaderApi;
    public string? OriginalApkSha256 { get; set; }
    public string? OriginalLibMainSha256 { get; set; }
    public string? LoaderSha256 { get; set; }
    public List<string> InjectedLibraries { get; set; } = new();
    public DateTimeOffset PatchedAt { get; set; }
}

/// <summary>Everything the toolkit knows about one APK file.</summary>
public sealed class ApkAnalysis
{
    public required string Path { get; init; }
    public required ManifestInfo Manifest { get; init; }
    public string? ApkSha256 { get; set; }
    public long SizeBytes { get; init; }
    public List<string> MissingRequiredEntries { get; } = new();
    public Dictionary<string, string> FileHashes { get; } = new(StringComparer.Ordinal);
    public List<string> Abis { get; } = new();
    public bool IsArm64 { get; set; }
    public string? LibIl2CppMachine { get; set; }
    public string? UnityVersion { get; set; }
    public string? UnityVersionSource { get; set; }
    public required Il2CppInfo Il2Cpp { get; init; }
    public string? XrRuntime { get; set; }
    public List<string> XrEvidence { get; } = new();
    public ApkSignerInfo? Signer { get; set; }
    public bool HasLoader { get; set; }
    public bool LibMainNeedsLoader { get; set; }
    public List<string> LibMainNeeded { get; } = new();
    public List<string> EmbeddedMods { get; } = new();
    public PatchMarker? Marker { get; set; }
    public List<string> SplitNames { get; } = new();
    public List<string> ModificationIndicators { get; } = new();

    public bool IsPatchedByModKit => HasLoader || LibMainNeedsLoader || Marker != null;
    public bool IsTargetPackage => GameConstants.IsTargetPackage(Manifest.Package);
    public bool LooksComplete => MissingRequiredEntries.Count == 0;

    public string? Hash(string entry) => FileHashes.TryGetValue(entry, out var h) ? h : null;

    public string VersionLabel => $"{Manifest.VersionName ?? "?"} ({Manifest.VersionCode?.ToString() ?? "?"})";
}

public sealed class ApkInspectorOptions
{
    public bool ComputeApkHash { get; init; } = true;
    public bool ComputeFileHashes { get; init; } = true;

    /// <summary>Certificate SHA-256 of the NightClubModKit signing key, to flag APKs we signed.</summary>
    public string? ModKitSignerSha256 { get; init; }

    /// <summary>Certificate SHA-256 of the original developer key, when known.</summary>
    public string? ExpectedOriginalSignerSha256 { get; init; }
}

public static class ApkInspector
{
    public static readonly string[] HashedEntries =
    {
        GameConstants.LibMainEntry, GameConstants.LibUnityEntry, GameConstants.LibIl2CppEntry, GameConstants.MetadataEntry,
    };

    public static ApkAnalysis Analyze(string apkPath, ApkInspectorOptions? options = null)
    {
        options ??= new ApkInspectorOptions();
        using var zip = new ZipArchiveReader(apkPath);

        var manifestEntry = zip.Find("AndroidManifest.xml") ?? throw new InvalidDataException("APK has no AndroidManifest.xml.");
        var manifest = AxmlManifest.Parse(zip.ReadAllBytes(manifestEntry));

        var a = new ApkAnalysis
        {
            Path = apkPath,
            Manifest = manifest,
            SizeBytes = zip.Length,
            Il2Cpp = Il2CppDetector.Detect(zip),
        };

        foreach (var req in GameConstants.RequiredEntries)
            if (!zip.Contains(req)) a.MissingRequiredEntries.Add(req);

        a.Abis.AddRange(zip.Entries
            .Where(e => e.Name.StartsWith("lib/") && e.Name.Count(c => c == '/') == 2)
            .Select(e => e.Name.Split('/')[1]).Distinct().OrderBy(s => s));

        var il2cpp = zip.Find(GameConstants.LibIl2CppEntry);
        if (il2cpp != null)
        {
            var head = zip.ReadPrefix(il2cpp, 64);
            if (ElfFile.LooksLikeElf(head) && head.Length >= 20)
            {
                var machine = BitConverter.ToUInt16(head, 18);
                a.LibIl2CppMachine = machine == ElfConst.EM_AARCH64 ? "AArch64" : $"machine {machine}";
                a.IsArm64 = machine == ElfConst.EM_AARCH64 && head[4] == 2;
            }
        }

        (a.UnityVersion, a.UnityVersionSource) = UnityDetector.Detect(zip);

        DetectXr(zip, a);

        a.Signer = ApkSignatureReader.Read(zip);

        a.HasLoader = zip.Contains(GameConstants.LoaderEntry);
        a.EmbeddedMods.AddRange(zip.Entries
            .Where(e => e.Name.StartsWith(GameConstants.LibDir + GameConstants.ModLibPrefix) && e.Name.EndsWith(".so"))
            .Select(e => e.Name[GameConstants.LibDir.Length..]));

        var libmain = zip.Find(GameConstants.LibMainEntry);
        if (libmain != null)
        {
            try
            {
                var elf = new ElfFile(zip.ReadAllBytes(libmain));
                a.LibMainNeeded.AddRange(elf.GetNeeded());
                a.LibMainNeedsLoader = a.LibMainNeeded.Contains(GameConstants.LoaderLibName);
            }
            catch (Exception ex)
            {
                a.ModificationIndicators.Add($"libmain.so could not be parsed: {ex.Message}");
            }
        }

        var marker = zip.Find(GameConstants.PatchMarkerEntry);
        if (marker != null)
        {
            try { a.Marker = JsonSerializer.Deserialize<PatchMarker>(zip.ReadAllBytes(marker), Json.Options); }
            catch { a.Marker = new PatchMarker { Tool = "unreadable marker" }; }
        }

        if (options.ComputeFileHashes)
        {
            foreach (var name in HashedEntries)
            {
                var e = zip.Find(name);
                if (e == null) continue;
                using var s = zip.OpenRead(e);
                a.FileHashes[name] = Hashing.Sha256(s);
            }
        }

        if (options.ComputeApkHash) a.ApkSha256 = Hashing.Sha256File(apkPath);

        // --- modification indicators ---------------------------------------------------
        if (a.HasLoader) a.ModificationIndicators.Add($"{GameConstants.LoaderLibName} is present.");
        if (a.LibMainNeedsLoader) a.ModificationIndicators.Add($"libmain.so depends on {GameConstants.LoaderLibName}.");
        if (a.Marker != null) a.ModificationIndicators.Add("NightClubModKit patch marker found.");
        if (a.EmbeddedMods.Count > 0) a.ModificationIndicators.Add($"Embedded mods: {string.Join(", ", a.EmbeddedMods)}.");
        if (a.Signer == null) a.ModificationIndicators.Add("APK is not signed.");
        if (a.Signer != null && options.ModKitSignerSha256 != null && Hashing.Equal(a.Signer.CertSha256, options.ModKitSignerSha256))
            a.ModificationIndicators.Add("APK is signed with the NightClubModKit key.");
        if (a.Signer != null && options.ExpectedOriginalSignerSha256 != null && !Hashing.Equal(a.Signer.CertSha256, options.ExpectedOriginalSignerSha256))
            a.ModificationIndicators.Add("Signing certificate differs from the original developer certificate.");
        if (manifest.Debuggable == true) a.ModificationIndicators.Add("Manifest is debuggable (store builds normally are not).");

        return a;
    }

    private static void DetectXr(ZipArchiveReader zip, ApkAnalysis a)
    {
        var libs = zip.Entries.Where(e => e.Name.StartsWith(GameConstants.LibDir)).Select(e => e.Name[GameConstants.LibDir.Length..]).ToList();
        foreach (var l in libs)
        {
            if (l.Contains("OVRPlugin", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("openxr", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("UnityOpenXR", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("XRSDKOculus", StringComparison.OrdinalIgnoreCase))
                a.XrEvidence.Add(l);
        }
        foreach (var s in a.Manifest.AllStrings.Where(s =>
                     s.StartsWith("com.oculus.", StringComparison.Ordinal) || s.StartsWith("com.meta.", StringComparison.Ordinal) ||
                     s.StartsWith("horizonos.", StringComparison.Ordinal)).Distinct().Take(12))
            a.XrEvidence.Add(s);

        var hasOculusLib = libs.Any(l => l.Contains("OVRPlugin", StringComparison.OrdinalIgnoreCase) || l.Contains("XRSDKOculus", StringComparison.OrdinalIgnoreCase));
        var hasOpenXr = libs.Any(l => l.Contains("openxr", StringComparison.OrdinalIgnoreCase));
        a.XrRuntime = (hasOculusLib, hasOpenXr) switch
        {
            (true, true) => "Oculus XR + OpenXR (Meta)",
            (true, false) => "Oculus XR",
            (false, true) => "OpenXR (Meta)",
            _ => a.XrEvidence.Count > 0 ? "Meta (manifest only)" : null,
        };
    }
}
