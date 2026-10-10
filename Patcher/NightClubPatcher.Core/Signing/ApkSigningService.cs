using NightClubPatcher.Core.Infrastructure;

namespace NightClubPatcher.Core.Signing;

/// <summary>zipalign + apksigner pipeline.</summary>
public sealed class ApkSigningService
{
    private readonly ToolLocator _tools;
    private readonly KeystoreManager _keys;
    private readonly KitPaths _paths;
    private readonly KitLog _log;

    public ApkSigningService(ToolLocator tools, KeystoreManager keys, KitPaths paths, KitLog log)
    {
        _tools = tools;
        _keys = keys;
        _paths = paths;
        _log = log;
    }

    public void Zipalign(string input, string output)
    {
        var zipalign = _tools.FindZipalign() ?? throw new InvalidOperationException(
            "zipalign was not found. Copy it to Tools/zipalign/ or install Android SDK build-tools.");
        if (File.Exists(output)) File.Delete(output);
        _log.Step("Aligning APK (zipalign)");
        var r = ProcessRunner.Run(zipalign, new[] { "-f", "-p", "4", input, output });
        if (!r.Ok) throw new InvalidOperationException("zipalign failed: " + r.Combined);
        var check = ProcessRunner.Run(zipalign, new[] { "-c", "-p", "4", output });
        if (!check.Ok) throw new InvalidOperationException("zipalign verification failed: " + check.Combined);
    }

    public void Sign(string input, string output)
    {
        var cfg = _keys.EnsureKeystore();
        var java = _tools.FindJava() ?? throw new InvalidOperationException("java was not found (install a JDK 17+ or set JAVA_HOME).");
        var jar = _tools.FindApksignerJar() ?? throw new InvalidOperationException(
            "apksigner.jar was not found. Copy build-tools/<ver>/lib/apksigner.jar to Tools/apksigner/ or install Android SDK build-tools.");
        if (File.Exists(output)) File.Delete(output);

        _log.Step("Signing APK");
        var env = new Dictionary<string, string> { ["NCMK_KS_PASS"] = cfg.Password };
        var r = ProcessRunner.Run(java, new[]
        {
            "-jar", jar, "sign",
            "--ks", _paths.KeystorePath, "--ks-type", cfg.StoreType, "--ks-key-alias", cfg.Alias,
            "--ks-pass", "env:NCMK_KS_PASS", "--key-pass", "env:NCMK_KS_PASS",
            "--v1-signing-enabled", "true", "--v2-signing-enabled", "true", "--v3-signing-enabled", "true",
            "--out", output, input,
        }, TimeSpan.FromMinutes(10), environment: env);
        if (!r.Ok) throw new InvalidOperationException("apksigner failed: " + r.Combined);
        Verify(output);
    }

    public void Verify(string apk)
    {
        var java = _tools.FindJava() ?? throw new InvalidOperationException("java was not found.");
        var jar = _tools.FindApksignerJar() ?? throw new InvalidOperationException("apksigner.jar was not found.");
        var r = ProcessRunner.Run(java, new[] { "-jar", jar, "verify", "--print-certs", apk });
        if (!r.Ok) throw new InvalidOperationException("apksigner verify failed: " + r.Combined);
        _log.Debug(r.StdOut.Trim());
    }

    /// <summary>Full pipeline: unsigned → aligned → signed.</summary>
    public void AlignAndSign(string unsigned, string aligned, string signedOut)
    {
        Zipalign(unsigned, aligned);
        Sign(aligned, signedOut);
    }
}
