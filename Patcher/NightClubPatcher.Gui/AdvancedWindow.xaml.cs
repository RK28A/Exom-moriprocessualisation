using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NightClubPatcher.Core;
using NightClubPatcher.Core.Apk;
using NightClubPatcher.Core.Infrastructure;
using NightClubPatcher.Core.Patching;

namespace NightClubPatcher.Gui;

public partial class AdvancedWindow : Window
{
    private readonly MainWindow _main;
    private readonly ModKitService _svc;
    private string? _apk;

    public AdvancedWindow(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        _svc = main.Service;
        AllowUnverified.IsChecked = main.AllowUnverified;
        EmbedMods.IsChecked = main.EmbedMods;
        SceneLoaderDiagnostics.IsChecked = main.SceneLoaderDiagnostics;
        SceneLoaderRecovery.IsChecked = main.SceneLoaderRecovery;
        Loaded += (_, _) =>
        {
            var sb = new StringBuilder();
            foreach (var (tool, path) in _svc.Tools.Report()) sb.AppendLine($"{tool,-38} {path ?? "NOT FOUND"}");
            sb.AppendLine($"{"libncmkloader.so",-38} {_svc.Paths.FindLoader() ?? "NOT BUILT (Native/build.ps1)"}");
            sb.AppendLine($"{"Kit root",-38} {_svc.Paths.Root}");
            sb.AppendLine($"{"Signing key",-38} {(_svc.Keys.Exists ? _svc.Paths.KeystorePath : "not created yet")}");
            ToolsInfo.Text = sb.ToString();
            var b = _svc.Backups.Latest();
            if (b != null) LoadApk(b.ApkPath);
        };
    }

    // ------------------------------------------------------------ APK explorer

    private void LoadApk(string path)
    {
        _apk = path;
        ApkPathText.Text = path;
        ApkTree.Items.Clear();
        using var zip = new ZipArchiveReader(path);
        var root = new TreeViewItem { Header = Path.GetFileName(path), IsExpanded = true };
        var nodes = new Dictionary<string, TreeViewItem>();
        foreach (var e in zip.Entries.OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            var parts = e.Name.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var parent = root;
            var prefix = "";
            for (var i = 0; i < parts.Length; i++)
            {
                prefix += parts[i] + (i < parts.Length - 1 ? "/" : "");
                if (i == parts.Length - 1 && !e.IsDirectory)
                {
                    parent.Items.Add(new TreeViewItem { Header = $"{parts[i]}  ({e.UncompressedSize:N0} B)", Tag = e.Name });
                }
                else
                {
                    if (!nodes.TryGetValue(prefix, out var node))
                    {
                        node = new TreeViewItem { Header = parts[i] + "/", IsExpanded = prefix is "lib/" or "lib/arm64-v8a/" };
                        nodes[prefix] = node;
                        parent.Items.Add(node);
                    }
                    parent = node;
                }
            }
        }
        ApkTree.Items.Add(root);
        _ = ShowUnityInfoAsync(path);
    }

    private async Task ShowUnityInfoAsync(string path)
    {
        UnityInfo.Text = "Analyzing...";
        var a = await Task.Run(() => _svc.Analyze(path));
        var sb = new StringBuilder();
        sb.AppendLine($"APK                 {path}");
        sb.AppendLine($"Package             {a.Manifest.Package}");
        sb.AppendLine($"Version             {a.VersionLabel}");
        sb.AppendLine($"Unity Version       {a.UnityVersion} ({a.UnityVersionSource})");
        sb.AppendLine($"Backend             {(a.Il2Cpp.IsIl2Cpp ? "IL2CPP" : "not IL2CPP")}");
        sb.AppendLine($"Architecture        {(a.IsArm64 ? "ARM64" : "?")}  ABIs: {string.Join(", ", a.Abis)}");
        sb.AppendLine($"Metadata            {GameConstants.MetadataEntry}  (v{a.Il2Cpp.MetadataVersion}, magic ok: {a.Il2Cpp.MetadataMagicValid})");
        sb.AppendLine($"Main Unity data     {GameConstants.DataUnity3dEntry}");
        sb.AppendLine($"XR                  {a.XrRuntime}");
        sb.AppendLine($"extractNativeLibs   {a.Manifest.ExtractNativeLibs?.ToString() ?? "(default)"}");
        sb.AppendLine($"Signer              {a.Signer?.ToString() ?? "unsigned"}");
        sb.AppendLine($"libmain NEEDED      {string.Join(", ", a.LibMainNeeded)}");
        sb.AppendLine();
        foreach (var kv in a.FileHashes) sb.AppendLine($"{kv.Key,-58} {kv.Value}");
        sb.AppendLine();
        sb.AppendLine("Modification indicators:");
        if (a.ModificationIndicators.Count == 0) sb.AppendLine("  none");
        foreach (var i in a.ModificationIndicators) sb.AppendLine("  - " + i);
        if (a.IsTargetPackage) sb.AppendLine().AppendLine("Compatibility: " + _svc.Profiles.Evaluate(a).Summary);
        sb.AppendLine();
        sb.AppendLine("IL2CPP analysis (Cpp2IL cache) and SDK generation: planned, see docs/ROADMAP.md");
        UnityInfo.Text = sb.ToString();
    }

