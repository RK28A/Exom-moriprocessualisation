using NightClubPatcher.Core.Adb;
using NightClubPatcher.Core.Apk;
using NightClubPatcher.Core.Backup;
using NightClubPatcher.Core.Builds;
using NightClubPatcher.Core.Diagnostics;
using NightClubPatcher.Core.Infrastructure;
using NightClubPatcher.Core.Mods;
using NightClubPatcher.Core.Patching;
using NightClubPatcher.Core.Recovery;
using NightClubPatcher.Core.Signing;
using NightClubPatcher.Core.Versions;

namespace NightClubPatcher.Core;

public sealed class KitStatus
{
    public DeviceState Device { get; set; } = DeviceState.NoAdb;
    public string? DeviceSerial { get; set; }
    public string? DeviceModel { get; set; }
    public List<AdbDevice> Devices { get; set; } = new();
    public QuestModelInfo? Headset { get; set; }
    public DeviceCompatibility? HeadsetCompatibility { get; set; }
    public string? AndroidVersion { get; set; }
    public InstalledGameState? Installed { get; set; }
    public OriginalBackup? Backup { get; set; }
    public ApkAnalysis? BackupAnalysis { get; set; }
    public CompatibilityResult? Compatibility { get; set; }
    public string? LoaderPath { get; set; }
    public string? PatchedCurrent { get; set; }
    public string? PatchedPrevious { get; set; }
    public bool? SafeMode { get; set; }
    public AdbHealth? AdbHealth { get; set; }
    public IReadOnlyList<string> MissingTools { get; set; } = Array.Empty<string>();
    public List<string> Warnings { get; } = new();

    public string DeviceText => Device switch
    {
        DeviceState.Connected => $"Connected - {Headset?.DisplayName ?? DeviceModel ?? "?"} ({DeviceSerial})",
        DeviceState.MultipleDevices => $"{Devices.Count(d => d.Ready)} headsets connected - choose one",
        DeviceState.Unauthorized => "Unauthorized - accept the USB debugging prompt in the headset",
        DeviceState.Offline => "Offline",
        DeviceState.NoDevice => "Not connected",
        DeviceState.NoAdb => "adb not found",
        _ => Device.ToString(),
    };
}

public enum DevicePurpose { PatchOnly, PatchAndInstall, Operation }

/// <summary>
/// High-level workflows shared by the CLI and the GUI. All operations are synchronous and
/// should be called from a background thread by UIs.
/// </summary>
public sealed class ModKitService
{
    public KitPaths Paths { get; }
    public KitLog Log { get; }
    public ToolLocator Tools { get; }
    public AdbClient Adb { get; }
    public KeystoreManager Keys { get; }
    public ApkSigningService Signer { get; }
    public BackupManager Backups { get; }
    public ProfileStore Profiles { get; }
    public BuildStore Builds { get; }
    public PatchEngine Patcher { get; }
    public UserDataService UserData { get; }
    public InstallService Installer { get; }

    /// <summary>What a signature-mismatch reinstall does with the game data (CLI --data, GUI "Game Data Mode").</summary>
    public GameDataMode DataMode
    {
        get => Installer.DataMode;
        set => Installer.DataMode = value;
    }
    public RestoreService Restorer { get; }
    public LogcatService Logcat { get; }
    public ToolSetup ToolSetup { get; }
    public NativeBuilder NativeBuilder { get; }

    private readonly Dictionary<string, (DateTime mtime, long size, ApkAnalysis analysis)> _analysisCache = new();

    public ModKitService(KitPaths? paths = null, KitLog? log = null)
    {
        Paths = paths ?? KitPaths.Discover();
        Paths.EnsureCreated();
        Log = log ?? new KitLog(Paths.Logs);
        Tools = new ToolLocator(Paths);
        Adb = new AdbClient(Tools.FindAdb(), Log);
        Keys = new KeystoreManager(Paths, Tools, Log);
        Signer = new ApkSigningService(Tools, Keys, Paths, Log);
        Backups = new BackupManager(Paths, Log);
        Profiles = new ProfileStore(Paths);
        Builds = new BuildStore(Paths, Log);
        Patcher = new PatchEngine(Paths, Log, Backups, Profiles, Builds, Signer, Keys);
        UserData = new UserDataService(Paths, Log);
        Installer = new InstallService(Log, UserData);
        Restorer = new RestoreService(Log, Backups, Installer);
        Logcat = new LogcatService(Log);
        ToolSetup = new ToolSetup(Paths, Tools, Log);
        NativeBuilder = new NativeBuilder(Paths, Tools, Log);
    }

    private string? ModKitJavaHash => Keys.TryLoad()?.CertJavaHash;
    private string? ModKitSha => Keys.TryLoad()?.CertSha256;

    public ApkInspectorOptions InspectOptions() => new() { ModKitSignerSha256 = ModKitSha };

