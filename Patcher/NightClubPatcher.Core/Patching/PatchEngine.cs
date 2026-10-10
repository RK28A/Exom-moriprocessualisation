using NightClubPatcher.Core.Apk;
using NightClubPatcher.Core.Backup;
using NightClubPatcher.Core.Builds;
using NightClubPatcher.Core.Elf;
using NightClubPatcher.Core.Infrastructure;
using NightClubPatcher.Core.Signing;
using NightClubPatcher.Core.Versions;

namespace NightClubPatcher.Core.Patching;

public enum PatchMode { Fast, Full }

public sealed class PatchOptions
{
    public PatchMode Mode { get; init; } = PatchMode.Fast;

    /// <summary>Allow patching builds whose fingerprints are not verified (advanced users only).</summary>
    public bool AllowUnverified { get; init; }

    /// <summary>Embed libncsmod_*.so found next to the loader into lib/arm64-v8a/.</summary>
    public bool EmbedMods { get; init; } = true;

    public string? LoaderPath { get; init; }
    public IReadOnlyList<string> ExtraMods { get; init; } = Array.Empty<string>();
}

public sealed record PatchResult(bool Ok, string Message, string? OutputApk, IReadOnlyList<string> Splits, string? WorkspaceRoot = null)
{
    public static PatchResult Fail(string msg, string? ws = null) => new(false, msg, null, Array.Empty<string>(), ws);
}

/// <summary>
/// FAST PATCH: the APK is edited as a ZIP. libmain.so gets a DT_NEEDED on libncsloader.so,
/// the loader (and optional embedded mods) are added uncompressed and page-aligned, a marker is
/// written to assets/, the old signature is stripped, and the result is zipaligned and signed.
/// </summary>
public sealed class PatchEngine
{
    private readonly KitPaths _paths;
    private readonly KitLog _log;
    private readonly BackupManager _backups;
    private readonly ProfileStore _profiles;
    private readonly BuildStore _builds;
    private readonly ApkSigningService _signer;
    private readonly KeystoreManager _keys;

    public PatchEngine(KitPaths paths, KitLog log, BackupManager backups, ProfileStore profiles, BuildStore builds,
        ApkSigningService signer, KeystoreManager keys)
    {
        _paths = paths;
        _log = log;
        _backups = backups;
        _profiles = profiles;
        _builds = builds;
        _signer = signer;
        _keys = keys;
    }

    public PatchResult Patch(OriginalBackup backup, PatchOptions options)
    {
        if (options.Mode == PatchMode.Full)
            return PatchResult.Fail("FULL PATCH (apktool resource rebuild) is not implemented yet. Use FAST PATCH.");

        _log.Step("Verifying original backup");
        var verification = _backups.Verify(backup, InspectOptions());
        if (!verification.Ok || verification.Analysis == null)
            return PatchResult.Fail("Original backup failed verification:" + Environment.NewLine + string.Join(Environment.NewLine, verification.Problems));
        var original = verification.Analysis;

        if (!original.IsTargetPackage) return PatchResult.Fail("Refusing to patch: not " + GameConstants.PackageName);
        if (!original.IsArm64) return PatchResult.Fail("Refusing to patch: libil2cpp.so is not ARM64.");
        if (!original.Il2Cpp.IsIl2Cpp) return PatchResult.Fail("Refusing to patch: the game is not an IL2CPP build.");
        if (!original.LooksComplete) return PatchResult.Fail("Refusing to patch: missing " + string.Join(", ", original.MissingRequiredEntries));

        var compat = _profiles.Evaluate(original);
        _log.Info("Compatibility: " + compat.Summary);
        foreach (var d in compat.Details) _log.Info("  " + d);
        if (compat.Status == Compatibility.WrongPackage) return PatchResult.Fail(compat.Summary);
        if (!compat.AllowsAutomaticPatch && !options.AllowUnverified)
            return PatchResult.Fail(compat.Summary + Environment.NewLine + "Automatic patching is disabled until compatibility is verified (use Advanced > Allow unverified build).");

        var loaderPath = options.LoaderPath ?? _paths.FindLoader();
        if (loaderPath == null || !File.Exists(loaderPath))
            return PatchResult.Fail($"{GameConstants.LoaderLibName} not found. Build it with Native/build.ps1 (Android NDK) - expected in {_paths.NativeOut}.");
        var loaderBytes = File.ReadAllBytes(loaderPath);
        var loaderElf = new ElfFile(loaderBytes);
        if (!loaderElf.IsAarch64) return PatchResult.Fail($"{loaderPath} is {loaderElf.MachineName}, expected AArch64.");

        var ws = PatchWorkspace.Create("patch");
        _log.Info("Workspace: " + ws.Root);
        try
        {
            var working = Path.Combine(ws.Input, "working.apk");
            File.Copy(backup.ApkPath, working);
            File.SetAttributes(working, FileAttributes.Normal);

            using var zip = new ZipArchiveReader(working);

            var mods = CollectMods(options, loaderPath).ToList();
            var (injected, loaderSha) = BuildUnsignedPatchedApk(zip, original, loaderBytes, mods, ws, _log);
            var unsigned = Path.Combine(ws.Patched, "unsigned.apk");

            var signed = Path.Combine(ws.Output, "patched.apk");
            _signer.AlignAndSign(unsigned, Path.Combine(ws.Patched, "aligned.apk"), signed);

            var splits = ResignSplits(backup, ws);

            _log.Step("Verifying patched APK");
            var check = ApkInspector.Analyze(signed, new ApkInspectorOptions { ComputeFileHashes = false });
            if (!check.IsTargetPackage || !check.HasLoader || !check.LibMainNeedsLoader)
                return PatchResult.Fail("Patched APK verification failed (package/loader/DT_NEEDED).", ws.Root);

            var stored = _builds.Commit(original.Manifest.VersionName!, original.Manifest.VersionCode!.Value, signed, new BuildRecord
            {
                Mode = "FastPatch",
                OriginalSha256 = original.ApkSha256,
                LoaderSha256 = loaderSha,
                InjectedLibraries = injected,
                CreatedAt = DateTimeOffset.Now,
            }, splits);

            var storedSplits = _builds.SplitsFor(original.Manifest.VersionName!, original.Manifest.VersionCode!.Value, previous: false);
            return new PatchResult(true, "Patched APK ready.", stored, storedSplits, ws.Root);
        }
        catch (Exception ex)
        {
            _log.Error(ex.ToString());
            return PatchResult.Fail("Patch failed: " + ex.Message, ws.Root);
        }
    }

