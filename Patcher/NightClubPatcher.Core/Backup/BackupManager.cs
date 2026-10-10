using NightClubPatcher.Core.Adb;
using NightClubPatcher.Core.Apk;
using NightClubPatcher.Core.Infrastructure;

namespace NightClubPatcher.Core.Backup;

public sealed class BackupMetadata
{
    public string Package { get; set; } = GameConstants.PackageName;
    public string VersionName { get; set; } = "";
    public long VersionCode { get; set; }
    public string? UnityVersion { get; set; }
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    public string? SignerSha256 { get; set; }
    public string? SignerSubject { get; set; }
    public string? SignerJavaHash { get; set; }
    public string Source { get; set; } = "Quest";
    public string? DeviceSerial { get; set; }
    public string? DeviceModel { get; set; }
    public string? DeviceApkPath { get; set; }
    public List<SplitBackup> Splits { get; set; } = new();
    public Dictionary<string, string> FileHashes { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
    public bool Immutable { get; set; } = true;
}

public sealed class SplitBackup
{
    public string FileName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string? DevicePath { get; set; }
}

public sealed record OriginalBackup(string Directory, string ApkPath, BackupMetadata? Metadata)
{
    public IEnumerable<string> SplitPaths =>
        Metadata?.Splits.Select(s => System.IO.Path.Combine(Directory, s.FileName)) ?? Enumerable.Empty<string>();
}

public sealed record BackupVerification(bool Ok, IReadOnlyList<string> Problems, ApkAnalysis? Analysis);

public enum BackupOutcome { Created, AlreadyExists, RefusedModified, RefusedWrongPackage, RefusedInvalid, ExistingDiffers, Failed }

public sealed record BackupResult(BackupOutcome Outcome, string Message, OriginalBackup? Backup);

/// <summary>
/// Manages Backups/&lt;package&gt;/&lt;version&gt;-&lt;code&gt;/original.apk.
/// The original backup is NEVER overwritten by any code path in this class.
/// </summary>
public sealed class BackupManager
{
    public const string OriginalFile = "original.apk";
    public const string ShaFile = "original.sha256";
    public const string MetaFile = "backup.json";

    private readonly KitPaths _paths;
    private readonly KitLog _log;

    public BackupManager(KitPaths paths, KitLog log)
    {
        _paths = paths;
        _log = log;
    }

    public OriginalBackup? Find(string? versionName, long? versionCode)
    {
        if (versionName == null || versionCode == null) return null;
        var dir = _paths.BackupDir(versionName, versionCode.Value);
        var apk = Path.Combine(dir, OriginalFile);
        return File.Exists(apk) ? Load(dir) : null;
    }

    /// <summary>All backups on disk, newest version first. Works even without backup.json.</summary>
    public List<OriginalBackup> All()
    {
        var list = new List<OriginalBackup>();
        if (!Directory.Exists(_paths.Backups)) return list;
        foreach (var apk in Directory.EnumerateFiles(_paths.Backups, OriginalFile, SearchOption.AllDirectories))
            list.Add(Load(Path.GetDirectoryName(apk)!));
        return list.OrderByDescending(b => b.Metadata?.VersionCode ?? 0).ThenByDescending(b => File.GetLastWriteTimeUtc(b.ApkPath)).ToList();
    }

    public OriginalBackup? Latest() => All().FirstOrDefault();

    private static OriginalBackup Load(string dir)
    {
        BackupMetadata? meta = null;
        var metaPath = Path.Combine(dir, MetaFile);
        try { if (File.Exists(metaPath)) meta = Json.Read<BackupMetadata>(metaPath); }
        catch { meta = null; }
        return new OriginalBackup(dir, Path.Combine(dir, OriginalFile), meta);
    }