    public ApkAnalysis Analyze(string apk)
    {
        var fi = new FileInfo(apk);
        var key = fi.FullName;
        lock (_analysisCache)
        {
            if (_analysisCache.TryGetValue(key, out var c) && c.mtime == fi.LastWriteTimeUtc && c.size == fi.Length)
                return c.analysis;
        }
        var a = ApkInspector.Analyze(apk, InspectOptions());
        lock (_analysisCache) _analysisCache[key] = (fi.LastWriteTimeUtc, fi.Length, a);
        return a;
    }

    // ------------------------------------------------------------------ tools

    /// <summary>Re-locates tools (after ToolSetup installed them).</summary>
    public void ReloadTools()
    {
        Adb.AdbPath = Tools.FindAdb();
        Log.Info("adb: " + (Adb.AdbPath ?? "not found"));
    }

    /// <summary>Full tool status (runs each tool to read its version) for the setup panel.</summary>
    public IReadOnlyList<ToolState> CheckTools() => ToolSetup.CheckAll();

    /// <summary>
    /// Install / Repair Tools: downloads every missing or broken tool into Tools/ after one consent
    /// prompt (unless <paramref name="acceptLicenses"/>). Working tools are kept and never re-downloaded.
    /// </summary>
    public async Task<ToolSetupResult> InstallToolsAsync(IUserInteraction ui, bool includeNative = true, bool acceptLicenses = false,
        CancellationToken ct = default)
    {
        Log.Step("Checking tools");
        var states = ToolSetup.CheckAll(includeNative ? ToolSetup.AllTools : ToolSetup.AllTools.Except(ToolSetup.NativeTools));
        foreach (var st in states) Log.Info($"{st.Name}: {st.StatusText}" + (st.Location != null ? $"  [{st.Location}]" : ""));
        var todo = states.Where(st => !st.Ready).Select(st => st.Id).ToList();
        if (todo.Count == 0)
        {
            Log.Success("All tools are ready.");
            return new ToolSetupResult(true, Array.Empty<string>(), Array.Empty<string>());
        }
        if (!acceptLicenses && !ui.Confirm("Install / Repair Tools", ToolSetup.ConsentText(todo), "Download and install"))
            return new ToolSetupResult(false, Array.Empty<string>(), new[] { "Cancelled by the user." });
        var r = await ToolSetup.InstallAsync(todo, ct);
        ReloadTools();
        return r;
    }

    /// <summary>Backwards-compatible helper: true when nothing is missing afterwards.</summary>
    public async Task<bool> EnsureToolsAsync(IUserInteraction ui, CancellationToken ct = default)
    {
        await InstallToolsAsync(ui, includeNative: true, acceptLicenses: false, ct);
        return ToolSetup.Missing().Count == 0;
    }

    /// <summary>BUILD NATIVE LOADER with the bundled NDK / CMake / Ninja.</summary>
    public NativeBuildResult BuildNativeLoader(CancellationToken ct = default) => NativeBuilder.Build(ct);

    // ------------------------------------------------------------------ detection

    public bool ConnectDevice(out string message)
    {
        var state = Adb.DetectDevice(out var dev);
        message = state switch
        {
            DeviceState.NoAdb => "adb was not found. Put adb.exe in Tools/adb/ or install Android platform-tools.",
            DeviceState.NoDevice => "No Quest connected. Connect it by USB and enable developer mode.",
            DeviceState.Unauthorized => "Quest is unauthorized. Put on the headset and accept 'Allow USB debugging'.",
            DeviceState.Offline => "Quest is offline. Reconnect the cable or restart adb.",
            DeviceState.MultipleDevices => "Several devices are connected. Select one (GUI device list, --device <serial>).",
            _ => $"Quest connected: {dev?.Headset.DisplayName} ({dev?.Serial})",
        };
        return state == DeviceState.Connected;
    }

    /// <summary>Like ConnectDevice, but asks the user when several devices are connected.</summary>
    public bool ConnectDevice(IUserInteraction ui, out string message)
    {
        if (ConnectDevice(out message)) return true;
        if (Adb.Available && Adb.DetectDevice(out _) == DeviceState.MultipleDevices && SelectDevice(ui, DevicePurpose.Operation))
            return ConnectDevice(out message);
        return false;
    }

    /// <summary>Set when the device was given explicitly (CLI --device): no list prompt, checks still run.</summary>
    public bool DeviceLockedByUser { get; set; }

