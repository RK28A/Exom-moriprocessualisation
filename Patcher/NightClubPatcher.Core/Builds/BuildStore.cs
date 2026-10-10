using NightClubPatcher.Core.Infrastructure;

namespace NightClubPatcher.Core.Builds;

public sealed class BuildRecord
{
    public string FileName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Mode { get; set; } = "FastPatch";
    public string? OriginalSha256 { get; set; }
    public string? LoaderSha256 { get; set; }
    public List<string> InjectedLibraries { get; set; } = new();
    public List<string> SplitFiles { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
    public string? Notes { get; set; }

    /// <summary>Headset selected when the build was made (the APK itself runs on every supported Quest).</summary>
    public string? TargetDevice { get; set; }
}

public sealed class BuildManifest
{
    public string Package { get; set; } = GameConstants.PackageName;
    public string VersionName { get; set; } = "";
    public long VersionCode { get; set; }
    public BuildRecord? Current { get; set; }
    public BuildRecord? Previous { get; set; }
    public List<BuildRecord> History { get; set; } = new();
}

/// <summary>
/// Builds/&lt;version&gt;-&lt;code&gt;/patched-current.apk + patched-previous.apk + build.json.
/// </summary>
public sealed class BuildStore
{
    public const string CurrentFile = "patched-current.apk";
    public const string PreviousFile = "patched-previous.apk";
    public const string ManifestFile = "build.json";

    private readonly KitPaths _paths;
    private readonly KitLog _log;

    public BuildStore(KitPaths paths, KitLog log)
    {
        _paths = paths;
        _log = log;
    }

    public string Dir(string versionName, long versionCode) => _paths.BuildDir(versionName, versionCode);

    public BuildManifest Load(string versionName, long versionCode)
    {
        var f = Path.Combine(Dir(versionName, versionCode), ManifestFile);
        return (File.Exists(f) ? Json.Read<BuildManifest>(f) : null)
               ?? new BuildManifest { VersionName = versionName, VersionCode = versionCode };
    }

    public string? CurrentApk(string versionName, long versionCode)
    {
        var p = Path.Combine(Dir(versionName, versionCode), CurrentFile);
        return File.Exists(p) ? p : null;
    }

    public string? PreviousApk(string versionName, long versionCode)
    {
        var p = Path.Combine(Dir(versionName, versionCode), PreviousFile);
        return File.Exists(p) ? p : null;
    }

    /// <summary>Rotates current → previous and stores the new build as current.</summary>
    public string Commit(string versionName, long versionCode, string signedApk, BuildRecord record, IReadOnlyList<string>? signedSplits = null)
    {
        var dir = Dir(versionName, versionCode);
        Directory.CreateDirectory(dir);
        var manifest = Load(versionName, versionCode);
        var current = Path.Combine(dir, CurrentFile);
        var previous = Path.Combine(dir, PreviousFile);

        if (File.Exists(current))
        {
            File.Copy(current, previous, overwrite: true);
            RotateSplits(dir);
            manifest.Previous = manifest.Current;
            if (manifest.Previous != null) manifest.Previous.FileName = PreviousFile;
        }

        File.Copy(signedApk, current, overwrite: true);
        record.FileName = CurrentFile;
        record.Sha256 = Hashing.Sha256File(current);
        record.SplitFiles.Clear();
        foreach (var split in signedSplits ?? Array.Empty<string>())
        {
            var name = "current_" + Path.GetFileName(split);
            File.Copy(split, Path.Combine(dir, name), overwrite: true);
            record.SplitFiles.Add(name);
        }
        manifest.Current = record;
        manifest.History.Add(new BuildRecord
        {
            FileName = $"(history) {record.CreatedAt:yyyy-MM-dd HH:mm:ss}",
            Sha256 = record.Sha256,
            Mode = record.Mode,
            LoaderSha256 = record.LoaderSha256,
            InjectedLibraries = new(record.InjectedLibraries),
            CreatedAt = record.CreatedAt,
        });
        Json.Write(Path.Combine(dir, ManifestFile), manifest);
        _log.Success($"Saved {current}");
        return current;
    }

    private static void RotateSplits(string dir)
    {
        foreach (var old in Directory.EnumerateFiles(dir, "previous_*.apk")) File.Delete(old);
        foreach (var cur in Directory.EnumerateFiles(dir, "current_*.apk"))
            File.Copy(cur, Path.Combine(dir, "previous_" + Path.GetFileName(cur)["current_".Length..]), overwrite: true);
    }

    public IReadOnlyList<string> SplitsFor(string versionName, long versionCode, bool previous)
    {
        var dir = Dir(versionName, versionCode);
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.EnumerateFiles(dir, previous ? "previous_*.apk" : "current_*.apk").OrderBy(f => f).ToList();
    }
}
