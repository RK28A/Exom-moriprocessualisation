using System.Diagnostics;
using NightClubPatcher.Core.Android;
using NightClubPatcher.Core.Infrastructure;
using NightClubPatcher.Core.Signing;
using Xunit;

namespace NightClubPatcher.Tests;

/// <summary>Runs only where a JDK (keytool / jarsigner) is available.</summary>
public class SigningTests
{
    [Fact]
    public void Keystore_is_generated_once_and_matches_apk_signer_certificate()
    {
        using var tmp = new TempDir();
        var paths = new KitPaths(tmp.File("kit"));
        paths.EnsureCreated();
        var log = new KitLog(null);
        var tools = new ToolLocator(paths);
        if (tools.FindKeytool() == null) return;

        var keys = new KeystoreManager(paths, tools, log);
        var cfg = keys.EnsureKeystore();
        Assert.True(keys.Exists);
        Assert.NotNull(cfg.CertSha256);
        Assert.NotNull(cfg.CertJavaHash);
        var ksBytes = File.ReadAllBytes(paths.KeystorePath);

        // Second call must reuse the same key.
        var again = keys.EnsureKeystore();
        Assert.Equal(cfg.CertSha256, again.CertSha256);
        Assert.Equal(ksBytes, File.ReadAllBytes(paths.KeystorePath));

        // Losing keystore.json must never silently create a new key.
        File.Delete(paths.KeystoreConfigPath);
        Assert.Throws<InvalidOperationException>(() => keys.EnsureKeystore());
        Json.Write(paths.KeystoreConfigPath, cfg);

        // v1-sign an APK with jarsigner and check the reader sees the same certificate.
        var jarsigner = Path.Combine(Path.GetDirectoryName(tools.FindKeytool()!)!, OperatingSystem.IsWindows() ? "jarsigner.exe" : "jarsigner");
        if (!File.Exists(jarsigner)) return;
        var apk = FakeApk.Create(tmp.File("unsigned.apk"), v1SignatureFiles: false);
        var r = ProcessRunner.Run(jarsigner, new[]
        {
            "-keystore", paths.KeystorePath, "-storetype", cfg.StoreType, "-storepass:env", "KS", "-keypass:env", "KS",
            "-sigalg", "SHA256withRSA", "-digestalg", "SHA-256", apk, cfg.Alias,
        }, environment: new Dictionary<string, string> { ["KS"] = cfg.Password });
        Assert.True(r.Ok, r.Combined);

        var signer = ApkSignatureReader.Read(apk);
        Assert.NotNull(signer);
        Assert.Equal("v1", signer!.Scheme);
        Assert.Equal(cfg.CertSha256, signer.CertSha256);
        Assert.Equal(cfg.CertJavaHash, signer.JavaHash);
        Assert.Contains("NightClubModKit", signer.Subject);
    }
}
