namespace NightClubPatcher.Core.Recovery;

/// <summary>What happens to the game's external data (Android/data, Android/obb) when a signature mismatch forces a reinstall.</summary>
public enum GameDataMode
{
    /// <summary>Back up, reinstall, restore (default).</summary>
    Keep,
    /// <summary>Back up, reinstall, do not restore.</summary>
    BackupOnly,
    /// <summary>No backup, no restore.</summary>
    Skip,
    /// <summary>Back up for safety, reinstall, remove any leftover external data so the game starts fresh.</summary>
    Clean,
}

public static class GameDataModes
{
    public static readonly GameDataMode[] All = { GameDataMode.Keep, GameDataMode.BackupOnly, GameDataMode.Skip, GameDataMode.Clean };

    /// <summary>CLI value: keep, backup-only, skip, clean.</summary>
    public static string CliName(this GameDataMode m) => m switch
    {
        GameDataMode.BackupOnly => "backup-only",
        GameDataMode.Skip => "skip",
        GameDataMode.Clean => "clean",
        _ => "keep",
    };

    /// <summary>GUI label.</summary>
    public static string Label(this GameDataMode m) => m switch
    {
        GameDataMode.BackupOnly => "Backup only",
        GameDataMode.Skip => "Skip restore",
        GameDataMode.Clean => "Clean install",
        _ => "Normal restore",
    };

    public static bool TryParse(string? value, out GameDataMode mode)
    {
        mode = GameDataMode.Keep;
        var v = (value ?? "").Trim().ToLowerInvariant().Replace('_', '-').Replace(" ", "-");
        foreach (var m in All)
            if (v == m.CliName() || v == m.ToString().ToLowerInvariant() || v == m.Label().ToLowerInvariant().Replace(' ', '-'))
            {
                mode = m;
                return true;
            }
        if (v is "backuponly" or "backup") { mode = GameDataMode.BackupOnly; return true; }
        return false;
    }

    public static bool BacksUp(this GameDataMode m) => m != GameDataMode.Skip;
    public static bool Restores(this GameDataMode m) => m == GameDataMode.Keep;
}