    /// <summary>
    /// Integrity check usable without backup.json: SHA-256 against original.sha256 (when present),
    /// package name, version, and absence of modification markers.
    /// </summary>
    public BackupVerification Verify(OriginalBackup backup, ApkInspectorOptions? options = null)
    {
        var problems = new List<string>();
        if (!File.Exists(backup.ApkPath))
            return new BackupVerification(false, new[] { "original.apk is missing." }, null);

        ApkAnalysis analysis;
        try
        {
            analysis = ApkInspector.Analyze(backup.ApkPath, options ?? new ApkInspectorOptions());
        }
        catch (Exception ex)
        {
            return new BackupVerification(false, new[] { "original.apk is unreadable: " + ex.Message }, null);
        }

        var shaPath = Path.Combine(backup.Directory, ShaFile);
        string? expected = null;
        if (File.Exists(shaPath)) expected = File.ReadAllText(shaPath).Split(' ', '\t', '\n')[0].Trim();
        expected ??= backup.Metadata?.Sha256;
        if (expected == null) problems.Add("No recorded SHA-256 (original.sha256 / backup.json missing). Integrity cannot be proven.");
        else if (!Hashing.Equal(expected, analysis.ApkSha256)) problems.Add($"SHA-256 mismatch: expected {expected}, actual {analysis.ApkSha256}.");

        if (!analysis.IsTargetPackage) problems.Add($"Wrong package: {analysis.Manifest.Package}.");
        if (backup.Metadata != null)
        {
            if (backup.Metadata.VersionCode != analysis.Manifest.VersionCode) problems.Add("versionCode differs from backup.json.");
            if (backup.Metadata.VersionName != analysis.Manifest.VersionName) problems.Add("versionName differs from backup.json.");
        }
        if (analysis.IsPatchedByModKit) problems.Add("Backup contains NCMK modifications!");

        foreach (var split in backup.Metadata?.Splits ?? new())
        {
            var p = Path.Combine(backup.Directory, split.FileName);
            if (!File.Exists(p)) problems.Add($"Split {split.FileName} is missing.");
            else if (!Hashing.Equal(split.Sha256, Hashing.Sha256File(p))) problems.Add($"Split {split.FileName} hash mismatch.");
        }

        return new BackupVerification(problems.Count == 0, problems, analysis);
    }

    /// <summary>
    /// Pulls the installed APK(s) and stores them as the immutable original backup, unless the
    /// installed game looks modified or a backup for that version already exists.
    /// </summary>
    public BackupResult CreateFromDevice(AdbClient adb, ApkInspectorOptions inspectOptions)
    {
        if (!adb.IsPackageInstalled(GameConstants.PackageName))
            return new BackupResult(BackupOutcome.Failed, $"{GameConstants.PackageName} is not installed on the device.", null);

        var paths = adb.PackagePaths(GameConstants.PackageName);
        if (paths.Count == 0)
            return new BackupResult(BackupOutcome.Failed, "pm path returned no APK.", null);

        var incoming = Path.Combine(_paths.BackupRoot, ".incoming", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(incoming);
        try
        {
            _log.Step("Pulling original APK");
            _log.Info(paths[0]);
            var basePulled = Path.Combine(incoming, "base.apk");
            if (!adb.Pull(paths[0], basePulled))
                return new BackupResult(BackupOutcome.Failed, "adb pull of base.apk failed.", null);

            var pulledSplits = new List<(string local, string remote)>();
            foreach (var split in paths.Skip(1))
            {
                var local = Path.Combine(incoming, Path.GetFileName(split));
                _log.Step($"Pulling split {split}");
                if (!adb.Pull(split, local))
                    return new BackupResult(BackupOutcome.Failed, $"adb pull of {split} failed.", null);
                pulledSplits.Add((local, split));
            }

            _log.Step("Validating pulled APK");
            ApkAnalysis analysis;
            try { analysis = ApkInspector.Analyze(basePulled, inspectOptions); }
            catch (Exception ex) { return new BackupResult(BackupOutcome.RefusedInvalid, "Pulled APK is invalid: " + ex.Message, null); }

            if (!analysis.IsTargetPackage)
                return new BackupResult(BackupOutcome.RefusedWrongPackage, $"Pulled APK is {analysis.Manifest.Package}, not {GameConstants.PackageName}.", null);
            if (!analysis.LooksComplete)
                return new BackupResult(BackupOutcome.RefusedInvalid, "Pulled APK is missing: " + string.Join(", ", analysis.MissingRequiredEntries), null);
            if (analysis.ModificationIndicators.Count > 0)
                return new BackupResult(BackupOutcome.RefusedModified,
                    "Installed APK appears modified. Original backup will not be replaced." + Environment.NewLine +
                    string.Join(Environment.NewLine, analysis.ModificationIndicators.Select(i => " - " + i)), null);

            var existing = Find(analysis.Manifest.VersionName, analysis.Manifest.VersionCode);
            if (existing != null)
            {
                var existingSha = existing.Metadata?.Sha256 ?? Hashing.Sha256File(existing.ApkPath);
                return Hashing.Equal(existingSha, analysis.ApkSha256)
                    ? new BackupResult(BackupOutcome.AlreadyExists, "Original backup already exists and is identical.", existing)
                    : new BackupResult(BackupOutcome.ExistingDiffers,
                        "An original backup for this version already exists but the installed APK differs. The existing backup was kept untouched.", existing);
            }

            var dir = _paths.BackupDir(analysis.Manifest.VersionName ?? "unknown", analysis.Manifest.VersionCode ?? 0);
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, OriginalFile);
            if (File.Exists(target))
                return new BackupResult(BackupOutcome.AlreadyExists, "original.apk already exists; not overwriting.", Load(dir));

            File.Move(basePulled, target);
            var meta = new BackupMetadata
            {
                VersionName = analysis.Manifest.VersionName ?? "",
                VersionCode = analysis.Manifest.VersionCode ?? 0,
                UnityVersion = analysis.UnityVersion,
                Sha256 = analysis.ApkSha256!,
                SizeBytes = analysis.SizeBytes,
                SignerSha256 = analysis.Signer?.CertSha256,
                SignerSubject = analysis.Signer?.Subject,
                SignerJavaHash = analysis.Signer?.JavaHash,
                Source = "Quest",
                DeviceSerial = adb.Serial,
                DeviceModel = adb.IdentifyHeadset().DisplayName,
                DeviceApkPath = paths[0],
                FileHashes = new Dictionary<string, string>(analysis.FileHashes),
                CreatedAt = DateTimeOffset.Now,
                Immutable = true,
            };
            foreach (var (local, remote) in pulledSplits)
            {
                var name = "split_" + Path.GetFileName(local);
                var dest = Path.Combine(dir, name);
                File.Move(local, dest);
                meta.Splits.Add(new SplitBackup { FileName = name, Sha256 = Hashing.Sha256File(dest), DevicePath = remote });
            }

            File.WriteAllText(Path.Combine(dir, ShaFile), $"{meta.Sha256}  {OriginalFile}{Environment.NewLine}");
            Json.Write(Path.Combine(dir, MetaFile), meta);
            MakeReadOnly(dir);

            _log.Success($"Original backup created: {target}");
            if (meta.Splits.Count > 0) _log.Warn($"Game uses {meta.Splits.Count} split APK(s); they were backed up too.");
            return new BackupResult(BackupOutcome.Created, "Original backup created.", Load(dir));
        }
        finally
        {
            try { Directory.Delete(incoming, true); } catch { /* ignore */ }
        }
    }