    private bool AutoSelectDevice(IUserInteraction ui, DevicePurpose purpose)
    {
        var state = Adb.Available ? Adb.DetectDevice(out _) : DeviceState.NoAdb;
        if (state != DeviceState.Connected)
        {
            if (purpose == DevicePurpose.PatchOnly && Backups.Latest() != null)
            {
                Log.Info("No headset selected: patch will be built from the existing backup only.");
                return true;
            }
            Log.Error(state == DeviceState.MultipleDevices
                ? "Several devices are connected: pass --device <serial> (see --list-devices)."
                : "No usable headset: " + state);
            return false;
        }
        var (headset, compat) = CheckSelectedHeadset();
        Log.Info($"Using device: {headset.DisplayName} ({Adb.Serial})");
        foreach (var w in compat.Warnings) Log.Warn(w);
        if (!compat.Supported) { Log.Error($"{headset.DisplayName} is not supported."); return false; }
        if (compat.Warnings.Count > 0 && ui is not NonInteractive && purpose != DevicePurpose.Operation &&
            !ui.Confirm("Headset compatibility", string.Join(Environment.NewLine, compat.Warnings) + Environment.NewLine + "Continue?", "Continue"))
            return false;
        return true;
    }

    public List<AdbDevice> ListDevices() => Adb.Available ? Adb.Devices() : new List<AdbDevice>();

    /// <summary>Selects a device programmatically (GUI list, CLI --device).</summary>
    public bool UseDevice(string? serial)
    {
        Adb.PreferredSerial = string.IsNullOrWhiteSpace(serial) ? null : serial;
        if (serial == null) return true;
        var found = ListDevices().Any(d => d.Serial == serial);
        if (!found) Log.Warn($"Device {serial} is not connected.");
        return found;
    }

    /// <summary>Identifies the selected headset and checks it against the game manifest.</summary>
    public (QuestModelInfo headset, DeviceCompatibility compat) CheckSelectedHeadset()
    {
        var headset = Adb.IdentifyHeadset();
        var manifest = SafeBackupAnalysis()?.Manifest.SupportedDevices;
        return (headset, QuestDevices.Check(headset, manifest));
    }

    private ApkAnalysis? SafeBackupAnalysis()
    {
        var b = Backups.Latest();
        if (b == null) return null;
        try { return Analyze(b.ApkPath); }
        catch { return null; }
    }

    /// <summary>
    /// Device choice shown before patching (and whenever several headsets are connected).
    /// Lists every adb device with its headset model and compatibility, then checks the choice.
    /// Returns false when the user cancels or the device cannot be used.
    /// </summary>
    public bool SelectDevice(IUserInteraction ui, DevicePurpose purpose)
    {
        if (purpose != DevicePurpose.Operation) Log.Step("Detecting Quest");
        if (ui is NonInteractive || (DeviceLockedByUser && Adb.PreferredSerial != null))
            return AutoSelectDevice(ui, purpose);
        if (!Adb.Available)
        {
            if (purpose == DevicePurpose.PatchOnly && Backups.Latest() != null) return true;
            ui.Notify("No adb", "adb was not found. Put adb.exe in Tools/adb/ or install Android platform-tools.");
            return false;
        }

        var manifest = SafeBackupAnalysis()?.Manifest.SupportedDevices;
        var devices = Adb.Devices();
        var options = new List<string> { "Cancel" };
        foreach (var d in devices)
        {
            var c = QuestDevices.Check(d.Headset, manifest);
            options.Add(d.Label + (d.Ready ? $"  -  {c.Summary}" : ""));
        }
        var offlineIndex = -1;
        if (purpose == DevicePurpose.PatchOnly && Backups.Latest() != null)
        {
            offlineIndex = options.Count;
            options.Add("No headset - only build the patched APK from the existing backup");
        }

        if (devices.Count == 0 && offlineIndex < 0)
        {
            ui.Notify("No headset", "No Meta Quest detected. Connect it by USB (developer mode on) and try again.");
            return false;
        }

        var title = purpose == DevicePurpose.Operation ? "Choose a headset" : "Choose the headset to patch for";
        var message = purpose == DevicePurpose.Operation
            ? "Several devices are connected. Which one should be used?"
            : "Select the Meta Quest that holds NightClub Simulator." + Environment.NewLine +
              "The original APK is backed up from it and the patched game is installed on it." + Environment.NewLine +
              "Supported: Meta Quest 2, Quest Pro, Quest 3, Quest 3S (same arm64 build for all).";
        var choice = ui.Choose(title, message, options);
        if (choice <= 0) { Log.Info("Device selection cancelled."); return false; }
        if (choice == offlineIndex)
        {
            Adb.PreferredSerial = null;
            Log.Info("No headset selected: patch will be built from the existing backup only.");
            return true;
        }

        var dev = devices[choice - 1];
        if (!dev.Ready)
        {
            ui.Notify("Device not ready", dev.State == "unauthorized"
                ? "Put on the headset and accept 'Allow USB debugging', then try again."
                : $"Device {dev.Serial} is {dev.State}. Reconnect the cable or restart adb.");
            return false;
        }

        Adb.PreferredSerial = dev.Serial;
        if (!ConnectDevice(out var msg)) { ui.Notify("Device", msg); return false; }

        var (headset, compat) = CheckSelectedHeadset();
        var android = $"Android {Adb.GetProp("ro.build.version.release")} (SDK {Adb.GetProp("ro.build.version.sdk")}), {Adb.GetProp("ro.build.display.id")}";
        Log.Info($"Selected device: {headset.DisplayName} [{Adb.GetProp("ro.product.device")}] {dev.Serial} - {android}");
        foreach (var w in compat.Warnings) Log.Warn(w);

        if (!compat.Supported)
        {
            ui.Notify("Unsupported device", $"{headset.DisplayName} cannot run NightClub Simulator." +
                                            (headset.Note != null ? Environment.NewLine + headset.Note : ""));
            Adb.PreferredSerial = null;
            return false;
        }
        if (compat.Warnings.Count > 0 && purpose != DevicePurpose.Operation &&
            !ui.Confirm("Headset compatibility", string.Join(Environment.NewLine + Environment.NewLine, compat.Warnings) +
                                                  Environment.NewLine + Environment.NewLine + "Continue with this headset?", "Continue"))
        {
            return false;
        }
        return true;
    }