    /// <summary>
    /// The pure FAST PATCH transformation (no signing): libmain.so DT_NEEDED + loader + mods +
    /// marker, written to ws.Patched/unsigned.apk. Never touches the source file.
    /// </summary>
    internal static (List<string> injected, string loaderSha) BuildUnsignedPatchedApk(
        ZipArchiveReader zip, ApkAnalysis original, byte[] loaderBytes, IReadOnlyList<string> modFiles, PatchWorkspace ws, KitLog log)
    {
        log.Step("Extracting libmain.so");
        var libmainEntry = zip.Find(GameConstants.LibMainEntry) ?? throw new InvalidDataException("libmain.so missing");
        var libmain = zip.ReadAllBytes(libmainEntry);
        File.WriteAllBytes(Path.Combine(ws.Unpacked, "libmain.so"), libmain);

        log.Step($"Adding DT_NEEDED {GameConstants.LoaderLibName} to libmain.so");
        var elfResult = ElfNeededPatcher.AddNeeded(libmain, GameConstants.LoaderLibName);
        log.Info(elfResult.Message);
        File.WriteAllBytes(Path.Combine(ws.Native, "libmain.so"), elfResult.Data);
        File.WriteAllBytes(Path.Combine(ws.Native, GameConstants.LoaderLibName), loaderBytes);

        var plan = new ApkRewritePlan();
        plan.ReplaceEntry(GameConstants.LibMainEntry, elfResult.Data);
        plan.AddOrReplace(GameConstants.LoaderEntry, loaderBytes, EntryCompression.Stored, zip);
        var injected = new List<string> { GameConstants.LoaderLibName };

        foreach (var mod in modFiles)
        {
            var name = Path.GetFileName(mod);
            var bytes = File.ReadAllBytes(mod);
            if (!new ElfFile(bytes).IsAarch64) { log.Warn($"Skipping {name}: not AArch64."); continue; }
            plan.AddOrReplace(GameConstants.LibDir + name, bytes, EntryCompression.Stored, zip);
            injected.Add(name);
            log.Info("Embedding mod " + name);
        }

        var loaderSha = Hashing.Sha256(loaderBytes);
        var marker = new PatchMarker
        {
            ToolVersion = typeof(PatchEngine).Assembly.GetName().Version?.ToString() ?? "0",
            Mode = "FastPatch",
            OriginalApkSha256 = original.ApkSha256,
            OriginalLibMainSha256 = original.Hash(GameConstants.LibMainEntry),
            LoaderSha256 = loaderSha,
            InjectedLibraries = injected,
            PatchedAt = DateTimeOffset.Now,
        };
        plan.AddOrReplace(GameConstants.PatchMarkerEntry, System.Text.Encoding.UTF8.GetBytes(Json.Serialize(marker)), EntryCompression.Deflated, zip);

        if (original.Manifest.ExtractNativeLibs == false)
            log.Info("extractNativeLibs=false: native libraries are stored uncompressed and page-aligned.");

        log.Step("Rebuilding APK");
        ApkRewriter.Rewrite(zip, Path.Combine(ws.Patched, "unsigned.apk"), plan);
        return (injected, loaderSha);
    }

