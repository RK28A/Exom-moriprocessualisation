using System.Diagnostics;
using NightClubPatcher.Core.Infrastructure;

namespace NightClubPatcher.Core.Adb;

public enum DeviceState { NoAdb, NoDevice, Unauthorized, Offline, Connected, MultipleDevices }

/// <summary>One line of "adb devices -l".</summary>
public sealed record AdbDevice(string Serial, string State, string Description)
{
    private string? Field(string key) => Description.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .FirstOrDefault(p => p.StartsWith(key + ":", StringComparison.Ordinal))?[(key.Length + 1)..];

    public string? Model => Field("model")?.Replace('_', ' ');
    public string? Codename => Field("device");
    public string? Product => Field("product");
    public bool Ready => State == "device";
    public bool IsWireless => Serial.Contains(':') || Serial.Contains("._adb-tls-connect.");

    public QuestModelInfo Headset => QuestDevices.Identify(Model, Codename);

    public string Label
    {
        get
        {
            var state = State switch
            {
                "device" => "",
                "unauthorized" => "  [UNAUTHORIZED - accept USB debugging in the headset]",
                _ => $"  [{State}]",
            };
            var link = IsWireless ? "Wi-Fi" : "USB";
            return $"{Headset.DisplayName}  ({Serial}, {link}){state}";
        }
    }
}

public sealed class AdbException : Exception
{
    public AdbException(string message) : base(message) { }
}

/// <summary>Thin wrapper around adb.exe. Every call is logged.</summary>
public sealed class AdbClient
{
    private readonly KitLog _log;
    public string? AdbPath { get; }
    public string? Serial { get; set; }

    /// <summary>Device chosen by the user (GUI selector, --device, or the pre-patch prompt).</summary>
    public string? PreferredSerial { get; set; }

    public AdbClient(string? adbPath, KitLog log)
    {
        AdbPath = adbPath;
        _log = log;
    }

    public bool Available => AdbPath != null;

    private IEnumerable<string> WithSerial(IEnumerable<string> args)
    {
        if (!string.IsNullOrEmpty(Serial))
        {
            yield return "-s";
            yield return Serial!;
        }
        foreach (var a in args) yield return a;
    }

    public ProcessResult RunTimed(TimeSpan? timeout, params string[] args)
    {
        if (AdbPath == null) throw new AdbException("adb was not found. Put adb.exe in Tools/adb or install Android platform-tools.");
        var full = WithSerial(args).ToList();
        _log.Debug("adb " + string.Join(' ', full));
        var r = ProcessRunner.Run(AdbPath, full, timeout ?? TimeSpan.FromMinutes(2));
        if (!r.Ok) _log.Debug($"adb exit {r.ExitCode}: {r.Combined}");
        return r;
    }

    public ProcessResult Run(params string[] args) => RunTimed(null, args);

    public ProcessResult Shell(string command, TimeSpan? timeout = null) => RunTimed(timeout, "shell", command);

    public string Version()
    {
        var r = Run("version");
        return r.Ok ? r.StdOut.Split('\n').FirstOrDefault()?.Trim() ?? "" : "";
    }

    public List<AdbDevice> Devices() => ParseDevices(RunTimed(null, "devices", "-l").StdOut);

