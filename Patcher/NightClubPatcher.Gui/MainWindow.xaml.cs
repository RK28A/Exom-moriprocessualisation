using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NightClubPatcher.Core;
using NightClubPatcher.Core.Adb;
using NightClubPatcher.Core.Infrastructure;
using NightClubPatcher.Core.Patching;
using NightClubPatcher.Core.Recovery;

namespace NightClubPatcher.Gui;

/// <summary>One line of the in-app activity log.</summary>
public sealed class LogEntry
{
    public required string Time { get; init; }
    public required LogLevel Level { get; init; }
    public required string Glyph { get; init; }
    public required string Text { get; init; }
    public required Brush Brush { get; init; }
    public FontWeight Weight { get; init; } = FontWeights.Normal;
    public override string ToString() => $"{Time} {Glyph} {Text}";
}

/// <summary>One workflow step (KitLog.Step) shown in the STEPS list.</summary>
public sealed class StepEntry : INotifyPropertyChanged
{
    private string _glyph = "●";
    private Brush _brush = Palette.Accent;
    private Brush _textBrush = Palette.Fg;

    public required string Title { get; init; }
    public string Glyph { get => _glyph; set { _glyph = value; Changed(nameof(Glyph)); } }
    public Brush Brush { get => _brush; set { _brush = value; Changed(nameof(Brush)); } }
    public Brush TextBrush { get => _textBrush; set { _textBrush = value; Changed(nameof(TextBrush)); } }

    public void Done() { Glyph = "✓"; Brush = Palette.Ok; TextBrush = Palette.Muted; }
    public void Failed() { Glyph = "✗"; Brush = Palette.Danger; TextBrush = Palette.Danger; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

internal static class Palette
{
    private static SolidColorBrush B(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public static readonly Brush Fg = B("#F1EDFA");
    public static readonly Brush Muted = B("#9C93B5");
    public static readonly Brush Info = B("#CFC8E3");
    public static readonly Brush Accent = B("#C77DFF");
    public static readonly Brush Ok = B("#3DD68C");
    public static readonly Brush Warn = B("#FFC857");
    public static readonly Brush Danger = B("#FF5C6C");
}

public partial class MainWindow : Window
{
    private readonly ModKitService _svc;
    private readonly WpfInteraction _ui;
    private readonly ObservableCollection<LogEntry> _log = new();
    private readonly ObservableCollection<StepEntry> _steps = new();
    private readonly ICollectionView _logView;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _watcher = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Stopwatch _opWatch = new();
    private bool _busy;
    private bool _refreshing;
    private bool _watching;
    private bool _fillingDevices;
    private bool? _lastResult;
    private bool _loaderReady;
    private bool _toolInstallFailed;
    private string? _deviceSignature;
    private StepEntry? _currentStep;

    /// <summary>Set from the Advanced window.</summary>
    public bool AllowUnverified { get; set; }
    public bool EmbedMods { get; set; } = true;

    private readonly GuiSettings _settings = GuiSettings.Load();
    private bool _loadingSettings;

    public MainWindow()
    {
        InitializeComponent();
        _svc = new ModKitService();
        _ui = new WpfInteraction(this);
        ApplySettings();

        LogList.ItemsSource = _log;
        StepsList.ItemsSource = _steps;
        _logView = CollectionViewSource.GetDefaultView(_log);
        _logView.Filter = o => o is LogEntry e && PassesFilter(e);

        _svc.Log.Message += (level, msg) => Dispatcher.BeginInvoke(() => OnLog(level, msg));
        _svc.Log.Progress += (key, text, fraction) => Dispatcher.BeginInvoke(() => OnProgress(key, text, fraction));
        _clock.Tick += (_, _) => ActivityTime.Text = _opWatch.IsRunning ? $"{_opWatch.Elapsed:mm\\:ss}" : ActivityTime.Text;
        _watcher.Tick += async (_, _) => await WatchDevicesAsync();
        SubtitleText.Text = $"NCMK {typeof(ModKitService).Assembly.GetName().Version?.ToString(3)} - com.mandegames.NightClubSimulater - Quest 2 / Pro / 3 / 3S";

        StatusRight.Text = "Kit folder: " + _svc.Paths.Root;
        Loaded += async (_, _) =>
        {
            _svc.Log.Info("NCMK started. Kit folder: " + _svc.Paths.Root);
            await RefreshAsync();
            await RefreshSetupAsync();
            if (App.SmokeTest)
            {
                // CI: the WPF window opened and the first detection + setup check completed without errors.
                Application.Current.Shutdown(0);
                return;
            }
            _watcher.Start();
            // First start: install whatever is missing (one consent prompt), then point at the next step.
            if (_svc.ToolSetup.Missing().Count > 0) await OfferToolInstallAsync();
            else if (!_loaderReady) _svc.Log.Info("Native loader not built yet: click \"Build Native Loader\" in the SETUP card.");
        };
    }

    public ModKitService Service => _svc;

    // ------------------------------------------------------------------ settings

    private void ApplySettings()
    {
        _loadingSettings = true;
        foreach (var m in GameDataModes.All) DataModeCombo.Items.Add(m.Label());
        var mode = _settings.GameDataMode;
        DataModeCombo.SelectedIndex = Array.IndexOf(GameDataModes.All, mode);
        _svc.DataMode = mode;
        LaunchAfterInstall.IsChecked = _settings.LaunchAfterInstall;
        _loadingSettings = false;
    }

    private void DataMode_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingSettings || DataModeCombo.SelectedIndex < 0) return;
        var mode = GameDataModes.All[DataModeCombo.SelectedIndex];
        _svc.DataMode = mode;
        _settings.DataMode = mode.CliName();
        _settings.Save();
        _svc.Log.Info("Game data mode: " + mode.Label());
    }

    private void LaunchAfterInstall_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.LaunchAfterInstall = LaunchAfterInstall.IsChecked == true;
        _settings.Save();
    }

