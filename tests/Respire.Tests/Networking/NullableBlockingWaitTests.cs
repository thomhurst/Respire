using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class NullableBlockingWaitTests
{
    [Test]
    [Arguments("left", "BLPOP key {0}")]
    [Arguments("right-typed", "BRPOP key {0}")]
    [Arguments("move", "BLMOVE key destination LEFT RIGHT {0}")]
    [Arguments("move-many", "BLMOVEM key destination LEFT RIGHT {0} COUNT 1 OBO")]
    [Arguments("list-one", "BLPOP key {0}")]
    [Arguments("list-many", "BLMPOP {0} 1 key LEFT COUNT 1")]
    [Arguments("sorted-one", "BZPOPMIN key {0}")]
    [Arguments("sorted-one-typed", "BZPOPMIN key {0}")]
    [Arguments("sorted-many", "BZMPOP {0} 1 key MIN COUNT 1")]
    [Arguments("sorted-many-typed", "BZMPOP {0} 1 key MIN COUNT 1")]
    [Arguments("stream", "XREAD BLOCK {1} STREAMS key 0")]
    [Arguments("streams", "XREAD BLOCK {1} STREAMS key 0")]
    [Arguments("stream-options", "XREAD BLOCK {1} STREAMS key 0")]
    [Arguments("group", "XREADGROUP GROUP group consumer BLOCK {1} STREAMS key >")]
    public async Task WaitsEncodeFiniteZeroAndIndefiniteValues(string operation, string wireFormat)
    {
        foreach (var (wait, seconds, milliseconds) in new (TimeSpan?, string, string)[]
        {
            (null, "0", "0"),
            (TimeSpan.FromMilliseconds(250), "0.25", "250"),
            (TimeSpan.Zero, "0.001", "1"),
            (Timeout.InfiniteTimeSpan, "0", "0"),
        })
        {
            await using var server = new FakeRespServer(2, "*-1\r\n"u8.ToArray());
            await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Invoke(client, operation, wait, cancellation.Token);
            await Assert.That(server.ReceivedCommands.Single()).IsEqualTo(string.Format(wireFormat, seconds, milliseconds));
        }
    }

    [Test]
    [Arguments("left")]
    [Arguments("right-typed")]
    [Arguments("move")]
    [Arguments("move-many")]
    [Arguments("list-one")]
    [Arguments("list-many")]
    [Arguments("sorted-one")]
    [Arguments("sorted-one-typed")]
    [Arguments("sorted-many")]
    [Arguments("sorted-many-typed")]
    [Arguments("stream")]
    [Arguments("streams")]
    [Arguments("stream-options")]
    [Arguments("group")]
    public async Task NegativeWaitsFailBeforeSending(string operation)
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(() => Invoke(client, operation, TimeSpan.FromTicks(-1), default))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    [Arguments("left")]
    [Arguments("right-typed")]
    [Arguments("move")]
    [Arguments("move-many")]
    [Arguments("list-one")]
    [Arguments("list-many")]
    [Arguments("sorted-one")]
    [Arguments("sorted-one-typed")]
    [Arguments("sorted-many")]
    [Arguments("sorted-many-typed")]
    [Arguments("stream")]
    [Arguments("streams")]
    [Arguments("stream-options")]
    public async Task NegativeWaitsThrowSynchronouslyBeforeSending(string operation)
    {
        // ReadGroupOnceAsync has an existing async projection boundary; its validation is
        // covered by NegativeWaitsFailBeforeSending without promising a synchronous throw.
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        // A void delegate observes only errors thrown before the command returns its task.
        await Assert.That(() => { _ = Invoke(client, operation, TimeSpan.FromTicks(-1), default); })
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    [Arguments("left")]
    [Arguments("right-typed")]
    [Arguments("move")]
    [Arguments("move-many")]
    [Arguments("list-one")]
    [Arguments("list-many")]
    [Arguments("sorted-one")]
    [Arguments("sorted-one-typed")]
    [Arguments("sorted-many")]
    [Arguments("sorted-many-typed")]
    [Arguments("stream")]
    [Arguments("streams")]
    [Arguments("stream-options")]
    [Arguments("group")]
    public async Task NullWaitCanBeCancelledWithoutBlockingSharedTraffic(string operation)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(2, FakeRespServer.PongReply)
        {
            SuppressReply = command =>
            {
                if (command == "PING") return false;
                received.TrySetResult();
                return true;
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = Invoke(client, operation, null, cancellation.Token);
        await received.Task.WaitAsync(cancellation.Token);
        using var pong = await client.ExecuteAsync("PING", cancellationToken: cancellation.Token);
        await Assert.That(pong.AsString()).IsEqualTo("PONG");
        cancellation.Cancel();
        await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5))).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task OmittedWaitsRemainImmediate()
    {
        await using var server = new FakeRespServer("*-1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await client.Lists.LeftPopAsync("key");
        await client.Lists.RightPopAsync<string>("key");
        await client.Lists.MoveAsync("key", "destination");
        await client.Lists.MoveManyAsync("key", "destination");
        await client.Lists.PopManyAsync("key");
        await client.SortedSets.PopManyAsync(["key"]);
        await client.SortedSets.PopManyAsync<string>(["key"]);
        await client.Streams.ReadAsync("key");
        await client.Streams.ReadAsync([("key", RespireStreamId.Beginning)]);
        await client.Streams.ReadAsync(new RespireStreamReadOptions { Count = 1 }, "key");
        await client.Streams.ReadGroupOnceAsync("key", "group", "consumer");
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith('B') || command.Contains(" BLOCK ", StringComparison.Ordinal))).IsFalse();
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(11);
        await Assert.That(server.ReceivedConnectionIds.Distinct().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task QueuedStreamReadsRejectExplicitNullWaits()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        foreach (var queue in new IRespireCommandQueue[] { batch, transaction })
        {
            var options = new RespireStreamReadOptions { WaitFor = null };
            await Assert.That(() => queue.Streams.Read(options, "key")).Throws<ArgumentException>();
            await Assert.That(() => queue.Streams.ReadGroup("key", "group", "consumer", options)).Throws<ArgumentException>();
        }
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    [Arguments("omitted", "XREADGROUP GROUP group consumer BLOCK 5000 STREAMS key >")]
    [Arguments("null", "XREADGROUP GROUP group consumer BLOCK 0 STREAMS key >")]
    [Arguments("pending", "XREADGROUP GROUP group consumer STREAMS key 0")]
    public async Task GroupIteratorHonorsExplicitNullAndKeepsPendingReadsImmediate(string mode, string expected)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(2, "*-1\r\n"u8.ToArray())
        {
            SuppressReply = _ => { received.TrySetResult(); return mode != "pending"; },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var options = mode == "omitted" ? default : new RespireStreamReadOptions { WaitFor = null };
        await using var reader = client.Streams.ReadGroupAsync(options, "key", "group", "consumer",
            startAt: mode == "pending" ? RespireStreamId.Beginning : (RespireStreamId?)null,
            cancellationToken: cancellation.Token).GetAsyncEnumerator();
        var next = reader.MoveNextAsync().AsTask();
        await received.Task.WaitAsync(cancellation.Token);
        await Assert.That(server.ReceivedCommands.Single()).IsEqualTo(expected);
        if (mode == "pending")
            await Assert.That(await next.WaitAsync(cancellation.Token)).IsFalse();
        else
        {
            cancellation.Cancel();
            await Assert.That(async () => await next.WaitAsync(TimeSpan.FromSeconds(5))).Throws<OperationCanceledException>();
        }
    }

    // Keep this helper synchronous so it cannot turn immediate validation errors into failed tasks.
    private static Task Invoke(IRespireClient client, string operation, TimeSpan? waitFor, CancellationToken cancellationToken)
        => operation switch
        {
            "left" => client.Lists.LeftPopAsync("key", waitFor, cancellationToken).AsTask(),
            "right-typed" => client.Lists.RightPopAsync<string>("key", waitFor, cancellationToken).AsTask(),
            "move" => client.Lists.MoveAsync("key", "destination", waitFor, cancellationToken: cancellationToken).AsTask(),
            "move-many" => client.Lists.MoveManyAsync("key", "destination", waitFor, cancellationToken: cancellationToken).AsTask(),
            "list-one" => client.Lists.PopAsync("key", waitFor, cancellationToken: cancellationToken).AsTask(),
            "list-many" => client.Lists.PopManyAsync("key", waitFor, cancellationToken: cancellationToken).AsTask(),
            "sorted-one" => client.SortedSets.PopAsync("key", waitFor, false, cancellationToken).AsTask(),
            "sorted-one-typed" => client.SortedSets.PopAsync<string>("key", waitFor, false, cancellationToken).AsTask(),
            "sorted-many" => client.SortedSets.PopManyAsync("key", 1, false, waitFor, cancellationToken).AsTask(),
            "sorted-many-typed" => client.SortedSets.PopManyAsync<string>(["key"], waitFor, cancellationToken: cancellationToken).AsTask(),
            "stream" => client.Streams.ReadAsync("key", waitFor, cancellationToken: cancellationToken).AsTask(),
            "streams" => client.Streams.ReadAsync([("key", RespireStreamId.Beginning)], waitFor, cancellationToken: cancellationToken).AsTask(),
            "stream-options" => client.Streams.ReadAsync(new RespireStreamReadOptions { WaitFor = waitFor }, "key", cancellationToken: cancellationToken).AsTask(),
            "group" => client.Streams.ReadGroupOnceAsync("key", "group", "consumer", new RespireStreamReadOptions { WaitFor = waitFor }, cancellationToken: cancellationToken).AsTask(),
            _ => throw new ArgumentException("Unknown operation", nameof(operation)),
        };
}