    public KitStatus Detect(bool analyzeBackup = true)
    {
        var s = new KitStatus();
        s.AdbHealth = Adb.CheckHealth();
        s.MissingTools = ToolSetup.Missing();
        AdbDevice? dev = null;
        s.Device = s.AdbHealth.Ok ? Adb.DetectDevice(out dev) : DeviceState.NoAdb;
        s.DeviceSerial = dev?.Serial;
        s.LoaderPath = Paths.FindLoader();
        if (Adb.Available) s.Devices = Adb.Devices();

        if (s.Device == DeviceState.Connected)
        {
            s.DeviceModel = Adb.GetProp("ro.product.model");
            s.Headset = Adb.IdentifyHeadset();
            s.AndroidVersion = $"Android {Adb.GetProp("ro.build.version.release")} (SDK {Adb.GetProp("ro.build.version.sdk")})";
            OriginalBackup? hint = Backups.Latest();
            s.Installed = DeviceGameState.Read(Adb, hint?.Metadata?.SignerJavaHash, ModKitJavaHash);
            s.SafeMode = Adb.RemoteExists(GameConstants.DeviceSafeModeFlag);
        }

        if (s.Installed?.Installed == true)
            s.Backup = Backups.Find(s.Installed.VersionName, s.Installed.VersionCode);
        s.Backup ??= Backups.Latest();

        if (s.Backup != null && analyzeBackup)
        {
            try
            {
                s.BackupAnalysis = Analyze(s.Backup.ApkPath);
                s.Compatibility = Profiles.Evaluate(s.BackupAnalysis);
                if (s.Headset != null)
                {
                    s.HeadsetCompatibility = QuestDevices.Check(s.Headset, s.BackupAnalysis.Manifest.SupportedDevices);
                    s.Warnings.AddRange(s.HeadsetCompatibility.Warnings);
                }
            }
            catch (Exception ex)
            {
                s.Warnings.Add("Backup could not be analyzed: " + ex.Message);
            }
        }

        var vn = s.BackupAnalysis?.Manifest.VersionName ?? s.Installed?.VersionName;
        var vc = s.BackupAnalysis?.Manifest.VersionCode ?? s.Installed?.VersionCode;
        if (vn != null && vc != null)
        {
            s.PatchedCurrent = Builds.CurrentApk(vn, vc.Value);
            s.PatchedPrevious = Builds.PreviousApk(vn, vc.Value);
        }

        if (s.Installed?.Installed == true && s.Backup?.Metadata != null && s.Installed.VersionCode != s.Backup.Metadata.VersionCode)
        {
            s.Warnings.Add("NightClub update detected." + Environment.NewLine +
                           $"Installed: {s.Installed.VersionName} ({s.Installed.VersionCode})" + Environment.NewLine +
                           $"Backed up: {s.Backup.Metadata.VersionName} ({s.Backup.Metadata.VersionCode})");
        }
        var profile = s.Installed?.Installed == true ? Profiles.Find(s.Installed.VersionName, s.Installed.VersionCode) : null;
        if (s.Installed?.Installed == true && profile == null && s.Warnings.Count == 0)
        {
            var supported = string.Join(", ", Profiles.All().Select(p => p.VersionName));
            s.Warnings.Add($"NightClub update detected. Installed: {s.Installed.VersionName}. Supported: {supported}. " +
                           "Automatic patch disabled until compatibility is verified.");
        }
        return s;
    }

    // ------------------------------------------------------------------ backup

    public BackupResult Backup()
    {
        if (!ConnectDevice(out var msg)) return new BackupResult(BackupOutcome.Failed, msg, null);
        var existing = Backups.Latest();
        var opts = InspectOptions();
        var result = Backups.CreateFromDevice(Adb, new ApkInspectorOptions
        {
            ModKitSignerSha256 = opts.ModKitSignerSha256,
            ExpectedOriginalSignerSha256 = existing?.Metadata?.SignerSha256,
        });
        Log.Info(result.Message);
        return result;
    }

