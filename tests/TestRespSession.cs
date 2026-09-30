using System.Buffers;
using System.Net.Sockets;
using System.Text;
using Respire.Protocol;

namespace Respire.TestSupport;

// A single owned wire session for server parity tests that need MULTI/WATCH affinity.
internal sealed class TestRespSession(Stream stream, TcpClient? socket = null) : IAsyncDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private byte[] _buffer = new byte[4096];
    private int _length;

    internal static async Task<TestRespSession> ConnectAsync(RespireOptions options)
    {
        var endpoint = options.Endpoints[0];
        TcpClient? socket = null;
        Stream stream;
        if (options.TestingStreamFactory is { } factory)
            stream = await factory(endpoint.Host, endpoint.Port, default);
        else
        {
            socket = new TcpClient();
            try
            {
                await socket.ConnectAsync(endpoint.Host, endpoint.Port).WaitAsync(Limit);
                stream = socket.GetStream();
            }
            catch { socket.Dispose(); throw; }
        }
        var session = new TestRespSession(stream, socket);
        try
        {
            using var hello = await session.CommandAsync("HELLO", ((int)options.Protocol).ToString());
            if (hello.IsError) throw new IOException(hello.AsString());
            if (options.Database != 0)
            {
                using var selected = await session.CommandAsync("SELECT", options.Database.ToString());
                if (selected.IsError) throw new IOException(selected.AsString());
            }
            return session;
        }
        catch { await session.DisposeAsync(); throw; }
    }

    internal Task<RespValue> CommandAsync(params string[] arguments)
        => CommandBytesAsync(arguments.Select(Encoding.UTF8.GetBytes).ToArray());

    internal async Task<RespValue> CommandBytesAsync(params byte[][] arguments)
    {
        await SendAsync(arguments);
        return await ReadAsync();
    }

    internal async Task SendAsync(params byte[][] arguments)
    {
        var writer = new ArrayBufferWriter<byte>();
        writer.Write(Encoding.ASCII.GetBytes($"*{arguments.Length}\r\n"));
        foreach (var argument in arguments)
        {
            writer.Write(Encoding.ASCII.GetBytes($"${argument.Length}\r\n"));
            writer.Write(argument);
            writer.Write("\r\n"u8);
        }
        await stream.WriteAsync(writer.WrittenMemory).AsTask().WaitAsync(Limit);
    }

    internal async Task<RespValue> ReadAsync()
    {
        using var deadline = new CancellationTokenSource(Limit);
        while (true)
        {
            var consumed = 0;
            var status = RespParser.TryParseValue(_buffer.AsSpan(0, _length), ref consumed, out var value);
            if (status == RespParseStatus.Done)
            {
                // The returned reply may reference its original payload storage.
                var remaining = new byte[Math.Max(4096, _length - consumed)];
                _buffer.AsSpan(consumed, _length - consumed).CopyTo(remaining);
                _length -= consumed;
                _buffer = remaining;
                return value;
            }
            if (status != RespParseStatus.NeedMoreData) throw new IOException("Invalid RESP response.");
            if (_length == _buffer.Length) Array.Resize(ref _buffer, _buffer.Length * 2);
            var read = await stream.ReadAsync(_buffer.AsMemory(_length), deadline.Token);
            if (read == 0) throw new EndOfStreamException();
            _length += read;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await stream.DisposeAsync(); }
        finally { socket?.Dispose(); }
    }
}
