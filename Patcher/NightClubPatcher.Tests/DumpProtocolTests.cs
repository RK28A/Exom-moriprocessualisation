using System.Text;
using NightClubPatcher.Core.Remote;
using Xunit;

namespace NightClubPatcher.Tests;

public class DumpProtocolTests
{
    /// <summary>Reads what the "loader" sent, records what the client writes.</summary>
    private sealed class Duplex(byte[] fromServer) : Stream
    {
        private readonly MemoryStream _in = new(fromServer);
        public readonly MemoryStream Sent = new();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _in.Read(buffer, offset, Math.Min(count, 7)); // small reads
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Sent.Write(buffer, offset, count);
    }

    private static byte[] Server(params object[] parts)
    {
        var ms = new MemoryStream();
        foreach (var p in parts)
        {
            var b = p as byte[] ?? Encoding.UTF8.GetBytes((string)p);
            ms.Write(b);
        }
        return ms.ToArray();
    }

    [Fact]
    public async Task Chunks_and_progress_are_reassembled_exactly()
    {
        var chunk1 = Encoding.UTF8.GetBytes("// dump\nclass A\n{\n}\n");
        var chunk2 = new byte[] { 0x0a, (byte)'D', (byte)' ', (byte)'9', 0x0a, 0xff, 0x00 };  // binary-safe, looks like a header
        var s = new Duplex(Server("NCMK 1 0.3.0 com.mandegames.NightClubSimulater 1.1.0\n", "P 1 10 Assembly-CSharp\n",
            $"D {chunk1.Length}\n", chunk1, "P 10 10 done\n", $"D {chunk2.Length}\n", chunk2, "E OK 29 10 42\n"));
        var output = new MemoryStream();
        var progress = new List<(long, long, string)>();
        var r = await DumpProtocol.RunAsync(s, output, (d, t, w) => progress.Add((d, t, w)));
        Assert.True(r.Ok, r.Error);
        Assert.Equal("1.1.0", r.GameVersion);
        Assert.Equal(10, r.Classes);
        Assert.Equal(42, r.Methods);
        Assert.Equal(chunk1.Concat(chunk2).ToArray(), output.ToArray());
        Assert.Equal("DUMP\n", Encoding.ASCII.GetString(s.Sent.ToArray()));
        Assert.Equal((1L, 10L, "Assembly-CSharp"), progress[0]);
    }

    [Fact]
    public async Task Errors_are_reported()
    {
        var r = await DumpProtocol.RunAsync(new Duplex(Server("NCMK 1 0.3.0 pkg 1.1.0\n", "E ERR IL2CPP runtime not ready yet\n")),
            new MemoryStream(), (_, _, _) => { });
        Assert.False(r.Ok);
        Assert.Contains("not ready", r.Error);

        // adb forward accepts the connection even when nothing listens on @ncmk: no bytes at all.
        r = await DumpProtocol.RunAsync(new Duplex(Server()), new MemoryStream(), (_, _, _) => { });
        Assert.False(r.Ok);
        Assert.True(r.NoAnswer);

        r = await DumpProtocol.RunAsync(new Duplex(Server("HTTP/1.1 400\n")), new MemoryStream(), (_, _, _) => { });
        Assert.False(r.Ok);
        Assert.Contains("Unexpected answer", r.Error);

        r = await DumpProtocol.RunAsync(new Duplex(Server("NCMK 1 0.3.0 pkg 1.1.0\n", "D 100\n", "short")), new MemoryStream(), (_, _, _) => { })
            .ContinueWith(t => t.IsFaulted ? new DumpProtocolResult(false, t.Exception!.InnerException!.Message, null, 0, 0, 0) : t.Result);
        Assert.False(r.Ok);
    }
}