    /// <summary>Returns the backup to patch from, creating it from the device on first run.</summary>
    public OriginalBackup? EnsureBackup(out string message)
    {
        if (Adb.Available && Adb.DetectDevice(out _) == DeviceState.Connected && Adb.IsPackageInstalled(GameConstants.PackageName))
        {
            var (vn, vc) = Adb.InstalledVersion(GameConstants.PackageName);
            var b = Backups.Find(vn, vc);
            if (b != null) { message = "Using existing original backup."; return b; }
            var r = Backup();
            message = r.Message;
            if (r.Backup != null && r.Outcome is BackupOutcome.Created or BackupOutcome.AlreadyExists) return r.Backup;
            return Backups.Find(vn, vc);
        }
        var latest = Backups.Latest();
        message = latest != null ? "Device unavailable; using latest backup on disk." : "No device and no original backup.";
        return latest;
    }

    public VersionProfile PinFingerprints() => PinFingerprints(Backups.Latest() ?? throw new InvalidOperationException("No original backup to pin."));

    /// <summary>Pins the fingerprints of a verified original backup (Versions/&lt;build&gt;.json).</summary>
    public VersionProfile PinFingerprints(OriginalBackup b)
    {
        var v = Backups.Verify(b, InspectOptions());
        if (!v.Ok || v.Analysis == null) throw new InvalidOperationException("Backup verification failed: " + string.Join("; ", v.Problems));
        var p = Profiles.Pin(v.Analysis);
        Log.Success($"Fingerprints pinned in Versions/{p.FileName}");
        return p;
    }

    // ------------------------------------------------------------------ patch / install

    /// <summary>PATCH GAME. With a UI, the device choice is shown first.</summary>
    /// <summary>Mods the next patch will embed (shown to the user before patching).</summary>
    public IReadOnlyList<(string Name, string Path)> PlannedEmbeddedMods(PatchOptions options) => Patcher.PlannedMods(options);

    public PatchResult Patch(PatchOptions options, IUserInteraction? ui = null)
    {
        if (ui != null && !SelectDevice(ui, DevicePurpose.PatchOnly))
            return PatchResult.Fail("Patch cancelled: no device selected.");
        return PatchSelected(options, ui);
    }

    private PatchResult PatchSelected(PatchOptions options, IUserInteraction? ui = null)
    {
        if (Adb.Available && Adb.DetectDevice(out var dev) == DeviceState.Connected)
            options.TargetDevice = $"{Adb.IdentifyHeadset().DisplayName} ({dev?.Serial})";
        var backup = EnsureBackup(out var msg);
        Log.Info(msg);
        if (backup == null) return PatchResult.Fail("No original backup available. Connect the Quest with the original game installed.");
        var r = Patcher.Patch(backup, options);
        if (r.Ok || !r.NeedsPin || ui == null) return r;

        // Not pinned yet: offer to pin the verified backup's fingerprints right here and carry on.
        var choice = ui.Choose("Build not verified yet",
            r.Message + Environment.NewLine + Environment.NewLine +
            "Pin fingerprints records the hashes of your verified original backup (Versions/<build>.json) so this exact " +
            "build is treated as known. Only do this for a game you installed from the store.",
            new[] { "Cancel", "Pin fingerprints and continue" });
        if (choice != 1) return r;
        try
        {
            PinFingerprints(backup);
        }
        catch (Exception ex)
        {
            return PatchResult.Fail("Could not pin fingerprints: " + ex.Message);
        }
        return Patcher.Patch(backup, options);
    }

    public InstallResult InstallCurrent(IUserInteraction ui, bool previous = false)
    {
        if (!ConnectDevice(ui, out var msg)) return new InstallResult(false, msg);
        var backup = Backups.Latest();
        var vn = backup?.Metadata?.VersionName;
        var vc = backup?.Metadata?.VersionCode;
        if (vn == null || vc == null) return new InstallResult(false, "No backup metadata; patch the game first.");
        var apk = previous ? Builds.PreviousApk(vn, vc.Value) : Builds.CurrentApk(vn, vc.Value);
        if (apk == null) return new InstallResult(false, previous ? "No previous patched build." : "No patched build. Run PATCH first.");
        return Installer.Install(Adb, apk, Builds.SplitsFor(vn, vc.Value, previous), ui,
            backup?.Metadata?.SignerJavaHash, ModKitJavaHash, previous ? "previous modded build" : "patched build");
    }