    // ------------------------------------------------------------------ activity log

    private void OnLog(LogLevel level, string msg)
    {
        if (level == LogLevel.Debug) return;
        if (level == LogLevel.Step)
        {
            if (_busy) StatusText.Text = msg + "...";
            _currentStep?.Done();
            _currentStep = new StepEntry { Title = msg };
            _steps.Add(_currentStep);
        }
        if (level == LogLevel.Error && _currentStep != null && _busy) _currentStep.Failed();

        var (glyph, brush, weight) = level switch
        {
            LogLevel.Step => ("▶", Palette.Accent, FontWeights.SemiBold),
            LogLevel.Success => ("✓", Palette.Ok, FontWeights.SemiBold),
            LogLevel.Warn => ("!", Palette.Warn, FontWeights.Normal),
            LogLevel.Error => ("✗", Palette.Danger, FontWeights.SemiBold),
            _ => (" ", Palette.Info, FontWeights.Normal),
        };
        _log.Add(new LogEntry { Time = DateTime.Now.ToString("HH:mm:ss"), Level = level, Glyph = glyph, Text = msg, Brush = brush, Weight = weight });
        if (_log.Count > 5000) _log.RemoveAt(0);
        if (AutoScroll.IsChecked == true && LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
    }

    private readonly Dictionary<string, LogEntry> _progressLines = new();

    /// <summary>One log line per running copy, updated in place; the progress bar follows it.</summary>
    private void OnProgress(string key, string? text, double? fraction)
    {
        _progressLines.TryGetValue(key, out var old);
        if (text == null)
        {
            if (old != null) _log.Remove(old);
            _progressLines.Remove(key);
            if (_busy) Progress.IsIndeterminate = true;
            return;
        }
        var entry = new LogEntry { Time = DateTime.Now.ToString("HH:mm:ss"), Level = LogLevel.Info, Glyph = "⏳", Text = text, Brush = Palette.Accent };
        var index = old != null ? _log.IndexOf(old) : -1;
        if (index >= 0) _log[index] = entry;
        else _log.Add(entry);
        _progressLines[key] = entry;
        if (fraction is { } f)
        {
            Progress.IsIndeterminate = false;
            Progress.Value = f * 100;
        }
        if (index < 0 && AutoScroll.IsChecked == true && LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
    }

    private bool PassesFilter(LogEntry e) => LogFilter.SelectedIndex switch
    {
        1 => e.Level is LogLevel.Step or LogLevel.Success or LogLevel.Warn or LogLevel.Error,
        2 => e.Level is LogLevel.Warn or LogLevel.Error,
        _ => true,
    };

    private void LogFilter_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => _logView?.Refresh();

    private string LogAsText() => string.Join(Environment.NewLine, _logView.Cast<LogEntry>().Select(e => e.ToString()));

    private void CopyLog_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(LogAsText());

    private void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "Text|*.txt", FileName = $"activity-{DateTime.Now:yyyyMMdd-HHmmss}.txt", InitialDirectory = _svc.Paths.Logs };
        if (dlg.ShowDialog(this) == true) File.WriteAllText(dlg.FileName, LogAsText(), Encoding.UTF8);
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _log.Clear();
        if (!_busy) _steps.Clear();
    }

