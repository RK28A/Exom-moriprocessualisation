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
        NightClub Simulator Mod Patcher (NightClubModKit) - target: com.mandegames.NightClubSimulater

        Usage: NightClubPatcher.exe <command> [options]

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
          --launch                     Start the game
          --logcat                     Stream filtered logcat (NCSLoader/Unity/il2cpp/crashes)
          --safe-mode on|off           Toggle /sdcard/NightClubMods/disable_mods
          --pin-fingerprints           Record SHA-256 of libmain/libunity/libil2cpp/metadata in Versions/
          --analyze <apk>              Inspect any APK file (read-only)
          --import-backup <apk>        Use a local original APK as the backup
          --elf-needed <so>            List DT_NEEDED of a shared library
          --elf-add-needed <in.so> <lib> <out.so>   Developer: add a DT_NEEDED entry
          --tools                      Show located external tools

        Options
          --allow-unverified           Allow patching builds whose fingerprints are not verified
          --no-mods                    Do not embed libncsmod_*.so into the APK
          --loader <path>              Use a specific libncsloader.so
          --mods keep|disable|delete   What to do with /sdcard/NightClubMods on restore (default: ask)
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
        svc.Log.Message += (level, msg) =>
        {
            if (level == LogLevel.Debug && !args.Contains("--verbose")) return;
            Console.ForegroundColor = level switch
            {
                LogLevel.Error => ConsoleColor.Red,
                LogLevel.Warn => ConsoleColor.Yellow,
                LogLevel.Success => ConsoleColor.Green,
                LogLevel.Debug => ConsoleColor.DarkGray,
                _ => ConsoleColor.Gray,
            };
            Console.WriteLine(msg);
            Console.ResetColor();
        };
        IUserInteraction ui = args.Contains("--non-interactive") ? new NonInteractive(svc.Log) : new ConsoleInteraction();

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

            case "--patch":
            {
                var r = svc.Patch(Options(args));
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
                    svc.Log.Info("Check the loader with: NightClubPatcher.exe --logcat   (expect '[NCSLoader] Hello from NightClubModKit')");
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
                Console.WriteLine($"{"libncsloader.so",-36} {svc.Paths.FindLoader() ?? "NOT BUILT"}");
                Console.WriteLine($"{"Kit root",-36} {svc.Paths.Root}");
                return 0;

            default:
                Console.WriteLine("Unknown command: " + cmd);
                Console.WriteLine(Help);
                return 1;
        }
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
        Console.WriteLine("      NightClub Simulator Mod Patcher");
        Console.WriteLine(new string('-', 50));
        Console.WriteLine($"Quest:                 {s.DeviceText}");
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