    public static List<AdbDevice> ParseDevices(string output)
    {
        var list = new List<AdbDevice>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices") || line.StartsWith("*")) continue;
            var parts = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            list.Add(new AdbDevice(parts[0], parts[1], string.Join(' ', parts.Skip(2))));
        }
        return list;
    }

    /// <summary>
    /// Selects the device to use and reports its state. The user's choice (PreferredSerial) wins;
    /// with several ready devices and no choice, returns MultipleDevices so callers ask.
    /// </summary>
    public DeviceState DetectDevice(out AdbDevice? device)
    {
        device = null;
        if (!Available) return DeviceState.NoAdb;
        Serial = null;
        var devices = Devices();
        if (devices.Count == 0) return DeviceState.NoDevice;

        if (PreferredSerial != null)
        {
            var chosen = devices.FirstOrDefault(d => d.Serial == PreferredSerial);
            if (chosen != null)
            {
                device = chosen;
                if (chosen.Ready) { Serial = chosen.Serial; return DeviceState.Connected; }
                return chosen.State == "unauthorized" ? DeviceState.Unauthorized : DeviceState.Offline;
            }
            _log.Warn($"Selected device {PreferredSerial} is no longer connected.");
            PreferredSerial = null;
        }

        var ready = devices.Where(d => d.Ready).ToList();
        if (ready.Count > 1) return DeviceState.MultipleDevices;
        if (ready.Count == 1)
        {
            device = ready[0];
            Serial = device.Serial;
            return DeviceState.Connected;
        }

        device = devices[0];
        return device.State switch
        {
            "unauthorized" => DeviceState.Unauthorized,
            _ => DeviceState.Offline,
        };
    }

    /// <summary>Full identification through getprop (works for Wi-Fi devices too).</summary>
    public QuestModelInfo IdentifyHeadset() =>
        QuestDevices.Identify(GetProp("ro.product.model"), GetProp("ro.product.device"), GetProp("ro.product.manufacturer"));

    public string GetProp(string name) => Shell($"getprop {name}").StdOut.Trim();

    public bool IsPackageInstalled(string package)
    {
        var r = Shell($"pm list packages {package}");
        return r.StdOut.Split('\n').Any(l => l.Trim() == "package:" + package);
    }

    /// <summary>Returns every APK path of the package (base.apk first, then splits).</summary>
    public List<string> PackagePaths(string package)
    {
        var r = Shell($"pm path {package}");
        var paths = r.StdOut.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("package:"))
            .Select(l => l["package:".Length..])
            .ToList();
        return paths.OrderBy(p => p.EndsWith("/base.apk") ? 0 : 1).ToList();
    }

    public (string? versionName, long? versionCode) InstalledVersion(string package)
    {
        var r = Shell($"dumpsys package {package}");
        string? name = null;
        long? code = null;
        foreach (var raw in r.StdOut.Split('\n'))
        {
            var line = raw.Trim();
            if (name == null && line.StartsWith("versionName="))
                name = line["versionName=".Length..].Trim();
            if (code == null && line.StartsWith("versionCode="))
            {
                var v = line["versionCode=".Length..].Split(' ')[0];
                if (long.TryParse(v, out var c)) code = c;
            }
        }
        return (name, code);
    }

    public bool Pull(string remote, string local, TimeSpan? timeout = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(local))!);
        var r = RunTimed(timeout ?? TimeSpan.FromMinutes(30), "pull", remote, local);
        if (!r.Ok) _log.Warn($"adb pull {remote} failed: {r.Combined}");
        return r.Ok;
    }

    public bool Push(string local, string remote, TimeSpan? timeout = null)
    {
        var r = RunTimed(timeout ?? TimeSpan.FromMinutes(30), "push", local, remote);
        if (!r.Ok) _log.Warn($"adb push {local} failed: {r.Combined}");
        return r.Ok;
    }

    public ProcessResult Install(string apk, bool replace = true)
    {
        var args = new List<string> { "install" };
        if (replace) args.Add("-r");
        args.Add(apk);
        return RunTimed(TimeSpan.FromMinutes(30), args.ToArray());
    }

    public ProcessResult InstallMultiple(IEnumerable<string> apks, bool replace = true)
    {
        var args = new List<string> { "install-multiple" };
        if (replace) args.Add("-r");
        args.AddRange(apks);
        return RunTimed(TimeSpan.FromMinutes(30), args.ToArray());
    }

    public ProcessResult Uninstall(string package) => RunTimed(TimeSpan.FromMinutes(5), "uninstall", package);

    public void ForceStop(string package) => Shell($"am force-stop {package}");

    public bool RemoteExists(string path) => Shell($"ls -d \"{path}\" >/dev/null 2>&1 && echo yes || echo no").StdOut.Trim() == "yes";

    public bool Launch(string package)
    {
        var resolve = Shell($"cmd package resolve-activity --brief {package}");
        var component = resolve.StdOut.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Contains('/'));
        ProcessResult r;
        if (component != null)
            r = Shell($"am start -n {component}");
        else
            r = Shell($"monkey -p {package} -c android.intent.category.LAUNCHER 1");
        return r.Ok && !r.Combined.Contains("Error", StringComparison.OrdinalIgnoreCase);
    }

    public Process StartLogcat(Action<string> onLine, params string[] extraArgs)
    {
        if (AdbPath == null) throw new AdbException("adb was not found.");
        var args = WithSerial(new[] { "logcat", "-v", "threadtime" }.Concat(extraArgs)).ToList();
        return ProcessRunner.StartStreaming(AdbPath, args, onLine);
    }
}
