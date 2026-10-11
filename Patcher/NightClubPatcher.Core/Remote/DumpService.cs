using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using NightClubPatcher.Core.Adb;
using NightClubPatcher.Core.Infrastructure;

namespace NightClubPatcher.Core.Remote;

public sealed record DumpResult(bool Ok, string Message, string? FilePath = null, long Bytes = 0, int Classes = 0, int Methods = 0);

/// <summary>
/// IL2CPP dump from the running game. The NCMK loader listens on the abstract socket @ncmk inside the
/// game process; the PC forwards a local TCP port to it (adb forward tcp:0 localabstract:ncmk), sends
/// DUMP and receives the dump text in chunks while the loader builds it progressively. The file is
/// written straight to the PC (Downloads by default) - nothing goes through the headset's storage.
/// </summary>
public sealed class DumpService
{
    public const string SocketName = "ncmk";

    private readonly AdbClient _adb;
    private readonly KitLog _log;

    public DumpService(AdbClient adb, KitLog log)
    {
        _adb = adb;
        _log = log;
    }

    /// <summary>The user's Downloads folder (Windows known folder, else ~/Downloads).</summary>
    public static string DownloadsFolder()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");  // FOLDERID_Downloads
                if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p) == 0 && p != IntPtr.Zero)
                {
                    var path = Marshal.PtrToStringUni(p);
                    Marshal.FreeCoTaskMem(p);
                    if (!string.IsNullOrEmpty(path)) return path;
                }
            }
            catch
            {
                // fall back below
            }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

    public async Task<DumpResult> DumpAsync(string? outputDir = null, CancellationToken ct = default)
    {
        outputDir ??= DownloadsFolder();
        _log.Step("Dumping the game's IL2CPP metadata from the headset");
        if (_adb.Pid(GameConstants.PackageName) == null)
            return new DumpResult(false, "NightClub Simulator is not running. Start the patched game on the headset, wait until it is loaded, then dump.");

        var port = _adb.Forward("localabstract:" + SocketName);
        if (port == null) return new DumpResult(false, "adb forward failed (is the headset connected and authorized?).");
        var tmp = Path.Combine(outputDir, $".ncmk-dump-{Guid.NewGuid():N}.part");
        try
        {
            Directory.CreateDirectory(outputDir);
            using var client = new TcpClient();
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectCts.CancelAfter(TimeSpan.FromSeconds(10));
                await client.ConnectAsync("127.0.0.1", port.Value, connectCts.Token);
            }
            await using var stream = client.GetStream();
            DumpProtocolResult r;
            await using (var file = File.Create(tmp))
            {
                r = await DumpProtocol.RunAsync(stream, file, (done, total, what) =>
                {
                    var frac = total > 0 ? (double)done / total : 0;
                    _log.ReportProgress("dump", $"Dump: {KitLog.ProgressBar(frac)}  {done}/{total} classes  {what}", frac);
                }, ct);
            }
            _log.EndProgress("dump");
            if (!r.Ok)
            {
                File.Delete(tmp);
                return new DumpResult(false, r.Error ?? "The dump failed.");
            }
            var name = $"NCMK-dump-{Sanitize(r.GameVersion)}-{DateTime.Now:yyyyMMdd-HHmmss}.cs";
            var final = Path.Combine(outputDir, name);
            File.Move(tmp, final);
            _log.Success($"Dump saved: {final} ({r.Classes} classes, {r.Methods} methods, {r.Bytes / 1048576.0:0.0} MB)");
            return new DumpResult(true, "Dump saved to " + final, final, r.Bytes, r.Classes, r.Methods);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            _log.EndProgress("dump");
            try { File.Delete(tmp); } catch { /* ignore */ }
            return new DumpResult(false,
                "The NCMK loader in the game did not answer (" + ex.Message + "). Make sure the running game was patched with this " +
                "NCMK version and has finished starting.");
        }
        finally
        {
            _adb.RemoveForward(port.Value);
        }
    }

    private static string Sanitize(string? s) =>
        string.IsNullOrWhiteSpace(s) ? "unknown" : new string(s.Select(c => char.IsLetterOrDigit(c) || c == '.' ? c : '_').ToArray());
}

