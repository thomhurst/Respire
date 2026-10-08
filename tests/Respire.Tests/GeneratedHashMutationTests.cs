using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Core;

namespace Respire.Tests;

public class GeneratedHashMutationTests
{
    [Test]
    public async Task CreationChangesNullDeletionAndNoOpSendOnlyRequiredFields()
    {
        await using var server = new FakeRespServer(":3\r\n"u8.ToArray(), ":0\r\n"u8.ToArray(), ":0\r\n"u8.ToArray(), ":1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var tracker = StoredHashModelHashMapper.Track(client, "fixed");
        var first = new StoredHashModel("x", "name", 0, null, null);
        await tracker.UpdateAsync(first);
        await tracker.UpdateAsync(first);
        var second = first with { Count = 1, Note = "note" };
        await tracker.UpdateAsync(second);
        await tracker.UpdateAsync(second with { Note = null });
        await tracker.UpdateAsync(second with { Note = null });
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "HSET fixed Id x Name name Count 0", "HDEL fixed Note Age",
            "HSET fixed Count 1 Note note", "HDEL fixed Note",
        });
    }

    [Test]
    public async Task FailedRemovalRetainsBaselineForWholeDeltaRetry()
    {
        await using var server = new FakeRespServer(":0\r\n"u8.ToArray(), "-ERR removal failed\r\n"u8.ToArray(), ":0\r\n"u8.ToArray(), ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var first = new StoredHashModel("x", "name", 0, "note", null);
        var tracker = StoredHashModelHashMapper.Track(client, first);
        var next = first with { Count = 1, Note = null };
        await Assert.That(async () => await tracker.UpdateAsync(next)).Throws<RespireServerException>();
        await tracker.UpdateAsync(next);
        await tracker.UpdateAsync(next);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "HSET model:{x} Count 1", "HDEL model:{x} Note",
            "HSET model:{x} Count 1", "HDEL model:{x} Note",
        });
    }

    [Test]
    public async Task RevertingAfterPartialFailureStillResendsPossiblyAppliedFields()
    {
        await using var server = new FakeRespServer(":0\r\n"u8.ToArray(), "-ERR removal failed\r\n"u8.ToArray(), ":0\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var first = new StoredHashModel("x", "name", 0, "note", null);
        var tracker = StoredHashModelHashMapper.Track(client, first);
        await Assert.That(async () => await tracker.UpdateAsync(first with { Count = 1, Note = null })).Throws<RespireServerException>();
        await tracker.UpdateAsync(first);
        await tracker.UpdateAsync(first);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "HSET model:{x} Count 1", "HDEL model:{x} Note", "HSET model:{x} Count 0 Note note",
        });
    }

    [Test]
    public async Task ExpiryGroupsUseHSetExAndUnchangedFieldsKeepTheirExpiry()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var first = new ExpiringHashModel("x", "name", "token", 0);
        await ExpiringHashModelHashMapper.SetAsync(client, first);
        var tracker = ExpiringHashModelHashMapper.Track(client, first);
        await tracker.UpdateAsync(first);
        await tracker.UpdateAsync(first with { Token = "new" });
        await tracker.UpdateAsync(first with { Token = "new", Counter = null });
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "HSETEX expiring:x PX 10000 FIELDS 1 Token token",
            "HSETEX expiring:x PX 20000 FIELDS 1 Counter 0",
            "HSET expiring:x Id x Name name",
            "HSETEX expiring:x PX 10000 FIELDS 1 Token new",
            "HDEL expiring:x Counter",
        });
    }

    [Test]
    public async Task UnsupportedHSetExRefusesBeforeOrdinaryWrites()
    {
        await using var server = new FakeRespServer("-ERR unknown command 'HSETEX', with args beginning with: 'key'\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(async () => await ExpiringHashModelHashMapper.SetAsync(client, new ExpiringHashModel("x", "name", "token", null)))
            .Throws<NotSupportedException>();
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(":0\r\n")]
    [Arguments(":2\r\n")]
    [Arguments("+OK\r\n")]
    public async Task MalformedExpiryWriteRetainsRetryState(string reply)
    {
        await using var server = new FakeRespServer(System.Text.Encoding.UTF8.GetBytes(reply), ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var first = new ExpiringHashModel("x", "name", "old", null);
        var tracker = ExpiringHashModelHashMapper.Track(client, first);
        var next = first with { Token = "new" };
        await Assert.That(async () => await tracker.UpdateAsync(next)).Throws<RespireProtocolException>();
        await tracker.UpdateAsync(next);
        await tracker.UpdateAsync(next);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "HSETEX expiring:x PX 10000 FIELDS 1 Token new",
            "HSETEX expiring:x PX 10000 FIELDS 1 Token new",
        });
    }

    [Test]
    public async Task NullBaselineValuesAreNoOpAndPreCanceledUpdateDoesNotConnect()
    {
        await using var client = RespireClient.Create("localhost:1");
        var value = new NullableHashModel(null, null);
        var tracker = NullableHashModelHashMapper.Track(client, value);
        await tracker.UpdateAsync(value);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () => await tracker.UpdateAsync(value, cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(async () => await NullableHashModelHashMapper.SetWithExpiryAsync(client, (RespireHashExpiryMode)99, value)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task MissingExpiryFieldReportsStatusAndRetainsRetryState()
    {
        var commandInfo = "*1\r\n*6\r\n$8\r\nhpexpire\r\n:-6\r\n*0\r\n:1\r\n:1\r\n:1\r\n"u8.ToArray();
        var expiryWrites = 0;
        await using var server = new FakeRespServer(3, ":0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command switch
            {
                "COMMAND INFO HPEXPIRE" => commandInfo,
                _ when command.StartsWith("HPEXPIRE", StringComparison.Ordinal) =>
                    Interlocked.Increment(ref expiryWrites) == 1 ? "*1\r\n:-2\r\n"u8.ToArray() : "*1\r\n:1\r\n"u8.ToArray(),
                _ => null,
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var first = new ExpiringHashModel("x", "name", "old", null);
        var tracker = ExpiringHashModelHashMapper.Track(client, first, RespireHashExpiryMode.HSetThenExpire);
        var next = first with { Token = "new" };
        var error = await Assert.That(async () => await tracker.UpdateAsync(next)).Throws<InvalidOperationException>();
        await Assert.That(error!.Message).Contains("field 'Token' (status -2)");
        await tracker.UpdateAsync(first);
        await tracker.UpdateAsync(first);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "COMMAND INFO HPEXPIRE", "HSET expiring:x Token new", "HPEXPIRE expiring:x 10000 FIELDS 1 Token",
            "COMMAND INFO HPEXPIRE", "HSET expiring:x Token old", "HPEXPIRE expiring:x 10000 FIELDS 1 Token",
        });
    }

    [Test]
    [Arguments("*0\r\n")]
    [Arguments("*1\r\n+OK\r\n")]
    [Arguments("*1\r\n:42\r\n")]
    public async Task MalformedExpiryStatusStillReportsProtocolFailure(string reply)
    {
        await using var server = new FakeRespServer(2, ":0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command switch
            {
                "COMMAND INFO HPEXPIRE" => "*1\r\n*6\r\n$8\r\nhpexpire\r\n:-6\r\n*0\r\n:1\r\n:1\r\n:1\r\n"u8.ToArray(),
                _ when command.StartsWith("HPEXPIRE", StringComparison.Ordinal) => System.Text.Encoding.UTF8.GetBytes(reply),
                _ => null,
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(async () => await ExpiringHashModelHashMapper.SetWithExpiryAsync(client,
            RespireHashExpiryMode.HSetThenExpire, new ExpiringHashModel("x", "name", "token", null)))
            .Throws<RespireProtocolException>();
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    public async Task ConcurrentUpdateRejectedAndCancellationRetainsRetryState(int connections)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(connections, ":0\r\n"u8.ToArray())
        {
            SuppressReply = command => { received.TrySetResult(); return true; },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", server.Port)],
            Connections = connections,
        });
        var first = new StoredHashModel("x", "name", 0, null, null);
        var tracker = StoredHashModelHashMapper.Track(client, first);
        using var cancellation = new CancellationTokenSource();
        var next = first with { Count = 1 };
        var pending = tracker.UpdateAsync(next, cancellation.Token).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(async () => await tracker.UpdateAsync(next)).Throws<InvalidOperationException>();
        cancellation.Cancel();
        await Assert.That(pending.IsCompleted).IsFalse();
        await Assert.That(async () => await tracker.UpdateAsync(first)).Throws<InvalidOperationException>();
        server.SuppressReply = null;
        // Cancellation cannot release the tracker before the accepted write settles.
        await server.SendRawAsync(":0\r\n"u8.ToArray(), server.ReceivedConnectionIds.Single());
        await Assert.That(async () => await pending).Throws<OperationCanceledException>();
        await tracker.UpdateAsync(first);
        await tracker.UpdateAsync(first);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[] { "HSET model:{x} Count 1", "HSET model:{x} Count 0" });
    }

    [Test]
    public async Task TrackerOwnsBinaryKeyAndMutableModelBaseline()
    {
        await using var server = new FakeRespServer(":0\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        byte[] key = [0xff, 0, 0x80];
        var value = new MutableTrackedHashModel { Name = "old" };
        var tracker = MutableTrackedHashModelHashMapper.Track(client, key, value);
        Array.Fill(key, (byte)0);
        value.Name = "new";
        await tracker.UpdateAsync(value);
        await tracker.UpdateAsync(value);
        await Assert.That(server.ReceivedArguments.Single()[1]).IsEquivalentTo(new byte[] { 0xff, 0, 0x80 });
        await Assert.That(server.ReceivedArguments.Single()[3]).IsEquivalentTo("new"u8.ToArray());
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    public async Task CapabilityCheckTimeoutKeepsTrackerRetryable(int connections)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commandInfo = "*1\r\n*6\r\n$8\r\nhpexpire\r\n:-6\r\n*0\r\n:1\r\n:1\r\n:1\r\n"u8.ToArray();
        await using var server = new FakeRespServer(connections + 2, ":0\r\n"u8.ToArray())
        {
            SuppressReply = command => { received.TrySetResult(); return command == "COMMAND INFO HPEXPIRE"; },
            ReplyOverride = (_, command) => command switch
            {
                "COMMAND INFO HPEXPIRE" => commandInfo,
                _ when command.StartsWith("HPEXPIRE", StringComparison.Ordinal) => "*1\r\n:1\r\n"u8.ToArray(),
                _ => null,
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", server.Port)],
            Connections = connections,
            CommandTimeout = TimeSpan.FromMilliseconds(200),
        });
        var first = new ExpiringHashModel("x", "name", "old", null);
        var tracker = ExpiringHashModelHashMapper.Track(client, first, RespireHashExpiryMode.HSetThenExpire);
        var next = first with { Token = "new" };
        var pending = tracker.UpdateAsync(next).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(async () => await pending).Throws<RespireTimeoutException>();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[] { "COMMAND INFO HPEXPIRE" });

        await tracker.UpdateAsync(first);
        await tracker.UpdateAsync(first);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[] { "COMMAND INFO HPEXPIRE" });

        server.SuppressReply = null;
        // Fan-out disposes its dedicated inspection connection after the timeout.
        await tracker.UpdateAsync(next);
        await tracker.UpdateAsync(next);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "COMMAND INFO HPEXPIRE", "COMMAND INFO HPEXPIRE",
            "HSET expiring:x Token new", "HPEXPIRE expiring:x 10000 FIELDS 1 Token",
        });
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    public async Task UnsupportedCapabilityKeepsRevertedBaselineNoOp(int connections)
    {
        await using var server = new FakeRespServer(connections + 3, "*1\r\n$-1\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", server.Port)],
            Connections = connections,
        });
        var first = new ExpiringHashModel("x", "name", "old", null);
        var tracker = ExpiringHashModelHashMapper.Track(client, first, RespireHashExpiryMode.HSetThenExpire);
        await Assert.That(async () => await tracker.UpdateAsync(first with { Token = "new" })).Throws<NotSupportedException>();
        await tracker.UpdateAsync(first);
        await tracker.UpdateAsync(first);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[] { "COMMAND INFO HPEXPIRE" });
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    public async Task CommandTimeoutRejectsRetryWhileAcceptedWriteRemainsUnsettled(int connections)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(connections, ":0\r\n"u8.ToArray())
        {
            SuppressReply = _ => { received.TrySetResult(); return true; },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", server.Port)],
            Connections = connections,
            CommandTimeout = TimeSpan.FromMilliseconds(200),
        });
        var first = new StoredHashModel("x", "name", 0, null, null);
        var tracker = StoredHashModelHashMapper.Track(client, first);
        var pending = tracker.UpdateAsync(first with { Count = 1 }).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(async () => await pending).Throws<RespireTimeoutException>();
        await Assert.That(async () => await tracker.UpdateAsync(first)).Throws<InvalidOperationException>();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[] { "HSET model:{x} Count 1" });

        server.SuppressReply = null;
        await server.SendRawAsync(":0\r\n"u8.ToArray(), server.ReceivedConnectionIds.Single());
        // A late reply cannot make the old snapshot trustworthy again.
        await Assert.That(async () => await tracker.UpdateAsync(first)).Throws<InvalidOperationException>();
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(1);
    }
}

[RespireHash("mutable")]
public partial class MutableTrackedHashModel
{
    public string Name { get; set; } = "";
}
