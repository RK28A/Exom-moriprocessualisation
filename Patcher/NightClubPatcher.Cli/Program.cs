using NightClubPatcher.Core;
using NightClubPatcher.Core.Apk;
using NightClubPatcher.Core.Elf;
using NightClubPatcher.Core.Infrastructure;
using NightClubPatcher.Core.Patching;
using NightClubPatcher.Core.Recovery;

namespace NightClubPatcher.Cli;

internal static class Program
{
    private const string Help = """
        NCMK - NightClub Simulator Mod Kit - target: com.mandegames.NightClubSimulater

        Advanced command-line interface. The main application is the GUI: NightClubPatcher.exe

        Usage: NightClubPatcher.Cli.exe <command> [options]

        Main commands
          --detect                     Detect Quest, game, version, backup and installation state
          --backup                     Pull the installed original APK into an immutable backup
          --patch                      Build patched-current.apk from the original backup (FAST PATCH)
          --install                    Install Builds/<ver>/patched-current.apk
          --patch-install              --patch then --install (add --launch to start the game)
          --restore                    RESTORE ORIGINAL (verified backup, safe signature handling)
          --emergency-restore [apk]    Restore using only an original.apk (no state database needed)
          --unpatch                    Remove ModKit changes, re-sign with ModKit key, install
          --diagnose                   Run all checks and export Logs/diagnostic.txt

        Other commands
          --install-previous           Reinstall patched-previous.apk (crash protection)
          --list-data                  Show every file of Android/data and Android/obb of the game (copies nothing)
          --backup-data                Back up the game data now (several methods, verified file by file)
          --launch                     Start the game
          --logcat                     Stream filtered logcat (NCMK/Unity/il2cpp/crashes)
          --push-mods                  Push DeviceMods/Mods/*.so and DeviceMods/Config/* to the headset's NCMKMods
                                       folder (add --overwrite-config to replace config files already there)
          --safe-mode on|off           Toggle NCMKMods/disable_mods (Android/data/<package>/files/NCMKMods)
          --pin-fingerprints           Record SHA-256 of libmain/libunity/libil2cpp/metadata in Versions/
          --analyze <apk>              Inspect any APK file (read-only)
          --import-backup <apk>        Use a local original APK as the backup
          --elf-needed <so>            List DT_NEEDED of a shared library
          --elf-add-needed <in.so> <lib> <out.so>   Developer: add a DT_NEEDED entry
          --tools                      Show located external tools
          --check-tools                Setup status: ADB, Build Tools, JDK 17, Android NDK, CMake, Ninja, Native Loader
          --install-tools              Install / repair every missing tool into Tools/ (portable, PATH untouched)
                                       add --accept-licenses to skip the prompt, --no-native to skip NDK/CMake/Ninja
          --build-native               Build libncmkloader.so with the bundled NDK/CMake/Ninja -> Native/out/arm64-v8a
          --bundled-only               Ignore Android SDK / JAVA_HOME / PATH tools (only use Tools/)
          --check-adb                  Check adb (binary, version, server) and list devices
          --restart-adb                Restart the adb server (fixes most "offline" devices)

        Devices (Meta Quest 2 / Quest Pro / Quest 3 / Quest 3S)
          --list-devices               List connected devices with headset model and compatibility
          --device <serial>            Use this device (skips the device prompt; see --list-devices)
                                       Without --device, --patch and --patch-install ask which headset to use.

        Options
          --allow-unverified           Allow patching builds whose fingerprints are not verified
          --no-mods                    Do not embed libncmkmod_*.so into the APK
          --loader <path>              Use a specific libncmkloader.so
          --mods keep|disable|delete   What to do with NCMKMods on restore (default: ask)
          --data keep|backup-only|skip|clean
                                       Game data on a signature-mismatch reinstall (default: keep)
                                         keep         back up, reinstall, restore
                                         backup-only  back up, reinstall, do not restore
                                         skip         no backup, no restore
                                         clean        back up for safety, reinstall, start with fresh data
          --no-install                 (with --unpatch) only build the APK
          --non-interactive            Never prompt; destructive choices are cancelled
        """;

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h") || args.Contains("/?"))
        {
            Console.WriteLine(Help);
            return 0;
        }