public sealed record DumpProtocolResult(bool Ok, string? Error, string? GameVersion, long Bytes, int Classes, int Methods);

/// <summary>Client side of the loader's line/chunk protocol (see Native/Remote/command_server.h).</summary>
public static class DumpProtocol
{
    public static async Task<DumpProtocolResult> RunAsync(Stream stream, Stream output, Action<long, long, string> progress,
        CancellationToken ct = default, TimeSpan? idleTimeout = null)
    {
        var reader = new ChunkReader(stream, idleTimeout ?? TimeSpan.FromSeconds(120));
        var hello = await reader.ReadLineAsync(ct);
        if (hello == null || !hello.StartsWith("NCMK ", StringComparison.Ordinal))
            return new DumpProtocolResult(false, "Unexpected answer from the game: " + (hello ?? "(nothing)"), null, 0, 0, 0);
        var parts = hello.Split(' ');
        var gameVersion = parts.Length >= 5 ? parts[4] : null;
        var cmd = Encoding.ASCII.GetBytes("DUMP\n");
        await stream.WriteAsync(cmd, ct);

        long written = 0;
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) return new DumpProtocolResult(false, "The game closed the connection before the end of the dump.", gameVersion, written, 0, 0);
            if (line.StartsWith("P ", StringComparison.Ordinal))
            {
                var p = line.Split(' ', 4);
                if (p.Length >= 3 && long.TryParse(p[1], out var done) && long.TryParse(p[2], out var total))
                    progress(done, total, p.Length == 4 ? p[3] : "");
            }
            else if (line.StartsWith("D ", StringComparison.Ordinal) && int.TryParse(line[2..], out var n) && n >= 0)
            {
                await reader.CopyExactlyAsync(output, n, ct);
                written += n;
            }
            else if (line.StartsWith("E OK", StringComparison.Ordinal))
            {
                var p = line.Split(' ');
                int.TryParse(p.ElementAtOrDefault(3), out var classes);
                int.TryParse(p.ElementAtOrDefault(4), out var methods);
                return new DumpProtocolResult(true, null, gameVersion, written, classes, methods);
            }
            else if (line.StartsWith("E ERR", StringComparison.Ordinal))
            {
                return new DumpProtocolResult(false, "The game reported: " + line[5..].Trim(), gameVersion, written, 0, 0);
            }
            else
            {
                return new DumpProtocolResult(false, "Protocol error: " + line, gameVersion, written, 0, 0);
            }
        }
    }

    /// <summary>Buffered reader for '\n' lines and exact-length binary chunks, with an idle timeout.</summary>
    private sealed class ChunkReader(Stream stream, TimeSpan idle)
    {
        private readonly byte[] _buf = new byte[64 * 1024];
        private int _pos, _len;

        private async Task<bool> FillAsync(CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(idle);
            try
            {
                _len = await stream.ReadAsync(_buf, cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new IOException($"no data from the game for {idle.TotalSeconds:0} s");
            }
            _pos = 0;
            return _len > 0;
        }

        public async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            var sb = new StringBuilder();
            while (true)
            {
                if (_pos >= _len && !await FillAsync(ct)) return sb.Length > 0 ? sb.ToString() : null;
                var b = _buf[_pos++];
                if (b == '\n') return sb.ToString();
                if (b != '\r') sb.Append((char)b);
                if (sb.Length > 4096) throw new IOException("protocol line too long");
            }
        }

        public async Task CopyExactlyAsync(Stream output, int count, CancellationToken ct)
        {
            while (count > 0)
            {
                if (_pos >= _len && !await FillAsync(ct)) throw new IOException("connection closed inside a dump chunk");
                var n = Math.Min(count, _len - _pos);
                await output.WriteAsync(_buf.AsMemory(_pos, n), ct);
                _pos += n;
                count -= n;
            }
        }
    }
}