    private string? SelectedEntry() => (ApkTree.SelectedItem as TreeViewItem)?.Tag as string;

    private void OpenBackup_Click(object sender, RoutedEventArgs e)
    {
        var b = _svc.Backups.Latest();
        if (b != null) LoadApk(b.ApkPath);
    }

    private void OpenPatched_Click(object sender, RoutedEventArgs e)
    {
        var m = _svc.Backups.Latest()?.Metadata;
        var p = m == null ? null : _svc.Builds.CurrentApk(m.VersionName, m.VersionCode);
        if (p != null) LoadApk(p);
        else MessageBox.Show(this, "No patched build yet.");
    }

    private void OpenApk_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "APK|*.apk" };
        if (dlg.ShowDialog(this) == true) LoadApk(dlg.FileName);
    }

    private void Extract_Click(object sender, RoutedEventArgs e)
    {
        var name = SelectedEntry();
        if (_apk == null || name == null) return;
        var dlg = new SaveFileDialog { FileName = Path.GetFileName(name) };
        if (dlg.ShowDialog(this) != true) return;
        using var zip = new ZipArchiveReader(_apk);
        zip.Extract(zip.Find(name)!, dlg.FileName);
    }

    private void Hash_Click(object sender, RoutedEventArgs e)
    {
        var name = SelectedEntry();
        if (_apk == null) return;
        if (name == null)
        {
            EntryInfo.Text = "APK SHA-256:\n" + Hashing.Sha256File(_apk);
            return;
        }
        using var zip = new ZipArchiveReader(_apk);
        using var s = zip.OpenRead(zip.Find(name)!);
        EntryInfo.Text = $"{name}\nSHA-256:\n{Hashing.Sha256(s)}";
    }

    private void Info_Click(object sender, RoutedEventArgs e)
    {
        var name = SelectedEntry();
        if (_apk == null || name == null) return;
        using var zip = new ZipArchiveReader(_apk);
        var en = zip.Find(name)!;
        var sb = new StringBuilder();
        sb.AppendLine(en.Name);
        sb.AppendLine($"Method:      {(en.IsStored ? "stored" : "deflate")}");
        sb.AppendLine($"Size:        {en.UncompressedSize:N0}");
        sb.AppendLine($"Compressed:  {en.CompressedSize:N0}");
        sb.AppendLine($"CRC32:       {en.Crc32:X8}");
        sb.AppendLine($"Data offset: 0x{en.DataOffset:X} (4K aligned: {en.DataOffset % 4096 == 0})");
        if (name.EndsWith(".so"))
        {
            try
            {
                var elf = new Core.Elf.ElfFile(zip.ReadAllBytes(en));
                sb.AppendLine($"ELF:         {elf.MachineName}");
                sb.AppendLine($"SONAME:      {elf.GetSoName()}");
                foreach (var n in elf.GetNeeded()) sb.AppendLine($"NEEDED:      {n}");
            }
            catch (Exception ex)
            {
                sb.AppendLine("ELF parse failed: " + ex.Message);
            }
        }
        EntryInfo.Text = sb.ToString();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_apk == null) return;
        var dlg = new SaveFileDialog { FileName = Path.GetFileName(_apk), Filter = "APK|*.apk" };
        if (dlg.ShowDialog(this) == true) File.Copy(_apk, dlg.FileName, overwrite: true);
    }

    /// <summary>Replace never edits the opened file: it writes a new APK in the workspace and signs it.</summary>
    private async void Replace_Click(object sender, RoutedEventArgs e)
    {
        var name = SelectedEntry();
        if (_apk == null || name == null) return;
        var pick = new OpenFileDialog { Title = "Replacement for " + name };
        if (pick.ShowDialog(this) != true) return;
        var save = new SaveFileDialog { FileName = Path.GetFileNameWithoutExtension(_apk) + "-modified.apk", Filter = "APK|*.apk" };
        if (save.ShowDialog(this) != true) return;
        var source = _apk;
        var replacement = pick.FileName;
        var output = save.FileName;
        if (Path.GetFullPath(output) == Path.GetFullPath(source))
        {
            MessageBox.Show(this, "Choose a different output file; the source APK is never overwritten.");
            return;
        }
        await _main.RunAsync("Replacing entry", () => Task.Run(() =>
        {
            var ws = PatchWorkspace.Create("replace");
            var plan = new ApkRewritePlan();
            plan.ReplaceEntry(name, File.ReadAllBytes(replacement));
            var unsigned = Path.Combine(ws.Patched, "unsigned.apk");
            ApkRewriter.Rewrite(source, unsigned, plan);
            _svc.Signer.AlignAndSign(unsigned, Path.Combine(ws.Patched, "aligned.apk"), output);
            _svc.Log.Success("Written " + output);
        }));
    }

    // ------------------------------------------------------------ builds & recovery

    private void Options_Changed(object sender, RoutedEventArgs e)
    {
        if (_main == null) return;
        _main.AllowUnverified = AllowUnverified.IsChecked == true;
        _main.EmbedMods = EmbedMods.IsChecked == true;
        _main.SceneLoaderDiagnostics = SceneLoaderDiagnostics.IsChecked == true;
        _main.SceneLoaderRecovery = SceneLoaderRecovery.IsChecked == true;
    }

    private async void Pin_Click(object sender, RoutedEventArgs e) =>
        await _main.RunAsync("Pinning fingerprints", () => Task.Run(() =>
        {
            var p = _svc.PinFingerprints();
            _main.Dispatcher.Invoke(() => _main.Report("Fingerprints", true, $"Pinned in Versions/{p.FileName}"));
        }));

    private async void InstallCurrent_Click(object sender, RoutedEventArgs e) => await Install(false);
    private async void InstallPrevious_Click(object sender, RoutedEventArgs e) => await Install(true);

    private async Task Install(bool previous)
    {
        var ui = new WpfInteraction(this);
        await _main.RunAsync("Installing", () => Task.Run(() =>
        {
            var r = _svc.InstallCurrent(ui, previous);
            _main.Dispatcher.Invoke(() => _main.Report("Install", r.Ok, r.Message));
        }));
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "APK|*.apk" };
        if (dlg.ShowDialog(this) != true) return;
        var r = _svc.Backups.ImportFromFile(dlg.FileName, _svc.InspectOptions());
        _main.Report("Import original", r.Outcome == Core.Backup.BackupOutcome.Created, r.Message);
    }

    private async void Dump_Click(object sender, RoutedEventArgs e) =>
        await _main.RunAsync("Dumping the game", async () =>
        {
            var r = await Task.Run(() => _svc.DumpGameAsync());
            _main.Report("DUMP", r.Ok, r.Message);
            if (r.Ok && r.FilePath != null)
            {
                try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{r.FilePath}\"") { UseShellExecute = true }); }
                catch { /* the path is in the report */ }
            }
        });

    private async void PushMods_Click(object sender, RoutedEventArgs e) =>
        await _main.RunAsync("Pushing mods and config", () => Task.Run(() => _svc.PushModsAndConfig().Ok));

    private void OpenDeviceMods_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Path.Combine(_svc.Paths.DeviceMods, "Mods"));
        Directory.CreateDirectory(Path.Combine(_svc.Paths.DeviceMods, "Config"));
        Process.Start(new ProcessStartInfo(_svc.Paths.DeviceMods) { UseShellExecute = true });
    }

    private async void SafeOff_Click(object sender, RoutedEventArgs e) =>
        await _main.RunAsync("Disabling safe mode", () => Task.Run(() => _svc.SetSafeMode(false)));

    private async void Launch_Click(object sender, RoutedEventArgs e) =>
        await _main.RunAsync("Launching", () => Task.Run(() => _svc.Launch()));

    private void OpenBackups_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(_svc.Paths.Backups) { UseShellExecute = true });

    private void OpenBuilds_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(_svc.Paths.Builds) { UseShellExecute = true });
}