        var svc = new ModKitService();
        var progressShown = false;
        svc.Log.Progress += (_, text, _) =>
        {
            if (Console.IsOutputRedirected) return;
            if (text == null)
            {
                if (progressShown) Console.Write("\r" + new string(' ', Math.Max(0, Console.WindowWidth - 1)) + "\r");
                progressShown = false;
                return;
            }
            var w = Math.Max(20, Console.WindowWidth - 1);
            Console.Write("\r" + (text.Length > w ? text[..w] : text.PadRight(w)));
            progressShown = true;
        };
        svc.Log.Message += (level, msg) =>
        {
            if (level == LogLevel.Debug && !args.Contains("--verbose")) return;
            Console.ForegroundColor = level switch
            {
                LogLevel.Error => ConsoleColor.Red,
                LogLevel.Warn => ConsoleColor.Yellow,
                LogLevel.Success => ConsoleColor.Green,
                LogLevel.Debug => ConsoleColor.DarkGray,
                LogLevel.Step => ConsoleColor.Cyan,
                _ => ConsoleColor.Gray,
            };
            Console.WriteLine(level == LogLevel.Step ? "==> " + msg : msg);
            Console.ResetColor();
        };
        IUserInteraction ui = args.Contains("--non-interactive") ? new NonInteractive(svc.Log) : new ConsoleInteraction();

        if (args.Contains("--bundled-only")) { svc.Tools.BundledOnly = true; svc.ReloadTools(); }

        if (args.Contains("--data"))
        {
            if (!GameDataModes.TryParse(Value(args, "--data"), out var mode))
            {
                Console.WriteLine("Usage: --data keep|backup-only|skip|clean");
                return 1;
            }
            svc.DataMode = mode;
            svc.Log.Info("Game data mode: " + mode.CliName());
        }

        var serial = Value(args, "--device");
        if (args.Contains("--device"))
        {
            if (serial == null) { Console.WriteLine("Usage: --device <serial>  (see --list-devices)"); return 1; }
            if (!svc.UseDevice(serial)) return 2;
            svc.DeviceLockedByUser = true;
        }

