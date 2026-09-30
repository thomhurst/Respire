using Respire.Commands;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class KeySerializationCommandTests
{
    [Test]
    [Arguments(0, 0)]
    [Arguments(1, 0)]
    [Arguments(2, 0)]
    [Arguments(0, 1)]
    [Arguments(1, 1)]
    [Arguments(2, 1)]
    [Arguments(0, 2)]
    [Arguments(1, 2)]
    [Arguments(2, 2)]
    [Arguments(0, 3)]
    [Arguments(1, 3)]
    [Arguments(2, 3)]
    public async Task CommandsPreserveWireOptionsPrefixAndOwnedBinaryReplies(int mode, int optionCase)
    {
        byte[] binary = [0, 255, 128, 10];
        var bulk = "$4\r\n"u8.ToArray().Concat(binary).Concat("\r\n"u8.ToArray()).ToArray();
        byte[][] results = [bulk, "$-1\r\n"u8.ToArray(), FakeRespServer.OkReply];
        var replies = mode == 2
            ? new[] { FakeRespServer.OkReply }.Concat(Enumerable.Repeat("+QUEUED\r\n"u8.ToArray(), 3))
                .Append("*3\r\n"u8.ToArray().Concat(results.SelectMany(x => x)).ToArray()).ToArray()
            : results;
        await using var server = new FakeRespServer(replies);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var view = client.WithKeyPrefix("tenant:");
        RespireExpiry expiry = optionCase switch
        {
            1 => TimeSpan.FromMilliseconds(1234),
            2 => DateTimeOffset.FromUnixTimeMilliseconds(123456789),
            3 => RespireExpiry.Persist,
            _ => default
        };
        var options = optionCase switch
        {
            1 => new RespireRestoreOptions { Replace = true, IdleTimeSeconds = 42 },
            2 => new RespireRestoreOptions { Frequency = 255 },
            3 => new RespireRestoreOptions { IdleTimeSeconds = 0 },
            _ => default
        };
        byte[]? dump;
        byte[]? missing;
        bool restored;
        if (mode == 0)
        {
            dump = await view.Keys.DumpAsync("key");
            missing = await view.Keys.DumpAsync("missing");
            restored = await view.Keys.RestoreAsync("target", "payload"u8.ToArray(), expiry, options);
        }
        else
        {
            using var batch = mode == 1 ? view.CreateBatch() : null;
            await using var transaction = mode == 2 ? view.CreateTransaction() : null;
            IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
            var first = queue.Keys.Dump("key");
            var second = queue.Keys.Dump("missing");
            var third = queue.Keys.Restore("target", "payload"u8.ToArray(), expiry, options);
            if (transaction is not null) await transaction.CommitAsync(); else await batch!.ExecuteAsync();
            dump = first.Result;
            missing = second.Result;
            restored = third.Result;
        }
        await client.DisposeAsync();
        await Assert.That(dump!).IsEquivalentTo(binary, CollectionOrdering.Matching);
        await Assert.That(missing).IsNull();
        await Assert.That(restored).IsTrue();
        var wire = optionCase switch
        {
            1 => "RESTORE tenant:target 1234 payload REPLACE IDLETIME 42",
            2 => "RESTORE tenant:target 123456789 payload ABSTTL FREQ 255",
            3 => "RESTORE tenant:target 0 payload IDLETIME 0",
            _ => "RESTORE tenant:target 0 payload"
        };
        await Assert.That(server.ReceivedCommands.Where(c => c is not "MULTI" and not "EXEC"))
            .IsEquivalentTo(["DUMP tenant:key", "DUMP tenant:missing", wire], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(5)]
    [Arguments(6)]
    [Arguments(7)]
    public async Task InvalidOptionsFailBeforeSendOrQueue(int optionCase)
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var expiry = optionCase switch
        {
            0 => RespireExpiry.Keep,
            1 => RespireExpiry.In(TimeSpan.Zero),
            2 => RespireExpiry.In(TimeSpan.FromTicks(1)),
            3 => RespireExpiry.In(TimeSpan.FromMilliseconds(-1)),
            4 => RespireExpiry.At(DateTimeOffset.UnixEpoch),
            5 => RespireExpiry.At(DateTimeOffset.UnixEpoch.AddMilliseconds(-1)),
            _ => default
        };
        var options = optionCase switch
        {
            6 => new RespireRestoreOptions { IdleTimeSeconds = -1 },
            7 => new RespireRestoreOptions { IdleTimeSeconds = 0, Frequency = 0 },
            _ => default
        };
        await Assert.That(async () => await client.Keys.RestoreAsync("key", default, expiry, options))
            .Throws<ArgumentException>();
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        await Assert.That(() => batch.Keys.Restore("key", default, expiry, options)).Throws<ArgumentException>();
        await Assert.That(() => transaction.Keys.Restore("key", default, expiry, options)).Throws<ArgumentException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    public async Task InvalidRestoreDoesNotSelectTransactionSlot()
    {
        await using var client = RespireClient.Create(new RespireOptions { UseCluster = true, Endpoints = { new RespireEndpoint("localhost") } });
        await using var transaction = client.WithKeyPrefix("tenant:").CreateTransaction();
        await Assert.That(() => transaction.Keys.Restore("{a}:key", default, RespireExpiry.Keep)).Throws<ArgumentException>();
        _ = transaction.Keys.Dump("{b}:key");
        await Assert.That(() => transaction.Keys.Restore("{c}:key", default)).Throws<InvalidOperationException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CancellationReachesBothCommands(bool restore)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer { SuppressReply = _ => { received.TrySetResult(); return true; } };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        Task operation = restore
            ? client.Keys.RestoreAsync("key", "payload"u8.ToArray(), cancellationToken: cancellation.Token).AsTask()
            : client.Keys.DumpAsync("key", cancellation.Token).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.That(async () => await operation).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task RestoreInvalidatesOnlyItsKeyBeforeAndAfterReplyWhileDumpIsReadOnly()
    {
        var cache = new ClientSideCacheCoordinator(new RespireClientSideCacheOptions());
        Insert("key");
        Insert("unrelated");
        var dump = new Cmd1(RespireCommands.Key.DUMP.Verb, "key");
        var readFence = cache.BeforeCommand("DUMP", in dump);
        cache.CompleteMutation(in readFence);
        await Assert.That(cache.Count).IsEqualTo(2);
        var restore = new Cmd3(RespireCommands.Key.RESTORE.Verb, "key", 0, "payload");
        var fence = cache.BeforeCommand("RESTORE", in restore);
        await Assert.That(cache.Count).IsEqualTo(1);
        Insert("key");
        cache.CompleteMutation(in fence);
        await Assert.That(cache.Count).IsEqualTo(1);
        RespireKey unrelated = "unrelated";
        await Assert.That(cache.TryGet(in unrelated, out var retained)).IsTrue();
        retained.Dispose();

        void Insert(RespireKey key)
        {
            var token = cache.BeginRead(in key);
            var value = RespValue.BulkString("value"u8.ToArray());
            cache.CompleteRead(in token, in value, allowInsert: true);
        }
    }
}
