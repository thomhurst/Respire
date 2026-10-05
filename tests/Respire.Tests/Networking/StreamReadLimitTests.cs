using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class StreamReadLimitTests
{
    [Test]
    [Arguments(false, 0)]
    [Arguments(false, 1)]
    [Arguments(false, 2)]
    [Arguments(true, 0)]
    [Arguments(true, 1)]
    [Arguments(true, 2)]
    public async Task LimitsAndOwnedKeysReachEverySurface(bool group, int surface)
    {
        var reply = StreamReadTests.Reply(false, ("tenant:{s}:one", ["1-0"]), ("tenant:{s}:two", ["2-0"]));
        byte[][] replies = surface == 2
            ? [FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), [.. "*1\r\n"u8, .. reply]] : [reply];
        await using var server = new FakeRespServer(replies);
        await using var owner = Create(server.Port);
        var client = owner.WithKeyPrefix("tenant:");
        var options = new StreamReadOptions { Count = 2, MaxCount = 3, MaxSize = 100 };
        byte[] key = "{s}:one"u8.ToArray();
        (RespireKey Key, RespireStreamId After)[] streams = [(key, group ? ">" : "0"), ("{s}:two", group ? ">" : "0")];
        RespireStreamReadResult[] result;
        if (surface == 0)
        {
            var pending = group ? client.Streams.ReadGroupAsync(streams, "g", "c", options)
                : client.Streams.ReadAsync(options, streams);
            Array.Fill(key, (byte)'x');
            result = await pending;
        }
        else
        {
            using var batch = surface == 1 ? client.CreateBatch() : null;
            await using var transaction = surface == 2 ? client.CreateTransaction() : null;
            IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
            var pending = group ? queue.Streams.ReadGroup(streams, "g", "c", options) : queue.Streams.Read(options, streams);
            Array.Fill(key, (byte)'x');
            if (transaction is not null) await transaction.CommitAsync();
            else await batch!.ExecuteAsync();
            result = pending.Result;
        }
        await Assert.That(result.Length).IsEqualTo(2);
        await Assert.That(result[0].Key).IsEqualTo((RespireKey)"{s}:one");
        await Assert.That(result[1].Entries[0].Id).IsEqualTo((RespireStreamId)"2-0");
        var verb = group ? "XREADGROUP GROUP g c" : "XREAD";
        var ids = group ? "> >" : "0 0";
        await Assert.That(server.ReceivedCommands).Contains($"{verb} COUNT 2 MAXCOUNT 3 MAXSIZE 100 STREAMS tenant:{{s}}:one tenant:{{s}}:two {ids}");
    }

    [Test]
    public async Task InvalidLimitsFailBeforeIoOrQueueing()
    {
        await using var server = new FakeRespServer();
        await using var client = Create(server.Port);
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        StreamReadOptions[] invalid = [new() { MaxCount = 0 }, new() { MaxSize = -1 }, new() { Count = 3, MaxCount = 2 }];
        foreach (var options in invalid)
        {
            await Assert.That(async () => await client.Streams.ReadAsync(options, "key")).Throws<ArgumentException>();
            await Assert.That(async () => await client.Streams.ReadGroupOnceAsync("key", "g", "c", options)).Throws<ArgumentException>();
            foreach (var queue in new IRespireCommandQueue[] { batch, transaction })
            {
                await Assert.That(() => queue.Streams.Read(options, "key")).Throws<ArgumentException>();
                await Assert.That(() => queue.Streams.ReadGroup("key", "g", "c", options)).Throws<ArgumentException>();
            }
        }
        await Assert.That(() => batch.Streams.Read(new StreamReadOptions { WaitFor = Timeout.InfiniteTimeSpan }, "key"))
            .Throws<ArgumentException>();
        await Assert.That(batch.Count).IsEqualTo(0);
        await Assert.That(transaction.Count).IsEqualTo(0);
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BlockingLimitsUseDedicatedConnectionAndCancellation(bool group)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            SuppressReply = _ => { received.TrySetResult(); return true; },
        };
        await using var client = Create(server.Port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var options = new StreamReadOptions { MaxCount = 2, MaxSize = 50, WaitFor = Timeout.InfiniteTimeSpan };
        async Task Read()
        {
            if (group) await client.Streams.ReadGroupOnceAsync("key", "g", "c", options, cancellationToken: cancel.Token);
            else await client.Streams.ReadAsync(options, "key", cancellationToken: cancel.Token);
        }
        var pending = Read();
        await received.Task.WaitAsync(timeout.Token);
        cancel.Cancel();
        await Assert.That(async () => await pending).Throws<OperationCanceledException>();
        await Assert.That(server.ReceivedCommands).Contains(group
            ? "XREADGROUP GROUP g c MAXCOUNT 2 MAXSIZE 50 BLOCK 0 STREAMS key >"
            : "XREAD MAXCOUNT 2 MAXSIZE 50 BLOCK 0 STREAMS key 0");
    }

    private static RespireClient Create(int port) => RespireClient.Create(new RespireOptions
    { Protocol = RespProtocol.Resp2, Connections = 1, Endpoints = [new("127.0.0.1", port)] });
}
