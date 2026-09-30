using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class StreamReadTests
{
    [Test]
    [Arguments(false, 0)]
    [Arguments(true, 0)]
    [Arguments(false, 1)]
    [Arguments(true, 1)]
    [Arguments(false, 2)]
    [Arguments(true, 2)]
    public async Task ReadsOwnRepliesAndSnapshotKeysAcrossSurfaces(bool map, int surface)
    {
        var response = Reply(map, ("tenant:{a}:one", ["1-0"]), ("tenant:{a}:two", ["2-0"]));
        byte[][] replies = surface == 2
            ? [FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), [.. "*1\r\n"u8, .. response]] : [response];
        await using var server = new FakeRespServer(replies);
        await using var client = Create(server.Port);
        var view = client.WithKeyPrefix("tenant:");
        byte[] key = "{a}:one"u8.ToArray();
        (RespireKey Key, RespireStreamId After)[] streams = [(key, "0"), ("{a}:two", "1-0")];
        RespireStreamReadResult[] result;
        using var batch = surface == 1 ? view.CreateBatch() : null;
        await using var transaction = surface == 2 ? view.CreateTransaction() : null;
        if (surface == 0)
        {
            var pending = view.Streams.ReadAsync(streams, count: 2).AsTask();
            Array.Fill(key, (byte)'x');
            streams[1] = ("wrong", "99-0");
            result = await pending;
        }
        else
        {
            IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
            var pending = queue.Streams.Read(streams, count: 2);
            Array.Fill(key, (byte)'x');
            streams[1] = ("wrong", "99-0");
            if (transaction is not null) await transaction.CommitAsync();
            else await batch!.ExecuteAsync();
            result = pending.Result;
        }
        await client.DisposeAsync();
        await Assert.That(result.Select(x => x.Key.ToString())).IsEquivalentTo(["{a}:one", "{a}:two"], CollectionOrdering.Matching);
        await Assert.That(result[0].Entries[0].Id).IsEqualTo((RespireStreamId)"1-0");
        await Assert.That(result[1].Entries[0]["field"]!).IsEquivalentTo(new byte[] { 0xff, 0 }, CollectionOrdering.Matching);
        await Assert.That(server.ReceivedCommands).Contains("XREAD COUNT 2 STREAMS tenant:{a}:one tenant:{a}:two 0 1-0");
    }

    [Test]
    public async Task InvalidRequestsFailBeforeIoOrEnqueue()
    {
        await using var server = new FakeRespServer();
        await using var client = Create(server.Port, cluster: true);
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        await Assert.That(async () => await client.Streams.ReadAsync([])).Throws<ArgumentException>();
        await Assert.That(async () => await client.Streams.ReadAsync("key", count: 0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Streams.ReadAsync("key", waitFor: TimeSpan.FromSeconds(-2)))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Streams.ReadAsync("key", ">" )).Throws<ArgumentException>();
        await Assert.That(async () => await client.Streams.ReadAsync([("a", "0"), ("a", "0")])).Throws<ArgumentException>();
        await Assert.That(async () => await client.Streams.ReadAsync([("{a}:one", "0"), ("{b}:two", "0")]))
            .ThrowsExactly<RespireServerException>();
        foreach (var streams in new[] { batch.Streams, transaction.Streams })
        {
            await Assert.That(() => streams.Read([])).Throws<ArgumentException>();
            await Assert.That(() => streams.Read([("{a}:one", "0"), ("{b}:two", "0")])).ThrowsExactly<RespireServerException>();
        }
        await Assert.That(batch.Count).IsEqualTo(0);
        await Assert.That(transaction.Count).IsEqualTo(0);
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ClusterRoutesAllPrefixedKeysToTheirSharedSlot(bool blocking)
    {
        await using var first = new FakeRespServer();
        await using var second = new FakeRespServer(blocking ? 2 : 1,
            Reply(false, ("{foo}:one", ["1-0"]), ("{foo}:two", ["2-0"])));
        var topology = Encoding.ASCII.GetBytes(
            $"*2\r\n*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{first.Port}\r\n" +
            $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{second.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        await using var client = Create(seed.Port, cluster: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await client.WithKeyPrefix("{foo}:").Streams.ReadAsync(
            [("one", "0"), ("two", "0")], waitFor: blocking ? TimeSpan.FromSeconds(1) : null, cancellationToken: timeout.Token);
        await Assert.That(result.Select(x => x.Key.ToString())).IsEquivalentTo(["one", "two"], CollectionOrdering.Matching);
        await Assert.That(first.CommandsSeen).IsEqualTo(0);
        await Assert.That(second.ReceivedCommands).IsEquivalentTo(
            [blocking ? "XREAD BLOCK 1000 STREAMS {foo}:one {foo}:two 0 0" : "XREAD STREAMS {foo}:one {foo}:two 0 0"]);
    }

    [Test]
    [Arguments(0L, 1L)]
    [Arguments(1L, 1L)]
    [Arguments(10001L, 2L)]
    [Arguments(-10000L, 0L)]
    public async Task BlockingReadsEncodeRoundedMilliseconds(long ticks, long milliseconds)
    {
        await using var server = new FakeRespServer("*-1\r\n"u8.ToArray());
        await using var client = Create(server.Port);
        var result = await client.Streams.ReadAsync("key", waitFor: TimeSpan.FromTicks(ticks));
        await Assert.That(result).IsEmpty();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo([$"XREAD BLOCK {milliseconds} STREAMS key 0"]);
    }

    [Test]
    public async Task ReconnectKeepsIndependentCursorsAndDeliversBufferedEntriesFirst()
    {
        var reconnecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(2, Reply(false, ("one", ["1-0", "2-0"]), ("two", ["5-0"])))
        {
            CloseConnectionAfterCommand = 2,
        };
        server.SuppressReply = _ => { if (server.CommandsSeen < 3) return false; reconnecting.TrySetResult(); return true; };
        await using var client = Create(server.Port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reader = client.Streams.ReadAllAsync([("one", "0"), ("two", "0")], cancellationToken: timeout.Token).GetAsyncEnumerator();
        foreach (var id in new[] { "1-0", "2-0", "5-0" })
        {
            await Assert.That(await reader.MoveNextAsync()).IsTrue();
            await Assert.That(reader.Current.Entry.Id).IsEqualTo((RespireStreamId)id);
            await Assert.That(server.CommandsSeen).IsEqualTo(1);
        }
        var next = reader.MoveNextAsync().AsTask();
        await reconnecting.Task.WaitAsync(timeout.Token);
        await Assert.That(server.ReceivedCommands.Skip(1)).IsEquivalentTo(
            Enumerable.Repeat("XREAD COUNT 64 BLOCK 1000 STREAMS one two 2-0 5-0", 2), CollectionOrdering.Matching);
        await server.SendRawAsync(Reply(false, ("two", ["6-0"])), 1);
        await Assert.That(await next).IsTrue();
        await Assert.That(reader.Current.Key).IsEqualTo((RespireKey)"two");
        await Assert.That(reader.Current.Entry.Id).IsEqualTo((RespireStreamId)"6-0");
    }

    [Test]
    public async Task DollarIsResolvedOnceEvenWhenFirstReadDisconnects()
    {
        var reconnecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(3, Entries(["7-0"])) { CloseConnectionAfterCommand = 2 };
        server.SuppressReply = _ => { if (server.CommandsSeen < 3) return false; reconnecting.TrySetResult(); return true; };
        await using var client = Create(server.Port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reader = client.Streams.ReadAllAsync("events", RespireStreamId.New, cancellationToken: timeout.Token).GetAsyncEnumerator();
        var next = reader.MoveNextAsync().AsTask();
        await reconnecting.Task.WaitAsync(timeout.Token);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "XREVRANGE events + - COUNT 1", "XREAD COUNT 64 BLOCK 1000 STREAMS events 7-0", "XREAD COUNT 64 BLOCK 1000 STREAMS events 7-0",
        }, CollectionOrdering.Matching);
        await server.SendRawAsync(Reply(false, ("events", ["8-0"])), 2);
        await Assert.That(await next).IsTrue();
        await Assert.That(reader.Current.Id).IsEqualTo((RespireStreamId)"8-0");
    }

    [Test]
    public async Task PermissionFailuresEndEnumerationWithoutRetry()
    {
        await using var server = new FakeRespServer("-NOPERM denied\r\n"u8.ToArray());
        await using var client = Create(server.Port);
        await using var reader = client.Streams.ReadAllAsync("events").GetAsyncEnumerator();
        await Assert.That(async () => await reader.MoveNextAsync()).ThrowsExactly<RespireServerException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CancellationOrClientDisposalEndsPendingEnumeration(bool disposeClient)
    {
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer
        {
            SuppressReply = _ => { reading.TrySetResult(); return true; },
        };
        await using var client = Create(server.Port);
        using var cancel = new CancellationTokenSource();
        await using var reader = client.Streams.ReadAllAsync("events").GetAsyncEnumerator(cancel.Token);
        var next = reader.MoveNextAsync().AsTask();
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (disposeClient) await client.DisposeAsync();
        else cancel.Cancel();
        await Assert.That(async () => await next.WaitAsync(TimeSpan.FromSeconds(5))).Throws<Exception>();
        await Assert.That(next.IsCompleted).IsTrue();
        await Assert.That(server.CommandsSeen).IsEqualTo(1);
    }

    [Test]
    public async Task MissingDollarBaselineUsesZeroAndInitialFailureDoesNotRetry()
    {
        await using var server = new FakeRespServer(2, "*0\r\n"u8.ToArray());
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.SuppressReply = command =>
        {
            if (!command.StartsWith("XREAD ", StringComparison.Ordinal)) return false;
            reading.TrySetResult();
            return true;
        };
        await using var client = Create(server.Port);
        await using var reader = client.Streams.ReadAllAsync("events", RespireStreamId.New).GetAsyncEnumerator();
        var next = reader.MoveNextAsync().AsTask();
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(server.ReceivedCommands.Last()).IsEqualTo("XREAD COUNT 64 BLOCK 1000 STREAMS events 0");
        await server.SendRawAsync(Reply(false, ("events", ["1-0"])), 1);
        await Assert.That(await next).IsTrue();

        await using var failedServer = new FakeRespServer { CloseConnectionAfterCommand = 1 };
        await using var failedClient = Create(failedServer.Port);
        await using var failedReader = failedClient.Streams.ReadAllAsync("events", RespireStreamId.New).GetAsyncEnumerator();
        await Assert.That(async () => await failedReader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<RespireConnectionException>();
        await Assert.That(failedServer.CommandsSeen).IsEqualTo(1);
    }

    [Test]
    public async Task HandshakeAuthenticationFailureEndsEnumerationWithoutRetry()
    {
        await using var server = new FakeRespServer("-WRONGPASS denied\r\n"u8.ToArray());
        await using var client = RespireClient.Create(new RespireOptions
        {
            Connections = 1, Password = "test-secret", Endpoints = [new("127.0.0.1", server.Port)],
        });
        await using var reader = client.Streams.ReadAllAsync("events").GetAsyncEnumerator();
        var error = await Assert.That(async () => await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)))
            .ThrowsExactly<RespireConnectionException>();
        await Assert.That(error!.InnerException).IsTypeOf<RespireServerException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(1);
    }

    private static RespireClient Create(int port, bool cluster = false)
        => RespireClient.Create(new RespireOptions { Connections = 1, UseCluster = cluster, Endpoints = [new("127.0.0.1", port)] });

    internal static byte[] Reply(bool map, params (string Key, string[] Ids)[] streams)
    {
        var bytes = new List<byte>(Encoding.ASCII.GetBytes($"{(map ? '%' : '*')}{streams.Length}\r\n"));
        foreach (var stream in streams)
        {
            if (!map) bytes.AddRange("*2\r\n"u8.ToArray());
            bytes.AddRange(Encoding.UTF8.GetBytes($"${Encoding.UTF8.GetByteCount(stream.Key)}\r\n{stream.Key}\r\n"));
            bytes.AddRange(Entries(stream.Ids));
        }
        return bytes.ToArray();
    }

    internal static byte[] Entries(string[] ids)
    {
        var bytes = new List<byte>(Encoding.ASCII.GetBytes($"*{ids.Length}\r\n"));
        foreach (var id in ids)
        {
            bytes.AddRange(Encoding.ASCII.GetBytes($"*2\r\n${id.Length}\r\n{id}\r\n*2\r\n$5\r\nfield\r\n$2\r\n"));
            bytes.AddRange([0xff, 0, 13, 10]);
        }
        return bytes.ToArray();
    }
}
