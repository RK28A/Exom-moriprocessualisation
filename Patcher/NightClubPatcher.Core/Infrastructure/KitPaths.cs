namespace NightClubPatcher.Core.Infrastructure;

/// <summary>
/// Resolves every on-disk location used by the toolkit. The root is the folder containing
/// NightClubModKit.sln (development) or the folder that contains a "Versions" directory
/// next to / above the executable (distribution).
/// </summary>
public sealed class KitPaths
{
    public string Root { get; }

    public KitPaths(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public static KitPaths Discover()
    {
        var env = Environment.GetEnvironmentVariable("NCS_MODKIT_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return new KitPaths(env);

        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "NightClubModKit.sln")) ||
                    File.Exists(Path.Combine(dir.FullName, "NightClubModKit.root")))
                    return new KitPaths(dir.FullName);
                dir = dir.Parent;
            }
        }

        return new KitPaths(AppContext.BaseDirectory);
    }

    public string Backups => Path.Combine(Root, "Backups");
    public string Builds => Path.Combine(Root, "Builds");
    public string Cache => Path.Combine(Root, "Cache");
    public string Versions => Path.Combine(Root, "Versions");
    public string Logs => Path.Combine(Root, "Logs");
    public string Tools => Path.Combine(Root, "Tools");
    public string Keys => Path.Combine(Root, "Keys");
    public string NativeOut => Path.Combine(Root, "Native", "out", GameConstants.Abi);
    public string Payload => Path.Combine(Root, "Payload", GameConstants.Abi);

    public string KeystorePath => Path.Combine(Keys, "NightClubModKit.keystore");
    public string KeystoreConfigPath => Path.Combine(Keys, "keystore.json");

    public string BackupRoot => Path.Combine(Backups, GameConstants.PackageName);
    public string UserDataBackupRoot => Path.Combine(BackupRoot, "userdata");

    public string BackupDir(string versionName, long versionCode) =>
        Path.Combine(BackupRoot, $"{Sanitize(versionName)}-{versionCode}");

    public string BuildDir(string versionName, long versionCode) =>
        Path.Combine(Builds, $"{Sanitize(versionName)}-{versionCode}");

    public string CacheDir(string versionName, long versionCode) =>
        Path.Combine(Cache, $"{Sanitize(versionName)}-{versionCode}");

    public static string WorkspaceRoot => Path.Combine(Path.GetTempPath(), "NightClubModKit");

    /// <summary>Candidate locations of the compiled native loader (first existing wins).</summary>
    public IEnumerable<string> LoaderCandidates()
    {
        yield return Path.Combine(NativeOut, GameConstants.LoaderLibName);   // Native/out/arm64-v8a (Build Native Loader)
        yield return Path.Combine(Root, GameConstants.LoaderLibName);        // next to NightClubPatcher.exe
        yield return Path.Combine(AppContext.BaseDirectory, GameConstants.LoaderLibName);
        yield return Path.Combine(Payload, GameConstants.LoaderLibName);
        yield return Path.Combine(AppContext.BaseDirectory, "Payload", GameConstants.Abi, GameConstants.LoaderLibName);
    }

    /// <summary>
    /// The loader the patcher injects: the most recently built of all candidates, so a fresh
    /// "Build Native Loader" (Native/out/arm64-v8a) always wins over an older bundled copy.
    /// </summary>
    public string? FindLoader() => LoaderCandidates()
        .Where(File.Exists)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderByDescending(File.GetLastWriteTimeUtc)
        .FirstOrDefault();

    /// <summary>Directories scanned for native mods (libncsmod_*.so) that can be embedded into the APK.</summary>
    public IEnumerable<string> ModPayloadDirs()
    {
        yield return Root;
        yield return Payload;
        yield return NativeOut;
    }

    public void EnsureCreated()
    {
        foreach (var d in new[] { Backups, Builds, Cache, Versions, Logs, Tools, Keys })
            Directory.CreateDirectory(d);
    }

    public static string Sanitize(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
