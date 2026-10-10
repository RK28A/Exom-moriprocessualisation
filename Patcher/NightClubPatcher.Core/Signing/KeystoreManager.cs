using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NightClubPatcher.Core.Infrastructure;

namespace NightClubPatcher.Core.Signing;

public sealed class KeystoreConfig
{
    /// <summary>Default for configs written before the alias was stored; new keys use "ncmk".</summary>
    public string Alias { get; set; } = KeystoreManager.LegacyAlias;
    public string Password { get; set; } = "";
    public string StoreType { get; set; } = "PKCS12";
    public string? CertSha256 { get; set; }
    public string? CertJavaHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Owns the single, permanent NCMK signing key. The key is created once and reused
/// for every patched build so updates install over each other without uninstalling.
/// </summary>
public sealed class KeystoreManager
{
    public const string DefaultAlias = "ncmk";
    public const string LegacyAlias = "ncsmodkit";

    private readonly KitPaths _paths;
    private readonly ToolLocator _tools;
    private readonly KitLog _log;

    public KeystoreManager(KitPaths paths, ToolLocator tools, KitLog log)
    {
        _paths = paths;
        _tools = tools;
        _log = log;
    }

    /// <summary>
    /// Keys/NightClubModKit.keystore (pre-NCMK name) becomes Keys/NCMK.keystore. The file is copied,
    /// never regenerated: the certificate must stay the same or the installed modded game could no
    /// longer be updated. The old file is kept as a backup.
    /// </summary>
    public void MigrateLegacyKeystore()
    {
        if (File.Exists(_paths.KeystorePath) || !File.Exists(_paths.LegacyKeystorePath)) return;
        File.Copy(_paths.LegacyKeystorePath, _paths.KeystorePath);
        _log.Info($"Signing key migrated: {Path.GetFileName(_paths.LegacyKeystorePath)} -> {Path.GetFileName(_paths.KeystorePath)} (same key; the old file is kept).");
    }

    public bool Exists => File.Exists(_paths.KeystorePath) && File.Exists(_paths.KeystoreConfigPath);

    public KeystoreConfig? TryLoad()
    {
        MigrateLegacyKeystore();
        if (!Exists) return null;
        var cfg = Json.Read<KeystoreConfig>(_paths.KeystoreConfigPath);
        if (cfg != null && (cfg.CertSha256 == null || cfg.CertJavaHash == null))
        {
            (cfg.CertSha256, cfg.CertJavaHash) = ReadCertHashes(cfg);
            if (cfg.CertSha256 != null) Json.Write(_paths.KeystoreConfigPath, cfg);
        }
        return cfg;
    }

    public KeystoreConfig EnsureKeystore()
    {
        var existing = TryLoad();
        if (existing != null) return existing;

        if (File.Exists(_paths.KeystorePath))
            throw new InvalidOperationException(
                $"{_paths.KeystorePath} exists but {_paths.KeystoreConfigPath} is missing. " +
                "Restore keystore.json; a new key will NOT be generated automatically because it would break updates of the installed modded game.");

        var keytool = _tools.FindKeytool() ?? throw new InvalidOperationException("keytool was not found (install a JDK 17+ or set JAVA_HOME).");
        Directory.CreateDirectory(_paths.Keys);

        var cfg = new KeystoreConfig
        {
            Alias = DefaultAlias,
            Password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _log.Step("Generating the permanent NCMK signing key (one time only)");
        var env = new Dictionary<string, string> { ["NCMK_KS_PASS"] = cfg.Password };
        var r = ProcessRunner.Run(keytool, new[]
        {
            "-genkeypair", "-keystore", _paths.KeystorePath, "-storetype", cfg.StoreType,
            "-alias", cfg.Alias, "-keyalg", "RSA", "-keysize", "2048", "-validity", "10000",
            "-dname", "CN=NCMK, OU=Modding, O=NCMK",
            "-storepass:env", "NCMK_KS_PASS", "-keypass:env", "NCMK_KS_PASS",
        }, TimeSpan.FromMinutes(2), environment: env);
        if (!r.Ok) throw new InvalidOperationException("keytool failed: " + r.Combined);

        (cfg.CertSha256, cfg.CertJavaHash) = ReadCertHashes(cfg);
        Json.Write(_paths.KeystoreConfigPath, cfg);
        _log.Success($"Keystore created: {_paths.KeystorePath}. BACK UP the Keys/ folder - losing it forces a reinstall of the modded game.");
        return cfg;
    }

    private (string?, string?) ReadCertHashes(KeystoreConfig cfg)
    {
        try
        {
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(_paths.KeystorePath, cfg.Password, X509KeyStorageFlags.EphemeralKeySet);
#pragma warning restore SYSLIB0057
            return (Convert.ToHexString(SHA256.HashData(cert.RawData)).ToLowerInvariant(),
                NightClubPatcher.Core.Android.ApkSignerInfo.ComputeJavaHash(cert.RawData));
        }
        catch (Exception ex)
        {
            _log.Warn("Could not read keystore certificate: " + ex.Message);
            return (null, null);
        }
    }
}