    // ------------------------------------------------------------------ operations

    public async Task RunAsync(string label, Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        _lastResult = null;
        Actions.IsEnabled = false;
        var installWasEnabled = InstallRepairButton.IsEnabled;
        var buildWasEnabled = BuildNativeButton.IsEnabled;
        InstallRepairButton.IsEnabled = false;
        BuildNativeButton.IsEnabled = false;
        _steps.Clear();
        _currentStep = null;
        ActivityTitle.Text = label + "...";
        StatusText.Text = label + "...";
        ActivityTitle.Foreground = Palette.Fg;
        Progress.IsIndeterminate = true;
        _opWatch.Restart();
        _clock.Start();
        _svc.Log.Info($"──────── {label} ────────");
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _lastResult = false;
            _svc.Log.Error(ex.Message);
            App.WriteCrashLog(ex);
            ErrorDialog.Show(this, label + " failed", ex.Message, ex.ToString(), _svc.Paths.Logs);
        }
        finally
        {
            _opWatch.Stop();
            _clock.Stop();
            ActivityTime.Text = $"{_opWatch.Elapsed:mm\\:ss}";
            Progress.IsIndeterminate = false;
            Progress.Value = _lastResult == false ? 0 : 100;
            if (_currentStep != null)
            {
                if (_lastResult == false) _currentStep.Failed();
                else _currentStep.Done();
            }
            ActivityTitle.Text = _lastResult switch
            {
                true => "✓ " + label + " - done",
                false => "✗ " + label + " - failed",
                _ => label + " - finished",
            };
            ActivityTitle.Foreground = _lastResult switch { true => Palette.Ok, false => Palette.Danger, _ => Palette.Fg };
            StatusText.Text = _lastResult switch { true => "Done.", false => label + " failed - see the log below.", _ => "Ready." };
            _busy = false;
            Actions.IsEnabled = true;
            InstallRepairButton.IsEnabled = installWasEnabled;
            BuildNativeButton.IsEnabled = buildWasEnabled;
        }
    }

    private Task Bg(Action a) => Task.Run(a);

    // ------------------------------------------------------------------ status

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        if (!_busy) StatusText.Text = "Detecting ADB, Quest and NightClub Simulator...";
        try
        {
            var s = await Task.Run(() => _svc.Detect());
            ShowStatus(s);
            if (!_busy) StatusText.Text = s.Device switch
            {
                DeviceState.Connected => "Ready.",
                DeviceState.Unauthorized => "Waiting: accept 'Allow USB debugging' in the headset.",
                DeviceState.MultipleDevices => "Several headsets connected: choose one in the HEADSET card.",
                DeviceState.NoAdb => "ADB is not available: click Install tools.",
                _ => "Ready (no headset connected).",
            };
        }
        catch (Exception ex)
        {
            _svc.Log.Error("Detection failed: " + ex.Message);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ShowStatus(KitStatus s)
    {
        var a = s.BackupAnalysis;
        FillDevices(s);
        UpdateAdbPill(s.AdbHealth, s.Devices, s.Device);
        _deviceSignature = Signature(s.Devices);

        HeadsetText.Text = s.Device switch
        {
            DeviceState.Connected => s.Headset?.DisplayName ?? "Meta Quest",
            DeviceState.MultipleDevices => "Choose a headset",
            DeviceState.Unauthorized => "Unauthorized",
            DeviceState.Offline => "Offline",
            DeviceState.NoAdb => "ADB unavailable",
            _ => "No headset",
        };
        QuestText.Text = s.Device == DeviceState.Connected ? $"{s.DeviceSerial} - {s.AndroidVersion}" : s.DeviceText;
        (AuthText.Text, AuthText.Foreground) = s.Device switch
        {
            DeviceState.Connected => ("Authorization: USB debugging allowed", Palette.Ok),
            DeviceState.Unauthorized => ("Authorization: waiting - accept 'Allow USB debugging' in the headset", Palette.Warn),
            DeviceState.Offline => ("Authorization: device offline - reconnect or Restart ADB", Palette.Warn),
            DeviceState.MultipleDevices => ("Authorization: select a headset above", Palette.Warn),
            _ => ("Authorization: -", Palette.Muted),
        };
        PackageText.Text = GameConstants.PackageName;
        HeadsetCompatText.Text = s.Headset == null ? "-" : s.HeadsetCompatibility?.Summary ?? (s.Headset.Supported ? "supported" : "NOT supported");
        HeadsetCompatText.Foreground = s.Headset == null ? Palette.Muted
            : s.HeadsetCompatibility?.Supported == false || !s.Headset.Supported ? Palette.Danger
            : s.HeadsetCompatibility?.Warnings.Count > 0 ? Palette.Warn : Palette.Ok;

        GameText.Text = s.Installed?.Installed == true ? "Installed" : s.Device == DeviceState.Connected ? "Not installed" : "Unknown (no headset)";
        VersionText.Text = s.Installed?.VersionName ?? a?.Manifest.VersionName ?? "?";
        VersionCodeText.Text = (s.Installed?.VersionCode ?? a?.Manifest.VersionCode)?.ToString() ?? "?";
        InstallText.Text = s.Installed?.Describe() ?? "?";
        LoaderText.Text = (s.Installed?.Kind == InstallationKind.ModdedByModKit ? "Installed" : "Not installed") +
                          (s.LoaderPath == null ? "  (libncmkloader.so not built!)" : "");
        LoaderText.Foreground = s.LoaderPath == null ? Palette.Warn : Palette.Fg;

        UnityText.Text = a?.UnityVersion ?? "? (needs backup)";
        ArchText.Text = a == null ? "?" : a.IsArm64 ? "ARM64 (arm64-v8a)" : "NOT ARM64";
        BackendText.Text = a == null ? "?" : a.Il2Cpp.IsIl2Cpp ? $"IL2CPP (metadata v{a.Il2Cpp.MetadataVersion})" : "not IL2CPP";
        XrText.Text = a?.XrRuntime ?? "?";
        CompatText.Text = s.Compatibility?.Summary ?? "?";
        CompatText.Foreground = s.Compatibility == null ? Palette.Fg : s.Compatibility.AllowsAutomaticPatch ? Palette.Ok : Palette.Warn;

        BackupText.Text = s.Backup != null ? "✓ " + Path.GetFileName(s.Backup.Directory) : "✗ none (created on first patch)";
        BackupText.Foreground = s.Backup != null ? Palette.Ok : Palette.Warn;
        BuildsText.Text = $"current {(s.PatchedCurrent != null ? "✓" : "-")}   previous {(s.PatchedPrevious != null ? "✓" : "-")}";
        SafeModeText.Text = s.SafeMode == true ? "ON" : s.SafeMode == false ? "off" : "?";

        WarningText.Text = string.Join(Environment.NewLine + Environment.NewLine, s.Warnings);
        WarningCard.Visibility = s.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        InstallToolsButton.Visibility = s.MissingTools.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Banner.Visibility = s.MissingTools.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BannerText.Text = s.MissingTools.Count > 0
            ? "Missing tools: " + string.Join(", ", s.MissingTools) + ". Click \"Install / Repair Tools\" to download them into Tools\\ automatically (portable, your PATH is not changed)."
            : "";
    }

    private void UpdateAdbPill(AdbHealth? health, IReadOnlyList<AdbDevice> devices, DeviceState state)
    {
        Brush dot;
        string text;
        if (health == null || !health.Found) { dot = Palette.Danger; text = "ADB not found"; }
        else if (!health.ServerRunning) { dot = Palette.Danger; text = "ADB server error"; }
        else
        {
            var ready = devices.Count(d => d.Ready);
            var unauthorized = devices.Any(d => d.State == "unauthorized");
            (dot, text) = state switch
            {
                DeviceState.Connected => (Palette.Ok, $"ADB connected · {ready} headset{(ready > 1 ? "s" : "")}"),
                DeviceState.MultipleDevices => (Palette.Warn, $"ADB OK · {ready} headsets - pick one"),
                _ when unauthorized => (Palette.Warn, "ADB OK · accept USB debugging in the headset"),
                DeviceState.Offline => (Palette.Warn, "ADB OK · headset offline"),
                _ => (Palette.Warn, "ADB OK · no headset"),
            };
        }
        AdbDot.Fill = dot;
        AdbText.Text = text;
        AdbPill.ToolTip = health == null ? null
            : $"adb: {health.Path ?? "not found"}\nversion: {health.Version ?? "-"}\nserver: {(health.ServerRunning ? "running" : "not running")}" +
              (health.Error != null ? "\n" + health.Error : "");
    }

    private static string Signature(IEnumerable<AdbDevice> devices) => string.Join("|", devices.Select(d => d.Serial + ":" + d.State).OrderBy(x => x));

    /// <summary>Polls "adb devices" every 3 s and refreshes the status when a headset appears/disappears.</summary>
    private async Task WatchDevicesAsync()
    {
        if (_busy || _refreshing || _watching || !_svc.Adb.Available) return;
        _watching = true;
        try
        {
            var devices = await Task.Run(() => _svc.ListDevices());
            var sig = Signature(devices);
            if (sig == _deviceSignature) return;
            var before = _deviceSignature;
            _deviceSignature = sig;
            if (before != null)
            {
                foreach (var d in devices.Where(d => before.Split('|').All(x => !x.StartsWith(d.Serial + ":", StringComparison.Ordinal))))
                    _svc.Log.Info("Headset connected: " + d.Label);
                foreach (var gone in before.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(x => x[..x.LastIndexOf(':')])
                             .Where(serial => devices.All(d => d.Serial != serial)))
                    _svc.Log.Warn("Headset disconnected: " + gone);
            }
            await RefreshAsync();
        }
        catch
        {
            // polling must never disturb the UI
        }
        finally
        {
            _watching = false;
        }
    }

    private void FillDevices(KitStatus s)
    {
        _fillingDevices = true;
        try
        {
            DeviceCombo.Items.Clear();
            foreach (var d in s.Devices)
                DeviceCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = d.Label, Tag = d.Serial, IsEnabled = d.Ready });
            if (s.Devices.Count == 0)
                DeviceCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "No headset connected", IsEnabled = false });
            foreach (System.Windows.Controls.ComboBoxItem item in DeviceCombo.Items)
                if ((string?)item.Tag != null && (string?)item.Tag == (_svc.Adb.PreferredSerial ?? s.DeviceSerial))
                    DeviceCombo.SelectedItem = item;
        }
        finally
        {
            _fillingDevices = false;
        }
    }

    private async void DeviceCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_fillingDevices || DeviceCombo.SelectedItem is not System.Windows.Controls.ComboBoxItem { Tag: string serial }) return;
        if (serial == _svc.Adb.PreferredSerial) return;
        _svc.UseDevice(serial);
        _svc.Log.Info("Selected headset " + serial);
        await RefreshAsync();
    }

    // ------------------------------------------------------------------ tools / adb

    /// <summary>Re-checks every tool (runs them to read versions) and updates the SETUP card.</summary>
    private async Task RefreshSetupAsync()
    {
        try
        {
            var (tools, loaderReady, loaderPath) = await Task.Run(() =>
            {
                var t = _svc.CheckTools();
                var ready = _svc.NativeBuilder.LoaderReady(out var p);
                return (t, ready, p);
            });
            var labels = new Dictionary<Core.Signing.ToolId, System.Windows.Controls.TextBlock>
            {
                [Core.Signing.ToolId.Adb] = SetupAdb,
                [Core.Signing.ToolId.BuildTools] = SetupBuildTools,
                [Core.Signing.ToolId.Jdk] = SetupJdk,
                [Core.Signing.ToolId.Ndk] = SetupNdk,
                [Core.Signing.ToolId.CMake] = SetupCMake,
                [Core.Signing.ToolId.Ninja] = SetupNinja,
            };
            foreach (var t in tools)
            {
                var label = labels[t.Id];
                label.Text = t.StatusText;
                label.Foreground = t.Ready ? Palette.Ok : Palette.Warn;
                label.ToolTip = t.Location;
            }
            _loaderReady = loaderReady;
            SetupLoader.Text = loaderReady ? "Ready" : _svc.NativeBuilder.SourcesPresent ? "Not built" : "Not built (Native sources missing)";
            SetupLoader.Foreground = loaderReady ? Palette.Ok : Palette.Warn;
            SetupLoader.ToolTip = loaderPath;

            var nativeToolsReady = tools.Where(t => Core.Signing.ToolSetup.NativeTools.Contains(t.Id)).All(t => t.Ready);
            BuildNativeButton.IsEnabled = nativeToolsReady && _svc.NativeBuilder.SourcesPresent;
            BuildNativeButton.ToolTip = BuildNativeButton.IsEnabled
                ? "Build libncmkloader.so with the bundled NDK, CMake and Ninja"
                : "Install the Android NDK, CMake and Ninja first (Install / Repair Tools)";
            var allReady = tools.All(t => t.Ready);
            InstallRepairButton.Content = _toolInstallFailed && !allReady ? "Retry Install / Repair" : "Install / Repair Tools";

            // Patching needs the loader.
            PatchButton.IsEnabled = loaderReady;
            PatchInstallButton.IsEnabled = loaderReady;
            PatchInstallButton.ToolTip = loaderReady ? "Choose headset, patch, sign, install" : "Build the native loader first (SETUP card)";
            PatchButton.ToolTip = loaderReady ? "Build the patched APK only" : "Build the native loader first (SETUP card)";
        }
        catch (Exception ex)
        {
            _svc.Log.Warn("Setup check failed: " + ex.Message);
        }
    }

    private async Task OfferToolInstallAsync()
    {
        await RunAsync("Install / Repair Tools", async () =>
        {
            var r = await _svc.InstallToolsAsync(_ui, includeNative: true);
            _toolInstallFailed = !r.Ok;
            _lastResult = r.Ok;
            if (r.Ok)
            {
                _svc.Log.Success("All tools are ready.");
            }
            else if (r.Errors.Count > 0 && r.Errors[0] != "Cancelled by the user.")
            {
                ErrorDialog.Show(this, "Some tools could not be installed",
                    "Already installed tools were kept. Check your internet connection and click \"Retry Install / Repair\".",
                    string.Join(Environment.NewLine, r.Errors), _svc.Paths.Logs);
            }
        });
        await RefreshSetupAsync();
        await RefreshAsync();
        if (!_loaderReady && BuildNativeButton.IsEnabled)
            _svc.Log.Info("Next step: click \"Build Native Loader\" in the SETUP card.");
    }

    private async void BuildNative_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync("Build Native Loader", async () =>
        {
            var r = await Task.Run(() => _svc.BuildNativeLoader());
            Report("BUILD NATIVE LOADER", r.Ok, r.Ok
                ? r.Message + Environment.NewLine + Environment.NewLine + "Patch + Install is now available."
                : r.Message);
        });
        await RefreshSetupAsync();
        await RefreshAsync();
    }

    private async void InstallTools_Click(object sender, RoutedEventArgs e) => await OfferToolInstallAsync();

    private async void RestartAdb_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync("Restarting ADB", async () =>
        {
            var ok = await Task.Run(() => _svc.Adb.RestartServer());
            _lastResult = ok;
            if (ok) _svc.Log.Success("ADB server restarted.");
            else _svc.Log.Error("ADB server could not be restarted.");
        });
        await RefreshAsync();
    }

    private PatchOptions Options() => new() { AllowUnverified = AllowUnverified, EmbedMods = EmbedMods };

    /// <summary>Shows which libncmkmod_*.so will be embedded; false when the user cancels.</summary>
    private bool ConfirmEmbeddedMods(string action)
    {
        var options = Options();
        var mods = _svc.PlannedEmbeddedMods(options);
        string list;
        if (!options.EmbedMods) list = "Embedding is turned off (Advanced): no mod will be embedded.";
        else if (mods.Count == 0) list = "No mod found in Native\\out\\arm64-v8a: only the loader will be embedded." + Environment.NewLine +
                                         "Add a folder to Mods\\ and click BUILD NATIVE LOADER to build mods.";
        else list = "Mods embedded into the APK:" + Environment.NewLine +
                    string.Join(Environment.NewLine, mods.Select(m => $"  \u2022 {m.Name}   ({Path.GetDirectoryName(m.Path)})"));
        _svc.Log.Info(list);
        return _ui.Confirm(action, list, "Continue");
    }

    private async void Patch_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmEmbeddedMods("PATCH GAME")) return;
        await RunAsync("Patching", async () =>
        {
            var r = await Task.Run(() => _svc.Patch(Options(), _ui));
            Report("PATCH GAME", r.Ok, r.Message + (r.OutputApk != null ? Environment.NewLine + r.OutputApk : ""));
        });
        await RefreshAsync();
    }

    private async void PatchInstall_Click(object sender, RoutedEventArgs e)
    {
        var launch = LaunchAfterInstall.IsChecked == true;
        if (!ConfirmEmbeddedMods("PATCH + INSTALL")) return;
        await RunAsync("Patch + install", async () =>
        {
            var (p, i) = await Task.Run(() => _svc.PatchAndInstall(Options(), _ui, launch));
            if (!p.Ok) { Report("PATCH + INSTALL", false, p.Message); return; }
            if (i == null || !i.Ok) { Report("PATCH + INSTALL", false, i?.Message ?? "Install skipped."); return; }
            Report("PATCH + INSTALL", true, i.Message + Environment.NewLine + Environment.NewLine +
                   "Open Logcat and look for:" + Environment.NewLine + "[NCMK] Hello from NCMK");
        });
        await RefreshAsync();
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (!_ui.Confirm("RESTORE ORIGINAL", "Reinstall the clean original NightClub Simulator from your backup?", "Restore"))
            return;
        await RunAsync("Restoring original", async () =>
        {
            var r = await Task.Run(() => _svc.RestoreOriginal(_ui));
            Report("RESTORE ORIGINAL", r.Ok, r.Message);
        });
        await RefreshAsync();
    }

    private async void Unpatch_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync("Unpatching", async () =>
        {
            var (u, i) = await Task.Run(() => _svc.Unpatch(_ui, install: true));
            Report("UNPATCH", u.Ok && (i == null || i.Ok), i?.Message ?? u.Message +
                   Environment.NewLine + "RESTORE ORIGINAL remains the safest recovery option.");
        });
        await RefreshAsync();
    }

    private async void SafeMode_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync("Starting in safe mode", () => Bg(() =>
        {
            if (_svc.SetSafeMode(true)) _svc.Launch();
        }));
        await RefreshAsync();
    }

    private async void Diagnose_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync("Diagnosing", async () =>
        {
            var path = await Task.Run(() => _svc.ExportDiagnostics(_svc.Diagnose()));
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        });
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(_svc.Paths.Logs) { UseShellExecute = true });

    private void Logcat_Click(object sender, RoutedEventArgs e) => new LogcatWindow(_svc) { Owner = this }.Show();

    private void Advanced_Click(object sender, RoutedEventArgs e) => new AdvancedWindow(this) { Owner = this }.ShowDialog();

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync("Backing up", async () =>
        {
            var r = await Task.Run(() => _svc.Backup());
            Report("BACKUP", r.Outcome is Core.Backup.BackupOutcome.Created or Core.Backup.BackupOutcome.AlreadyExists, r.Message);
        });
        await RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void ListData_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync("List game data", async () =>
        {
            var text = await Task.Run(() => _svc.ListGameData(_ui));
            _lastResult = true;
            ErrorDialog.Show(this, "Game data on the headset", "Files the game stores where adb can reach them:", text, null, isError: false);
        });
    }

    private async void BackupData_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync("Backup game data", async () =>
        {
            var b = await Task.Run(() => _svc.BackupGameData(_ui));
            if (b == null) { Report("BACKUP GAME DATA", false, "No headset connected."); return; }
            _lastResult = b.AccessibleDataComplete;
            ErrorDialog.Show(this, b.AccessibleDataComplete ? "Game data saved" : "Game data backup INCOMPLETE",
                b.AccessibleDataComplete ? "Every file adb can reach was saved and verified." : "Some files could not be saved - see the details.",
                b.Describe(), b.Directory, isError: !b.AccessibleDataComplete);
        });
    }

    private async void Emergency_Click(object sender, RoutedEventArgs e)
    {
        string? apk = null;
        var choice = _ui.Choose("EMERGENCY RESTORE",
            "Emergency restore installs an original APK even if the patcher state is broken." + Environment.NewLine +
            "Use the newest Backups/.../original.apk, or pick a file?",
            new[] { "Cancel", "Use newest backup", "Choose original.apk..." });
        if (choice <= 0) return;
        if (choice == 2)
        {
            var dlg = new OpenFileDialog { Filter = "APK|*.apk", InitialDirectory = _svc.Paths.Backups };
            if (dlg.ShowDialog(this) != true) return;
            apk = dlg.FileName;
        }
        await RunAsync("Emergency restore", async () =>
        {
            var r = await Task.Run(() => _svc.EmergencyRestore(_ui, apk));
            Report("EMERGENCY RESTORE", r.Ok, r.Message);
        });
        await RefreshAsync();
    }

    public void Report(string title, bool ok, string message)
    {
        _lastResult = ok;
        if (ok)
        {
            _svc.Log.Success(message);
            MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _svc.Log.Error(message);
        // The last log lines usually explain the failure (adb output, apksigner error, ...).
        var details = string.Join(Environment.NewLine, _log.TakeLast(60).Select(e => e.ToString()));
        ErrorDialog.Show(this, title + " failed", message, details, _svc.Paths.Logs);
    }
}