    public (PatchResult patch, InstallResult? install) PatchAndInstall(PatchOptions options, IUserInteraction ui, bool launch)
    {
        if (!SelectDevice(ui, DevicePurpose.PatchAndInstall))
            return (PatchResult.Fail("Patch + install cancelled: no headset selected."), null);
        if (!ConnectDevice(out var connectMsg)) return (PatchResult.Fail(connectMsg), null);
        var p = PatchSelected(options, ui);
        if (!p.Ok || p.OutputApk == null) return (p, null);
        if (!ConnectDevice(out var msg)) return (p, new InstallResult(false, msg));
        var backup = Backups.Latest();
        var i = Installer.Install(Adb, p.OutputApk, p.Splits, ui, backup?.Metadata?.SignerJavaHash, ModKitJavaHash, "patched build");
        if (i.Ok)
        {
            var push = PushModsAndConfig();
            if (!push.Ok) Log.Warn("Mods/config not pushed: " + push.Summary);
        }
        if (i.Ok && launch) Launch();
        return (p, i);
    }

    public (PatchResult unpatch, InstallResult? install) Unpatch(IUserInteraction ui, bool install)
    {
        var backup = Backups.Latest();
        if (backup?.Metadata == null) return (PatchResult.Fail("No original backup."), null);
        var current = Builds.CurrentApk(backup.Metadata.VersionName, backup.Metadata.VersionCode);
        string? source = current;

        // Prefer the APK that is actually installed if it is a ModKit build.
        if (ConnectDevice(ui, out _))
        {
            var paths = Adb.PackagePaths(GameConstants.PackageName);
            if (paths.Count > 0)
            {
                var pulled = Path.Combine(KitPaths.WorkspaceRoot, "installed-for-unpatch.apk");
                if (Adb.Pull(paths[0], pulled))
                {
                    var a = ApkInspector.Analyze(pulled, new ApkInspectorOptions { ComputeFileHashes = false });
                    if (a.IsPatchedByModKit) source = pulled;
                    else if (!a.IsPatchedByModKit && current == null)
                        return (PatchResult.Fail("The installed game is not patched by NCMK. Nothing to unpatch."), null);
                }
            }
        }
        if (source == null) return (PatchResult.Fail("No patched build found to unpatch."), null);

        var r = Patcher.Unpatch(source, backup);
        if (!r.Ok || !install || r.OutputApk == null) return (r, null);
        var i = Installer.Install(Adb, r.OutputApk, r.Splits, ui, backup.Metadata.SignerJavaHash, ModKitJavaHash, "unpatched build");
        return (r, i);
    }

    // ------------------------------------------------------------------ game data

    /// <summary>Lists every file of Android/data and Android/obb for the game (nothing is copied).</summary>
    public string ListGameData(IUserInteraction? ui = null)
    {
        var connected = ui != null ? ConnectDevice(ui, out var msg) : ConnectDevice(out msg);
        if (!connected) return msg;
        Log.Step("Listing game data on the headset");
        var text = UserData.DescribeOnDevice(Adb);
        foreach (var line in text.Split('\n')) if (line.Trim().Length > 0) Log.Info(line.TrimEnd());
        return text;
    }

    /// <summary>Backs up the game's accessible data now, without uninstalling anything.</summary>
    public UserDataBackup? BackupGameData(IUserInteraction? ui = null)
    {
        var connected = ui != null ? ConnectDevice(ui, out var msg) : ConnectDevice(out msg);
        if (!connected) { Log.Error(msg); return null; }
        return UserData.Backup(Adb, ui);
    }

    // ------------------------------------------------------------------ recovery

    public RestoreResult RestoreOriginal(IUserInteraction ui, ModFilesPolicy policy = ModFilesPolicy.Ask)
    {
        if (!ConnectDevice(ui, out var msg)) return new RestoreResult(false, msg, Array.Empty<string>());
        OriginalBackup? backup = null;
        if (Adb.IsPackageInstalled(GameConstants.PackageName))
        {
            var (vn, vc) = Adb.InstalledVersion(GameConstants.PackageName);
            backup = Backups.Find(vn, vc);
        }
        backup ??= Backups.Latest();
        if (backup == null) return new RestoreResult(false, "No original backup found in Backups/.", Array.Empty<string>());
        return Restorer.RestoreOriginal(Adb, backup, ui, ModKitJavaHash, policy);
    }

    public RestoreResult EmergencyRestore(IUserInteraction ui, string? apkPath = null)
    {
        if (!ConnectDevice(ui, out var msg)) return new RestoreResult(false, msg, Array.Empty<string>());
        return Restorer.Emergency(Adb, apkPath, ui);
    }

    // ------------------------------------------------------------------ device helpers

    public bool Launch()
    {
        if (!ConnectDevice(out var msg)) { Log.Error(msg); return false; }
        Log.Step("Launching " + GameConstants.PackageName);
        return Adb.Launch(GameConstants.PackageName);
    }

