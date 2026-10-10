using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using NightClubPatcher.Core;
using NightClubPatcher.Core.Infrastructure;
using NightClubPatcher.Core.Patching;
using NightClubPatcher.Core.Recovery;

namespace NightClubPatcher.Gui;

public partial class MainWindow : Window
{
    private readonly ModKitService _svc;
    private readonly WpfInteraction _ui;
    private bool _busy;

    /// <summary>Set from the Advanced window.</summary>
    public bool AllowUnverified { get; set; }
    public bool EmbedMods { get; set; } = true;

    public MainWindow()
    {
        InitializeComponent();
        _svc = new ModKitService();
        _ui = new WpfInteraction(this);
        _svc.Log.Message += (level, msg) => Dispatcher.BeginInvoke(() => AppendLog(level, msg));
        Loaded += async (_, _) => await RefreshAsync();
    }

    public ModKitService Service => _svc;

    private void AppendLog(LogLevel level, string msg)
    {
        if (level == LogLevel.Debug) return;
        var prefix = level switch { LogLevel.Error => "✗ ", LogLevel.Warn => "! ", LogLevel.Success => "✓ ", _ => "  " };
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss} {prefix}{msg}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    public async Task RunAsync(string label, Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        Actions.IsEnabled = false;
        BusyText.Text = label + "...";
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _svc.Log.Error(ex.Message);
            MessageBox.Show(this, ex.Message, label, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            Actions.IsEnabled = true;
            BusyText.Text = "";
        }
    }

    private Task Bg(Action a) => Task.Run(a);

    private async Task RefreshAsync()
    {
        await RunAsync("Detecting", async () =>
        {
            var s = await Task.Run(() => _svc.Detect());
            var a = s.BackupAnalysis;
            QuestText.Text = s.DeviceText;
            GameText.Text = s.Installed?.Installed == true ? "Detected" : s.Device == Core.Adb.DeviceState.Connected ? "Not installed" : "Unknown (no Quest)";
            VersionText.Text = s.Installed?.VersionName ?? a?.Manifest.VersionName ?? "?";
            VersionCodeText.Text = (s.Installed?.VersionCode ?? a?.Manifest.VersionCode)?.ToString() ?? "?";
            UnityText.Text = a?.UnityVersion ?? "? (needs backup)";
            ArchText.Text = a == null ? "?" : a.IsArm64 ? "ARM64" : "NOT ARM64";
            BackendText.Text = a == null ? "?" : a.Il2Cpp.IsIl2Cpp ? $"IL2CPP (metadata v{a.Il2Cpp.MetadataVersion})" : "not IL2CPP";
            XrText.Text = a?.XrRuntime ?? "?";
            BackupText.Text = s.Backup != null ? "✓ " + Path.GetFileName(s.Backup.Directory) : "✗ none (created automatically on first patch)";
            InstallText.Text = s.Installed?.Describe() ?? "?";
            LoaderText.Text = s.Installed?.Kind == InstallationKind.ModdedByModKit ? "Installed" : "Not Installed";
            LoaderText.Text += s.LoaderPath == null ? "   (libncsloader.so not built!)" : "";
            BuildsText.Text = $"current: {(s.PatchedCurrent != null ? "✓" : "-")}   previous: {(s.PatchedPrevious != null ? "✓" : "-")}";
            CompatText.Text = s.Compatibility?.Summary ?? "?";
            SafeModeText.Text = s.SafeMode == true ? "ON" : s.SafeMode == false ? "off" : "?";
            WarningText.Text = string.Join(Environment.NewLine + Environment.NewLine, s.Warnings);
        });
    }

    private PatchOptions Options() => new() { AllowUnverified = AllowUnverified, EmbedMods = EmbedMods };

    private async void Patch_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync("Patching", async () =>
        {
            var r = await Task.Run(() => _svc.Patch(Options()));
            Report("PATCH GAME", r.Ok, r.Message + (r.OutputApk != null ? Environment.NewLine + r.OutputApk : ""));
        });
        await RefreshAsync();
    }

    private async void PatchInstall_Click(object sender, RoutedEventArgs e)
    {
        var launch = LaunchAfterInstall.IsChecked == true;
        await RunAsync("Patching + installing", async () =>
        {
            var (p, i) = await Task.Run(() => _svc.PatchAndInstall(Options(), _ui, launch));
            if (!p.Ok) { Report("PATCH + INSTALL", false, p.Message); return; }
            if (i == null || !i.Ok) { Report("PATCH + INSTALL", false, i?.Message ?? "Install skipped."); return; }
            Report("PATCH + INSTALL", true, i.Message + Environment.NewLine + Environment.NewLine +
                   "Use OPEN LOGCAT and look for:" + Environment.NewLine + "[NCSLoader] Hello from NightClubModKit");
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
        if (ok) _svc.Log.Success(message);
        else _svc.Log.Error(message);
        MessageBox.Show(this, message, title, MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Error);
    }
}
