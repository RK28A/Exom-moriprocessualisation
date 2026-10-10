using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace NightClubPatcher.Gui;

public partial class App : Application
{
    /// <summary>
    /// --smoke-test: open the main window, run the first detection, then exit with code 0
    /// (or non-zero on any startup error). Used by CI to prove NightClubPatcher.exe starts the WPF UI.
    /// </summary>
    public static bool SmokeTest { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        SmokeTest = e.Args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase);
        DispatcherUnhandledException += OnDispatcherUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Fatal(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
            Dispatcher.BeginInvoke(() => ShowError("Background task failed", args.Exception));
        };
        base.OnStartup(e);
    }

    private void OnDispatcherUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (SmokeTest)
        {
            WriteCrashLog(e.Exception);
            Console.Error.WriteLine(e.Exception);
            Shutdown(2);
            return;
        }
        ShowError("Unexpected error", e.Exception);
    }

    private void ShowError(string title, Exception ex)
    {
        WriteCrashLog(ex);
        ErrorDialog.Show(MainWindow is { IsLoaded: true } ? MainWindow : null, title, ex.Message, ex.ToString());
    }

    private static void Fatal(Exception? ex)
    {
        if (ex == null) return;
        var path = WriteCrashLog(ex);
        try
        {
            MessageBox.Show($"NightClub Mod Patcher has to close:\n\n{ex.Message}\n\nDetails were saved to:\n{path}",
                "NightClub Mod Patcher", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // nothing else we can do
        }
    }

    /// <summary>Writes Logs/crash-*.txt next to the application (or %TEMP% as a fallback).</summary>
    public static string WriteCrashLog(Exception ex)
    {
        var text = $"{DateTime.Now:O}{Environment.NewLine}{ex}";
        foreach (var dir in new[] { Path.Combine(AppContext.BaseDirectory, "Logs"), Path.GetTempPath() })
        {
            try
            {
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                File.WriteAllText(path, text);
                return path;
            }
            catch
            {
                // try next location
            }
        }
        return "(could not write crash log)";
    }
}