    /// <summary>
    /// Creates NCMKMods (Mods/Config/Logs) in the game's external files folder and pushes
    /// DeviceMods/Mods/*.so and DeviceMods/Config/* (config files are never overwritten unless asked).
    /// </summary>
    public PushReport PushModsAndConfig(bool overwriteConfig = false)
    {
        if (!ConnectDevice(out var msg)) { Log.Error(msg); return new PushReport(false, 0, 0, new[] { msg }); }
        Log.Step("Pushing mods and config to " + GameConstants.DeviceModsRoot);
        var r = new DeviceModPusher(Paths, Log).Push(Adb, overwriteConfig);
        if (r.Ok) Log.Success(r.Summary);
        else Log.Error(r.Summary);
        return r;
    }

    public bool SetSafeMode(bool enabled)
    {
        if (!ConnectDevice(out var msg)) { Log.Error(msg); return false; }
        if (enabled)
        {
            Adb.Shell($"mkdir -p \"{GameConstants.DeviceModsRoot}\" && touch \"{GameConstants.DeviceSafeModeFlag}\"");
            Log.Success("Safe Mode enabled: the loader will start but no third-party mods will load.");
        }
        else
        {
            Adb.Shell($"rm -f \"{GameConstants.DeviceSafeModeFlag}\"");
            Log.Success("Safe Mode disabled.");
        }
        return true;
    }

    // ------------------------------------------------------------------ diagnostics

