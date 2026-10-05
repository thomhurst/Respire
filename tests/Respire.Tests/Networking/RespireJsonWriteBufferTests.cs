using System.Text;
using System.Net.Sockets;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Respire.Json;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public partial class RespireJsonClientTests
{
    [Test]
    [Arguments("SET", false)]
    [Arguments("MERGE", false)]
    [Arguments("MSET", false)]
    [Arguments("SET", true)]
    [Arguments("MERGE", true)]
    [Arguments("MSET", true)]
    public async Task AbandonedJsonWriteKeepsItsFrameWhileSerializationBufferIsReused(string operation, bool expire)
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        PausedJsonWriteStream? transport = null;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Connections = 1,
            CommandTimeout = expire ? TimeSpan.FromSeconds(2) : null,
            TestingStreamFactory = async (host, port, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(host, port, token);
                    return transport = new PausedJsonWriteStream(new NetworkStream(socket, ownsSocket: true));
                }
                catch { socket.Dispose(); throw; }
            },
        });
        var json = new RespireJsonClient(client);
        var info = JsonWriteContext.Default.TextDocument;
        var first = new TextDocument(new string('a', 32768));
        var replacement = new TextDocument(new string('b', 32768));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        transport!.PauseWrites = true;
        var pending = Write(first, cancellation.Token);
        Task next;
        try
        {
            await transport.WriteStarted.Task.WaitAsync(timeout.Token);
            if (expire)
                await Assert.That(async () => await pending.WaitAsync(timeout.Token)).Throws<RespireTimeoutException>();
            else
            {
                cancellation.Cancel();
                await Assert.That(async () => await pending.WaitAsync(timeout.Token)).Throws<OperationCanceledException>();
            }
            // The first using scope has cleared and returned its bytes. Serialize another value
            // before allowing either frame onto the socket, exercising the same client's reuse.
            next = Write(replacement, timeout.Token);
        }
        finally
        {
            transport.ResumeWrites.TrySetResult();
        }
        await next.WaitAsync(timeout.Token);
        var frames = server.ReceivedArguments.Where(frame => Encoding.UTF8.GetString(frame[0]) == "JSON." + operation).ToArray();
        await Assert.That(frames.Length).IsEqualTo(2);
        var values = new[] { first, replacement };
        for (var index = 0; index < frames.Length; index++)
        {
            var expected = JsonSerializer.SerializeToUtf8Bytes(values[index], info);
            await Assert.That(frames[index][3].AsSpan().SequenceEqual(expected)).IsTrue();
            if (operation == "MSET")
                await Assert.That(frames[index][6].AsSpan().SequenceEqual(expected)).IsTrue();
        }

        Task Write(TextDocument value, CancellationToken token) => operation switch
        {
            "SET" => json.SetAsync("{same}:first", value, info, cancellationToken: token).AsTask(),
            "MERGE" => json.MergeAsync("{same}:first", value, info, cancellationToken: token).AsTask(),
            "MSET" => json.MultiSetAsync([new("{same}:first", value), new("{same}:second", value)], info, token).AsTask(),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
    }

    [Test]
    [Arguments("SET")]
    [Arguments("MERGE")]
    [Arguments("MSET")]
    public async Task ConnectionFaultKeepsInProgressJsonFrameUntilTransportCompletes(string operation)
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        PausedJsonWriteStream? transport = null;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, CommandTimeout = null,
            Endpoints = [new("127.0.0.1", server.Port)],
            TestingStreamFactory = async (host, port, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(host, port, token);
                    return transport = new PausedJsonWriteStream(new NetworkStream(socket, ownsSocket: true));
                }
                catch { socket.Dispose(); throw; }
            },
        });
        var json = new RespireJsonClient(client);
        var info = JsonWriteContext.Default.TextDocument;
        var first = new TextDocument(new string('a', 32768));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var paused = transport!;
        paused.PauseWrites = true;
        paused.HoldAfterConnectionFault = true;
        var pending = Write(first, deadline.Token);
        try
        {
            await paused.WriteStarted.Task.WaitAsync(deadline.Token);
            var originalFrame = paused.PendingWrite.ToArray();
            await Assert.That(originalFrame.AsSpan().IndexOf(JsonSerializer.SerializeToUtf8Bytes(first, info)) >= 0).IsTrue();
            server.CloseConnections();
            await Assert.That(async () => await pending.WaitAsync(deadline.Token)).Throws<RespireConnectionException>();
            // Serialize through the same client after its first rental was cleared. The canceled
            // token prevents sending on the dead connection, but serialization still precedes dispatch.
            await Assert.That(async () => await Write(new TextDocument(new string('b', 32768)), new CancellationToken(true)))
                .Throws<OperationCanceledException>();
            await Assert.That(paused.PendingWrite.Span.SequenceEqual(originalFrame)).IsTrue();
        }
        finally { paused.ResumeWrites.TrySetResult(); }

        Task Write(TextDocument value, CancellationToken token) => operation switch
        {
            "SET" => json.SetAsync("{same}:first", value, info, cancellationToken: token).AsTask(),
            "MERGE" => json.MergeAsync("{same}:first", value, info, cancellationToken: token).AsTask(),
            "MSET" => json.MultiSetAsync([new("{same}:first", value), new("{same}:second", value)], info, token).AsTask(),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
    }

    private sealed class PausedJsonWriteStream(Stream inner) : Stream
    {
        internal bool PauseWrites { get; set; }
        internal bool HoldAfterConnectionFault { get; set; }
        internal ReadOnlyMemory<byte> PendingWrite { get; private set; }
        internal TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ResumeWrites { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.ReadAsync(buffer, cancellationToken);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (PauseWrites)
            {
                if (HoldAfterConnectionFault)
                {
                    // Send a partial frame, then model an I/O operation that still owns its
                    // remaining memory while receive-side failure completes the response wait.
                    await inner.WriteAsync(buffer[..17], cancellationToken);
                    buffer = buffer[17..];
                }
                PendingWrite = buffer;
                WriteStarted.TrySetResult();
                if (HoldAfterConnectionFault) await ResumeWrites.Task;
                else await ResumeWrites.Task.WaitAsync(cancellationToken);
            }
            await inner.WriteAsync(buffer, cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    [Test]
    [Arguments(false, 4)]
    [Arguments(true, 4)]
    [Arguments(false, 129)]
    [Arguments(true, 129)]
    public async Task PooledWritesPreserveMetadataWriterOptionsAndGrowth(bool indented, int batchLength)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = indented,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            MaxDepth = 128,
#if NET9_0_OR_GREATER
            IndentCharacter = '\t',
            IndentSize = 1,
            NewLine = "\r\n",
#endif
        };
        var info = new JsonWriteContext(options).TextDocument;
        TextDocument[] values = [new("<one>"), new(new string('x', 16384)), new(new string('y', 98304)), new("last")];
        var expected = values.Select(value => JsonSerializer.SerializeToUtf8Bytes(value, info)).ToArray();
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await client.Json.SetAsync("set", values[1], info);
        await client.Json.MergeAsync("merge", values[0], info);
        var entries = Enumerable.Range(0, batchLength)
            .Select(index => new RespireJsonSetEntry<TextDocument>($"{{same}}:{index}", values[Math.Min(index, values.Length - 1)])).ToArray();
        await client.Json.MultiSetAsync(entries, info);
        var frames = server.ReceivedArguments.Where(frame => Encoding.UTF8.GetString(frame[0]).StartsWith("JSON.", StringComparison.Ordinal)).ToArray();
        await Assert.That(frames.Length).IsEqualTo(3);
        await Assert.That(frames[0][3]).IsEquivalentTo(expected[1]);
        await Assert.That(frames[1][3]).IsEquivalentTo(expected[0]);
        for (var index = 0; index < entries.Length; index++)
            await Assert.That(frames[2][index * 3 + 3]).IsEquivalentTo(expected[Math.Min(index, expected.Length - 1)]);
    }

    [Test]
    public async Task MultiSetSerializationFailureSendsNothingAfterEarlierValues()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var cycle = new Node();
        cycle.Next = cycle;
        RespireJsonSetEntry<Node>[] entries = [new("{same}:one", new Node()), new("{same}:two", cycle)];
        await Assert.That(() => JsonSerializer.SerializeToUtf8Bytes(cycle, JsonTestContext.Default.Node))
            .Throws<InvalidOperationException>();
        await Assert.That(async () => await client.Json.MultiSetAsync(entries, JsonTestContext.Default.Node))
            .Throws<InvalidOperationException>();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("JSON.MSET", StringComparison.Ordinal))).IsFalse();
        await client.Json.SetAsync("after-error", new Node(), JsonTestContext.Default.Node);
        await Assert.That(server.ReceivedCommands).Contains("JSON.SET after-error . {\"Next\":null}");
    }

    [Test]
    public async Task ConcurrentWritesAndChangedOptionsNeverShareActiveBuffers()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var json = new RespireJsonClient(client);
        var contexts = new[] { new JsonWriteContext(), new JsonWriteContext(new JsonSerializerOptions { WriteIndented = true }) };
        var values = Enumerable.Range(0, 16).Select(index => new TextDocument(new string((char)('a' + index), 4096))).ToArray();
        await Task.WhenAll(values.Select((value, index) => json.SetAsync($"key:{index}", value, contexts[index % 2].TextDocument).AsTask()));
        // Exercise reuse after concurrent returns, including an options mismatch.
        for (var index = 0; index < 2; index++)
            await json.SetAsync($"repeat:{index}", values[index], contexts[index].TextDocument);
        var frames = server.ReceivedArguments.Where(frame => Encoding.UTF8.GetString(frame[0]) == "JSON.SET").ToArray();
        await Assert.That(frames.Length).IsEqualTo(18);
        foreach (var frame in frames)
        {
            var key = Encoding.UTF8.GetString(frame[1]);
            var index = int.Parse(key[(key.IndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            await Assert.That(frame[3]).IsEquivalentTo(JsonSerializer.SerializeToUtf8Bytes(values[index], contexts[index % 2].TextDocument));
        }
    }

    private sealed record TextDocument(string Text);
    [JsonSerializable(typeof(TextDocument))]
    private sealed partial class JsonWriteContext : JsonSerializerContext;
}