        try
        {
            return Run(svc, ui, args);
        }
        catch (Exception ex)
        {
            svc.Log.Error(ex.Message);
            svc.Log.Debug(ex.ToString());
            return 1;
        }
    }

    private static string? Value(string[] args, string flag)
    {
        var i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null;
    }

    private static PatchOptions Options(string[] args) => new()
    {
        AllowUnverified = args.Contains("--allow-unverified"),
        EmbedMods = !args.Contains("--no-mods"),
        LoaderPath = Value(args, "--loader"),
    };

    private static int Run(ModKitService svc, IUserInteraction ui, string[] args)
    {
        var cmd = args[0];
        switch (cmd)
        {
            case "--detect":
                return Detect(svc);

            case "--backup":
            {
                var r = svc.Backup();
                return r.Outcome is Core.Backup.BackupOutcome.Created or Core.Backup.BackupOutcome.AlreadyExists ? 0 : 2;
            }

            case "--install-tools":
            {
                var r = svc.InstallToolsAsync(ui, includeNative: !args.Contains("--no-native"),
                    acceptLicenses: args.Contains("--accept-licenses")).GetAwaiter().GetResult();
                PrintToolStatus(svc);
                return r.Ok ? 0 : 2;
            }

            case "--check-tools":
                return PrintToolStatus(svc) ? 0 : 2;

            case "--build-native":
            {
                var r = svc.BuildNativeLoader();
                Report(svc, r.Ok, r.Message);
                return r.Ok ? 0 : 2;
            }

            case "--check-adb":
            {
                var h = svc.Adb.CheckHealth();
                Console.WriteLine($"adb binary   : {(h.Found ? h.Path : "NOT FOUND (run --install-tools)")}");
                Console.WriteLine($"adb version  : {h.Version ?? "-"}");
                Console.WriteLine($"adb server   : {(h.ServerRunning ? "running" : "NOT running")}");
                if (h.Error != null) Console.WriteLine($"problem      : {h.Error}");
                if (!h.Ok) return 2;
                Console.WriteLine();
                return ListDevices(svc);
            }

            case "--restart-adb":
                return svc.Adb.RestartServer() ? 0 : 2;

            case "--list-data":
                Console.WriteLine(svc.ListGameData(ui));
                return 0;

            case "--backup-data":
            {
                var b = svc.BackupGameData(ui);
                if (b == null) return 2;
                Console.WriteLine();
                Console.WriteLine(b.Describe());
                return b.AccessibleDataComplete ? 0 : 3;
            }

            case "--list-devices":
                return ListDevices(svc);

            case "--patch":
            {
                var r = svc.Patch(Options(args), ui);
                Report(svc, r.Ok, r.Message + (r.OutputApk != null ? Environment.NewLine + r.OutputApk : ""));
                return r.Ok ? 0 : 2;
            }

            case "--install":
            case "--install-previous":
            {
                var r = svc.InstallCurrent(ui, previous: cmd == "--install-previous");
                Report(svc, r.Ok, r.Message);
                return r.Ok ? 0 : 2;
            }

            case "--patch-install":
            {
                var (p, i) = svc.PatchAndInstall(Options(args), ui, args.Contains("--launch"));
                Report(svc, p.Ok, p.Message);
                if (i != null) Report(svc, i.Ok, i.Message);
                if (i?.Ok == true)
                    svc.Log.Info("Check the loader with: NightClubPatcher.Cli.exe --logcat   (expect '[NCMK] Hello from NCMK')");
                return p.Ok && i?.Ok == true ? 0 : 2;
            }

            case "--restore":
            {
                var policy = Value(args, "--mods") switch
                {
                    "keep" => ModFilesPolicy.Keep,
                    "disable" => ModFilesPolicy.Disable,
                    "delete" => ModFilesPolicy.Delete,
                    _ => args.Contains("--non-interactive") ? ModFilesPolicy.Disable : ModFilesPolicy.Ask,
                };
                var r = svc.RestoreOriginal(ui, policy);
                Report(svc, r.Ok, r.Message);
                return r.Ok ? 0 : 2;
            }

            case "--emergency-restore":
            {
                var r = svc.EmergencyRestore(ui, Value(args, cmd));
                Report(svc, r.Ok, r.Message);
                return r.Ok ? 0 : 2;
            }

            case "--unpatch":
            {
                var (u, i) = svc.Unpatch(ui, install: !args.Contains("--no-install"));
                Report(svc, u.Ok, u.Message);
                if (i != null) Report(svc, i.Ok, i.Message);
                return u.Ok && (i == null || i.Ok) ? 0 : 2;
            }

            case "--diagnose":
            {
                var report = svc.Diagnose();
                Console.WriteLine(report.Render());
                svc.ExportDiagnostics(report);
                return 0;
            }

            case "--launch":
                return svc.Launch() ? 0 : 2;

            case "--logcat":
            {
                if (!svc.ConnectDevice(out var msg)) { svc.Log.Error(msg); return 2; }
                using var p = svc.Logcat.Stream(svc.Adb, Console.WriteLine, clearFirst: args.Contains("--clear"));
                Console.WriteLine("Press Ctrl+C to stop.");
                p.WaitForExit();
                return 0;
            }

            case "--push-mods":
                return svc.PushModsAndConfig(args.Contains("--overwrite-config")).Ok ? 0 : 2;

            case "--safe-mode":
            {
                var v = Value(args, cmd);
                if (v is not ("on" or "off")) { Console.WriteLine("Usage: --safe-mode on|off"); return 1; }
                return svc.SetSafeMode(v == "on") ? 0 : 2;
            }

            case "--pin-fingerprints":
            {
                var p = svc.PinFingerprints();
                Console.WriteLine(Json.Serialize(p));
                return 0;
            }

            case "--analyze":
            {
                var path = Value(args, cmd) ?? throw new ArgumentException("--analyze <apk>");
                PrintAnalysis(svc, svc.Analyze(path));
                return 0;
            }

            case "--import-backup":
            {
                var path = Value(args, cmd) ?? throw new ArgumentException("--import-backup <apk>");
                var r = svc.Backups.ImportFromFile(path, svc.InspectOptions());
                Report(svc, r.Outcome == Core.Backup.BackupOutcome.Created, r.Message);
                return r.Outcome == Core.Backup.BackupOutcome.Created ? 0 : 2;
            }

            case "--elf-needed":
            {
                var path = Value(args, cmd) ?? throw new ArgumentException("--elf-needed <so>");
                var elf = ElfFile.Load(path);
                Console.WriteLine($"{path}: {elf.MachineName}, SONAME={elf.GetSoName()}");
                foreach (var n in elf.GetNeeded()) Console.WriteLine("  NEEDED " + n);
                return 0;
            }

            case "--elf-add-needed":
            {
                if (args.Length < 4) throw new ArgumentException("--elf-add-needed <in.so> <lib> <out.so>");
                var r = ElfNeededPatcher.AddNeeded(File.ReadAllBytes(args[1]), args[2]);
                File.WriteAllBytes(args[3], r.Data);
                Console.WriteLine(r.Message);
                return 0;
            }

            case "--tools":
                foreach (var (tool, path) in svc.Tools.Report()) Console.WriteLine($"{tool,-36} {path ?? "NOT FOUND"}");
                Console.WriteLine($"{"libncmkloader.so",-36} {svc.Paths.FindLoader() ?? "NOT BUILT"}");
                Console.WriteLine($"{"Kit root",-36} {svc.Paths.Root}");
                return 0;

            default:
                Console.WriteLine("Unknown command: " + cmd);
                Console.WriteLine(Help);
                return 1;
        }
    }

    /// <summary>Setup status table; returns true when everything (including the loader) is ready.</summary>
    private static bool PrintToolStatus(ModKitService svc)
    {
        Console.WriteLine();
        var all = true;
        foreach (var t in svc.CheckTools())
        {
            all &= t.Ready;
            Console.ForegroundColor = t.Ready ? ConsoleColor.Green : ConsoleColor.Yellow;
            Console.WriteLine($"{t.Name,-15} {t.StatusText}");
            Console.ResetColor();
            if (t.Location != null) Console.WriteLine($"{"",-15} {t.Location}");
        }
        var loader = svc.NativeBuilder.LoaderReady(out var path);
        all &= loader;
        Console.ForegroundColor = loader ? ConsoleColor.Green : ConsoleColor.Yellow;
        Console.WriteLine($"{"Native Loader",-15} {(loader ? "Ready" : "Not built (run --build-native)")}");
        Console.ResetColor();
        if (path != null) Console.WriteLine($"{"",-15} {path}");
        Console.WriteLine($"{"tools.json",-15} {svc.ToolSetup.ManifestPath}");
        return all;
    }

    private static int ListDevices(ModKitService svc)
    {
        var devices = svc.ListDevices();
        if (devices.Count == 0)
        {
            Console.WriteLine(svc.Adb.Available ? "No device connected." : "adb not found.");
            return 2;
        }
        string? manifest = null;
        var b = svc.Backups.Latest();
        if (b != null) { try { manifest = svc.Analyze(b.ApkPath).Manifest.SupportedDevices; } catch { /* ignore */ } }
        foreach (var d in devices)
        {
            var c = Core.Adb.QuestDevices.Check(d.Headset, manifest);
            Console.WriteLine($"{d.Serial,-28} {d.State,-13} {d.Headset.DisplayName,-24} {(d.Ready ? c.Summary : "")}");
            foreach (var w in c.Warnings) Console.WriteLine("    ! " + w);
        }
        return 0;
    }

    private static void Report(ModKitService svc, bool ok, string message)
    {
        if (ok) svc.Log.Success(message);
        else svc.Log.Error(message);
    }

    private static int Detect(ModKitService svc)
    {
        var s = svc.Detect();
        Console.WriteLine();
        Console.WriteLine("      NCMK - NightClub Simulator Mod Kit");
        Console.WriteLine(new string('-', 50));
        Console.WriteLine($"Quest:                 {s.DeviceText}");
        if (s.Headset != null)
            Console.WriteLine($"Headset:               {s.Headset.DisplayName} - {s.HeadsetCompatibility?.Summary ?? (s.Headset.Supported ? "supported" : "not supported")}, {s.AndroidVersion}");
        if (s.Devices.Count > 1)
            foreach (var d in s.Devices) Console.WriteLine($"  device:              {d.Label}");
        Console.WriteLine($"Game:                  {(s.Installed?.Installed == true ? "Detected" : "Not detected")}");
        Console.WriteLine($"Package:               {GameConstants.PackageName}");
        var a = s.BackupAnalysis;
        Console.WriteLine($"Version:               {s.Installed?.VersionName ?? a?.Manifest.VersionName ?? "?"}");
        Console.WriteLine($"Version Code:          {s.Installed?.VersionCode ?? a?.Manifest.VersionCode}");
        Console.WriteLine($"Unity:                 {a?.UnityVersion ?? "? (needs backup)"}");
        Console.WriteLine($"Architecture:          {(a == null ? "?" : a.IsArm64 ? "ARM64" : "NOT ARM64")}");
        Console.WriteLine($"Backend:               {(a == null ? "?" : a.Il2Cpp.IsIl2Cpp ? "IL2CPP" : "not IL2CPP")}");
        Console.WriteLine($"XR:                    {a?.XrRuntime ?? "?"}");
        Console.WriteLine($"Original Backup:       {(s.Backup != null ? "✓ " + s.Backup.ApkPath : "✗ none")}");
        Console.WriteLine($"Current Installation:  {s.Installed?.Describe() ?? "?"}");
        Console.WriteLine($"Loader:                {(s.Installed?.Kind == InstallationKind.ModdedByModKit ? "Installed" : "Not Installed")}");
        Console.WriteLine($"Loader build:          {s.LoaderPath ?? "not built"}");
        Console.WriteLine($"Patched current:       {s.PatchedCurrent ?? "none"}");
        Console.WriteLine($"Patched previous:      {s.PatchedPrevious ?? "none"}");
        Console.WriteLine($"Safe mode:             {(s.SafeMode == true ? "ON" : s.SafeMode == false ? "off" : "?")}");
        if (s.Compatibility != null) Console.WriteLine($"Compatibility:         {s.Compatibility.Summary}");
        foreach (var w in s.Warnings)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("WARNING: " + w);
            Console.ResetColor();
        }
        return 0;
    }

    private static void PrintAnalysis(ModKitService svc, ApkAnalysis a)
    {
        Console.WriteLine($"File:              {a.Path} ({a.SizeBytes / 1024 / 1024} MB)");
        Console.WriteLine($"SHA-256:           {a.ApkSha256}");
        Console.WriteLine($"Package:           {a.Manifest.Package} {(a.IsTargetPackage ? "(NightClub Simulator)" : "(NOT the target game)")}");
        Console.WriteLine($"Version:           {a.VersionLabel}");
        Console.WriteLine($"SDK:               min {a.Manifest.MinSdk} target {a.Manifest.TargetSdk}");
        Console.WriteLine($"extractNativeLibs: {a.Manifest.ExtractNativeLibs?.ToString() ?? "(default)"}");
        Console.WriteLine($"ABIs:              {string.Join(", ", a.Abis)} (libil2cpp: {a.LibIl2CppMachine})");
        Console.WriteLine($"Unity:             {a.UnityVersion} [{a.UnityVersionSource}]");
        Console.WriteLine($"IL2CPP:            {a.Il2Cpp.IsIl2Cpp} (metadata v{a.Il2Cpp.MetadataVersion})");
        Console.WriteLine($"XR:                {a.XrRuntime} {string.Join(", ", a.XrEvidence.Take(6))}");
        Console.WriteLine($"Signer:            {a.Signer?.ToString() ?? "unsigned"}");
        Console.WriteLine($"libmain NEEDED:    {string.Join(", ", a.LibMainNeeded)}");
        Console.WriteLine($"Missing files:     {(a.MissingRequiredEntries.Count == 0 ? "none" : string.Join(", ", a.MissingRequiredEntries))}");
        foreach (var kv in a.FileHashes) Console.WriteLine($"  {kv.Key,-58} {kv.Value}");
        Console.WriteLine("Modification indicators:");
        if (a.ModificationIndicators.Count == 0) Console.WriteLine("  none (looks original)");
        foreach (var i in a.ModificationIndicators) Console.WriteLine("  - " + i);
        if (a.IsTargetPackage) Console.WriteLine("Compatibility:     " + svc.Profiles.Evaluate(a).Summary);
    }
}
