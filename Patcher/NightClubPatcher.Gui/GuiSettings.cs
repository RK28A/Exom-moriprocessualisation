using System.IO;
using System.Text.Json;

namespace NightClubPatcher.Gui;

/// <summary>
/// GUI preferences, stored per user in %APPDATA%\NCMK\gui-settings.json so they survive a fresh
/// copy of the kit. Never throws: a missing or unreadable file gives the defaults.
/// </summary>
public sealed class GuiSettings
{
    public bool LaunchAfterInstall { get; set; } = true;

    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NCMK", "gui-settings.json");

    public static GuiSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<GuiSettings>(File.ReadAllText(FilePath)) ?? new GuiSettings();
        }
        catch
        {
            // corrupt settings: fall back to defaults
        }
        return new GuiSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // settings are a convenience; never fail an operation because of them
        }
    }
}
