using System.Diagnostics;
using System.Text;

namespace NightClubPatcher.Core.Infrastructure;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
    public string Combined => (StdOut + Environment.NewLine + StdErr).Trim();
}

public static class ProcessRunner
{
    public static ProcessResult Run(string fileName, IEnumerable<string> args, TimeSpan? timeout = null, string? workingDir = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (workingDir != null) psi.WorkingDirectory = workingDir;
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (environment != null)
            foreach (var kv in environment) psi.Environment[kv.Key] = kv.Value;

        using var p = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (stdout) stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };

        try
        {
            p.Start();
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, "", $"Failed to start '{fileName}': {ex.Message}");
        }

        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        var ms = (int)(timeout ?? TimeSpan.FromMinutes(10)).TotalMilliseconds;
        if (!p.WaitForExit(ms))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return new ProcessResult(-2, stdout.ToString(), stderr + $"{Environment.NewLine}Timed out after {ms} ms.");
        }
        p.WaitForExit();
        return new ProcessResult(p.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>
    /// Runs a process to completion, delivering stdout/stderr lines as they arrive (for live progress).
    /// Returns the exit code (-1 if it could not start, -2 on timeout).
    /// </summary>
    public static int RunStreaming(string fileName, IEnumerable<string> args, Action<string> onLine,
        IReadOnlyDictionary<string, string>? environment = null, string? workingDir = null, TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (workingDir != null) psi.WorkingDirectory = workingDir;
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (environment != null)
            foreach (var kv in environment) psi.Environment[kv.Key] = kv.Value;

        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        try
        {
            p.Start();
        }
        catch (Exception ex)
        {
            onLine($"Failed to start '{fileName}': {ex.Message}");
            return -1;
        }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using var reg = ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { /* already gone */ } });
        if (!p.WaitForExit((int)(timeout ?? TimeSpan.FromMinutes(30)).TotalMilliseconds))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return -2;
        }
        p.WaitForExit();
        return p.ExitCode;
    }

    /// <summary>Starts a long-running process and streams its stdout line by line.</summary>
    public static Process StartStreaming(string fileName, IEnumerable<string> args, Action<string> onLine)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return p;
    }
}
