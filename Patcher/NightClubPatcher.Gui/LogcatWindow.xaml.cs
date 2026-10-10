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

    /// <summary>adb calls run on a worker thread; lines are marshalled to the UI in batches.</summary>
    private async void Start(bool clear)
    {
        Stop();
        StatusText.Text = "connecting...";
        try
        {
            var (ok, msg, proc) = await Task.Run(() =>
            {
                if (!_svc.ConnectDevice(out var m)) return (false, m, (Process?)null);
                var p = _svc.Logcat.Stream(_svc.Adb, line => Dispatcher.BeginInvoke(() => Append(line)), clear);
                return (true, m, p);
            });
            if (!ok) { StatusText.Text = msg; return; }
            _proc = proc;
            StatusText.Text = "streaming...";
        }
        catch (Exception ex)
        {
            StatusText.Text = "logcat failed: " + ex.Message;
        }
    }

    private void Append(string line)
    {
        if (LoaderOnly.IsChecked == true && !line.Contains("NCS")) return;
        Output.AppendText(line + Environment.NewLine);
        if (Output.LineCount > 20000) Output.Clear();
        Output.ScrollToEnd();
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