    /// <summary>Imports an APK file chosen by the user (same rules as a device backup).</summary>
    public BackupResult ImportFromFile(string apkPath, ApkInspectorOptions inspectOptions)
    {
        var analysis = ApkInspector.Analyze(apkPath, inspectOptions);
        if (!analysis.IsTargetPackage)
            return new BackupResult(BackupOutcome.RefusedWrongPackage, $"APK is {analysis.Manifest.Package}.", null);
        if (analysis.ModificationIndicators.Count > 0)
            return new BackupResult(BackupOutcome.RefusedModified, "APK appears modified: " + string.Join("; ", analysis.ModificationIndicators), null);
        var existing = Find(analysis.Manifest.VersionName, analysis.Manifest.VersionCode);
        if (existing != null)
            return new BackupResult(BackupOutcome.AlreadyExists, "A backup for this version already exists; not overwriting.", existing);

        var dir = _paths.BackupDir(analysis.Manifest.VersionName ?? "unknown", analysis.Manifest.VersionCode ?? 0);
        Directory.CreateDirectory(dir);
        File.Copy(apkPath, Path.Combine(dir, OriginalFile), overwrite: false);
        var meta = new BackupMetadata
        {
            VersionName = analysis.Manifest.VersionName ?? "",
            VersionCode = analysis.Manifest.VersionCode ?? 0,
            UnityVersion = analysis.UnityVersion,
            Sha256 = analysis.ApkSha256!,
            SizeBytes = analysis.SizeBytes,
            SignerSha256 = analysis.Signer?.CertSha256,
            SignerSubject = analysis.Signer?.Subject,
                SignerJavaHash = analysis.Signer?.JavaHash,
            Source = "File:" + Path.GetFileName(apkPath),
            FileHashes = new Dictionary<string, string>(analysis.FileHashes),
            CreatedAt = DateTimeOffset.Now,
        };
        File.WriteAllText(Path.Combine(dir, ShaFile), $"{meta.Sha256}  {OriginalFile}{Environment.NewLine}");
        Json.Write(Path.Combine(dir, MetaFile), meta);
        MakeReadOnly(dir);
        return new BackupResult(BackupOutcome.Created, "Original backup imported.", Load(dir));
    }

    private static void MakeReadOnly(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir))
        {
            try { File.SetAttributes(f, File.GetAttributes(f) | FileAttributes.ReadOnly); }
            catch { /* best effort */ }
        }
    }
}