    public DiagnosticReport Diagnose()
    {
        var r = new DiagnosticReport();
        foreach (var (tool, path) in Tools.Report()) r.Tools.Add($"{tool,-36} {path ?? "NOT FOUND"}");

        r.Add("ADB installed", Adb.Available ? CheckStatus.Pass : CheckStatus.Fail, Adb.AdbPath ?? "not found");
        if (Adb.Available) r.Add("ADB version", CheckStatus.Info, Adb.Version());

        var state = Adb.Available ? Adb.DetectDevice(out var dev) : DeviceState.NoAdb;
        if (Adb.Available)
            foreach (var d in Adb.Devices()) r.Add("adb device", CheckStatus.Info, d.Label);
        r.Add("Quest connected", state == DeviceState.Connected ? CheckStatus.Pass : CheckStatus.Fail, state.ToString());
        r.Add("Authorization", state switch
            {
                DeviceState.Unauthorized => CheckStatus.Fail,
                DeviceState.Connected => CheckStatus.Pass,
                _ => CheckStatus.Info,
            },
            state switch
            {
                DeviceState.Unauthorized => "Accept 'Allow USB debugging' in the headset",
                DeviceState.Connected => "authorized",
                _ => "n/a (no device)",
            });

        InstalledGameState? installed = null;
        var backup = Backups.Latest();
        if (state == DeviceState.Connected)
        {
            var headset = Adb.IdentifyHeadset();
            var headsetCompat = QuestDevices.Check(headset, SafeBackupAnalysis()?.Manifest.SupportedDevices);
            r.Add("Headset", headsetCompat.Supported ? (headsetCompat.Warnings.Count == 0 ? CheckStatus.Pass : CheckStatus.Warn) : CheckStatus.Fail,
                $"{headset.DisplayName} [{Adb.GetProp("ro.product.device")}] - {headsetCompat.Summary}");
            foreach (var w in headsetCompat.Warnings) r.Add("Headset note", CheckStatus.Warn, w);
            r.Add("Device build", CheckStatus.Info, $"Android {Adb.GetProp("ro.build.version.release")} (SDK {Adb.GetProp("ro.build.version.sdk")}) / {Adb.GetProp("ro.build.display.id")}");
            installed = DeviceGameState.Read(Adb, backup?.Metadata?.SignerJavaHash, ModKitJavaHash);
            r.Add("Package installed", installed.Installed ? CheckStatus.Pass : CheckStatus.Fail, GameConstants.PackageName);
            if (installed.Installed)
            {
                var profile = Profiles.Find(installed.VersionName, installed.VersionCode);
                r.Add("Package version", profile != null ? CheckStatus.Pass : CheckStatus.Warn,
                    $"{installed.VersionName} ({installed.VersionCode}){(profile == null ? " - no profile in Versions/" : "")}");
                r.Add("APK path", CheckStatus.Info, string.Join(", ", installed.ApkPaths));
                r.Add("Current signature", CheckStatus.Info, $"{installed.Describe()} [{string.Join(",", installed.SignatureHashes)}]");
                r.Add("Loader (extracted libs)", CheckStatus.Info, installed.LoaderFilePresent switch
                {
                    true => "libncmkloader.so present",
                    false => "not present",
                    _ => "unknown (native libs not extracted / not readable)",
                });
            }
            foreach (var (label, remote) in new[] { ("Game data (Android/data)", GameConstants.DeviceExternalData), ("Game data (Android/obb)", GameConstants.DeviceObbDir) })
            {
                var inv = UserData.Inventory(Adb, label, remote, Path.GetTempPath());
                r.Add(label, !inv.Exists || inv.InventoryComplete ? CheckStatus.Info : CheckStatus.Warn,
                    !inv.Exists ? "does not exist"
                    : $"{inv.Inventory.Count} files, {FolderBackupReport.FormatSize(inv.TotalBytes)}" +
                      (inv.InventoryComplete ? "" : " - listing incomplete: " + string.Join("; ", inv.InventoryErrors.Take(2))));
            }
            r.Add("Mod folder", CheckStatus.Info, Adb.RemoteExists(GameConstants.DeviceModsRoot) ? GameConstants.DeviceModsRoot : "absent");
            r.Add("Safe mode flag", CheckStatus.Info, Adb.RemoteExists(GameConstants.DeviceSafeModeFlag) ? "ON" : "off");
            r.LoaderLog.AddRange(Logcat.LoaderLog(Adb));
            foreach (var legacy in GameConstants.LegacyDeviceModsRoots.Where(Adb.RemoteExists))
                r.Add("Legacy mod folder", CheckStatus.Info, legacy + " (older kit; the NCMK loader uses " + GameConstants.DeviceModsRoot + ")");
            var logcat = Logcat.Dump(Adb);
            r.RelevantLogcat.AddRange(logcat.TakeLast(600));
            var entitlement = LogcatFilter.EntitlementFailures(logcat);
            r.Add("Meta entitlement", entitlement.Count > 0 ? CheckStatus.Warn : CheckStatus.Info,
                entitlement.Count > 0
                    ? $"the game's entitlement check appears to have FAILED ({entitlement.Count} log line(s), last: {entitlement[^1].Trim()}). " +
                      "The re-signed APK may be refused by Meta's entitlement check; NCMK does not bypass it. Restore the original game if it will not start."
                    : "no entitlement failure in logcat");
            r.CrashLog.AddRange(Logcat.CrashBuffer(Adb));
        }

        if (backup == null)
        {
            r.Add("Original backup", CheckStatus.Fail, "none - run BACKUP with the original game installed");
        }
        else
        {
            var v = Backups.Verify(backup, InspectOptions());
            r.Add("Original backup", v.Ok ? CheckStatus.Pass : CheckStatus.Fail, backup.ApkPath);
            r.Add("Original hash", v.Ok ? CheckStatus.Pass : CheckStatus.Fail, v.Ok ? v.Analysis?.ApkSha256 ?? "" : string.Join("; ", v.Problems));
            if (v.Analysis != null)
            {
                var a = v.Analysis;
                r.Add("Unity version", a.UnityVersion != null ? CheckStatus.Pass : CheckStatus.Warn, $"{a.UnityVersion ?? "unknown"} ({a.UnityVersionSource})");
                r.Add("Architecture", a.IsArm64 ? CheckStatus.Pass : CheckStatus.Fail, $"{a.LibIl2CppMachine} ABIs: {string.Join(",", a.Abis)}");
                r.Add("IL2CPP files", a.Il2Cpp.IsIl2Cpp ? CheckStatus.Pass : CheckStatus.Fail,
                    $"libil2cpp={a.Il2Cpp.HasLibIl2Cpp} metadata={a.Il2Cpp.HasMetadata} magic={a.Il2Cpp.MetadataMagicValid} v{a.Il2Cpp.MetadataVersion}");
                r.Add("XR runtime", CheckStatus.Info, a.XrRuntime ?? "not detected");
                r.Add("extractNativeLibs", CheckStatus.Info, a.Manifest.ExtractNativeLibs?.ToString() ?? "(default)");
                var compat = Profiles.Evaluate(a);
                r.Add("Compatibility", compat.AllowsAutomaticPatch ? CheckStatus.Pass : CheckStatus.Warn, compat.Summary);
            }
        }

        var vn = backup?.Metadata?.VersionName;
        var vc = backup?.Metadata?.VersionCode;
        var cur = vn != null && vc != null ? Builds.CurrentApk(vn, vc.Value) : null;
        var prev = vn != null && vc != null ? Builds.PreviousApk(vn, vc.Value) : null;
        r.Add("Patched APK (current)", cur != null ? CheckStatus.Pass : CheckStatus.Info, cur ?? "none");
        r.Add("Patched APK (previous)", CheckStatus.Info, prev ?? "none");
        var loader = Paths.FindLoader();
        r.Add("Loader build", loader != null ? CheckStatus.Pass : CheckStatus.Warn, loader ?? "libncmkloader.so not built (Native/build.ps1)");
        var ks = Keys.TryLoad();
        r.Add("Signing key", ks != null ? CheckStatus.Pass : CheckStatus.Info, ks != null ? $"{Paths.KeystorePath} sha256={ks.CertSha256}" : "not created yet (created on first patch)");
        return r;
    }

    public string ExportDiagnostics(DiagnosticReport report)
    {
        var path = Path.Combine(Paths.Logs, $"diagnostic-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(path, report.Render());
        File.Copy(path, Path.Combine(Paths.Logs, "diagnostic.txt"), true);
        Log.Success("Diagnostic exported: " + path);
        return path;
    }
}