    private IEnumerable<string> CollectMods(PatchOptions options, string loaderPath)
    {
        var mods = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (options.EmbedMods)
        {
            var dirs = new[] { Path.GetDirectoryName(loaderPath)! }.Concat(_paths.ModPayloadDirs()).Distinct();
            foreach (var dir in dirs.Where(Directory.Exists))
                foreach (var f in Directory.EnumerateFiles(dir, GameConstants.ModLibPrefix + "*.so"))
                    mods.TryAdd(Path.GetFileName(f), f);
        }
        foreach (var f in options.ExtraMods) mods[Path.GetFileName(f)] = f;
        return mods.Values;
    }

    /// <summary>Split APKs must carry the same certificate as base.apk, so they are re-signed unchanged.</summary>
    private List<string> ResignSplits(OriginalBackup backup, PatchWorkspace ws)
    {
        var result = new List<string>();
        foreach (var split in backup.SplitPaths)
        {
            var name = Path.GetFileName(split);
            _log.Step("Re-signing split " + name);
            var unsigned = Path.Combine(ws.Patched, "unsigned_" + name);
            ApkRewriter.Rewrite(split, unsigned, new ApkRewritePlan());
            var signed = Path.Combine(ws.Output, name);
            _signer.AlignAndSign(unsigned, Path.Combine(ws.Patched, "aligned_" + name), signed);
            result.Add(signed);
        }
        return result;
    }

    /// <summary>
    /// UNPATCH: removes NightClubModKit changes from a patched APK (original libmain.so from the
    /// backup, loader/mods/marker removed) and re-signs it with the ModKit key, so it can be
    /// installed over the modded game without uninstalling.
    /// </summary>
    public PatchResult Unpatch(string patchedApk, OriginalBackup backup)
    {
        var verification = _backups.Verify(backup, InspectOptions());
        if (!verification.Ok || verification.Analysis == null)
            return PatchResult.Fail("Original backup failed verification: " + string.Join("; ", verification.Problems));

        var patched = ApkInspector.Analyze(patchedApk, InspectOptions());
        if (!patched.IsTargetPackage) return PatchResult.Fail("Refusing: input is not NightClub Simulator.");
        if (patched.Manifest.VersionCode != verification.Analysis.Manifest.VersionCode)
            return PatchResult.Fail("Patched APK and original backup are different game versions.");
        if (!patched.IsPatchedByModKit) return PatchResult.Fail("This APK does not contain NightClubModKit modifications.");

        var ws = PatchWorkspace.Create("unpatch");
        try
        {
            byte[] originalLibMain;
            using (var orig = new ZipArchiveReader(backup.ApkPath))
                originalLibMain = orig.ReadAllBytes(GameConstants.LibMainEntry);

            var working = Path.Combine(ws.Input, "patched.apk");
            File.Copy(patchedApk, working);
            using var zip = new ZipArchiveReader(working);

            var plan = new ApkRewritePlan();
            plan.ReplaceEntry(GameConstants.LibMainEntry, originalLibMain);
            plan.Remove.Add(GameConstants.LoaderEntry);
            plan.Remove.Add(GameConstants.PatchMarkerEntry);
            plan.RemoveWhere.Add(n => n.StartsWith(GameConstants.LibDir + GameConstants.ModLibPrefix, StringComparison.Ordinal));
            foreach (var lib in patched.Marker?.InjectedLibraries ?? new())
                plan.Remove.Add(GameConstants.LibDir + lib);

            var unsigned = Path.Combine(ws.Patched, "unpatched-unsigned.apk");
            ApkRewriter.Rewrite(zip, unsigned, plan);
            var signed = Path.Combine(ws.Output, "unpatched.apk");
            _signer.AlignAndSign(unsigned, Path.Combine(ws.Patched, "unpatched-aligned.apk"), signed);

            var check = ApkInspector.Analyze(signed, InspectOptions());
            if (check.HasLoader || check.LibMainNeedsLoader || check.Marker != null || check.EmbeddedMods.Count > 0)
                return PatchResult.Fail("Unpatched APK still contains ModKit files.", ws.Root);
            if (!Hashing.Equal(check.Hash(GameConstants.LibMainEntry), verification.Analysis.Hash(GameConstants.LibMainEntry)))
                return PatchResult.Fail("Unpatched libmain.so does not match the original.", ws.Root);

            var dir = _builds.Dir(check.Manifest.VersionName!, check.Manifest.VersionCode!.Value);
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, "unpatched.apk");
            File.Copy(signed, dest, true);
            _log.Success("Unpatched APK: " + dest);
            return new PatchResult(true, "Unpatched APK ready (signed with the NightClubModKit key).", dest,
                _builds.SplitsFor(check.Manifest.VersionName!, check.Manifest.VersionCode!.Value, false), ws.Root);
        }
        catch (Exception ex)
        {
            _log.Error(ex.ToString());
            return PatchResult.Fail("Unpatch failed: " + ex.Message, ws.Root);
        }
    }

    private ApkInspectorOptions InspectOptions() => new()
    {
        ModKitSignerSha256 = _keys.TryLoad()?.CertSha256,
    };
}
