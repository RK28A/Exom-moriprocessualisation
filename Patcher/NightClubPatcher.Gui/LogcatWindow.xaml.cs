using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using NightClubPatcher.Core;

namespace NightClubPatcher.Gui;

public partial class LogcatWindow : Window
{
    private readonly ModKitService _svc;
    private Process? _proc;

    public LogcatWindow(ModKitService svc)
    {
        InitializeComponent();
        _svc = svc;
        Loaded += (_, _) => Start(false);
        Closed += (_, _) => Stop();
    }

    private void Start(bool clear)
    {
        Stop();
        if (!_svc.ConnectDevice(out var msg))
        {
            StatusText.Text = msg;
            return;
        }
        _proc = _svc.Logcat.Stream(_svc.Adb, line => Dispatcher.BeginInvoke(() =>
        {
            if (LoaderOnly.IsChecked == true && !line.Contains("NCS")) return;
            Output.AppendText(line + Environment.NewLine);
            if (Output.LineCount > 20000) Output.Clear();
            Output.ScrollToEnd();
        }), clear);
        StatusText.Text = "streaming...";
    }

    private void Stop()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { /* ignore */ }
        _proc = null;
        StatusText.Text = "stopped";
    }

    private void Start_Click(object sender, RoutedEventArgs e) => Start(false);
    private void ClearStart_Click(object sender, RoutedEventArgs e) { Output.Clear(); Start(true); }
    private void Stop_Click(object sender, RoutedEventArgs e) => Stop();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Text|*.txt",
            FileName = $"logcat-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            InitialDirectory = _svc.Paths.Logs,
        };
        if (dlg.ShowDialog(this) == true) File.WriteAllText(dlg.FileName, Output.Text);
    }
}
