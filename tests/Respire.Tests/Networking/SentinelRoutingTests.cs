using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SentinelRoutingTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly byte[] PrimaryRole = "*3\r\n$6\r\nmaster\r\n:0\r\n*0\r\n"u8.ToArray();

    [Test]
    public async Task LazyClientDiscoversOnFirstOperationAndPrefixViewsShareThePrimary()
    {
        await using var primary = Primary();
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = RespireClient.Create(Options(sentinel.Port));
        await using var prefixed = client.WithKeyPrefix("tenant:");
        await Assert.That(sentinel.CommandsSeen).IsEqualTo(0);
        await Assert.That(primary.CommandsSeen).IsEqualTo(0);
        await Assert.That(client.IsConnected).IsFalse();
        await prefixed.SetAsync("key", "value").AsTask().WaitAsync(Limit);
        await Assert.That(client.Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", primary.Port));
        await Assert.That(prefixed.Endpoint).IsEqualTo(client.Endpoint);
        await Assert.That(client.IsConnected).IsTrue();
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["ROLE", "SET tenant:key value"]);
    }

    [Test]
    public async Task ReadOnlyRetiresStalePrimaryAndNextWriteUsesTheSameClientCore()
    {
        var rejectWrites = false;
        await using var oldPrimary = Primary((_, command) => command.StartsWith("SET ") && Volatile.Read(ref rejectWrites)
            ? "-READONLY You can't write against a read only replica.\r\n"u8.ToArray() : null);
        await using var promoted = Primary();
        var primaryPort = oldPrimary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref primaryPort));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await using var prefixed = client.WithKeyPrefix("tenant:");
        var core = client.Core;
        Volatile.Write(ref primaryPort, promoted.Port);
        Volatile.Write(ref rejectWrites, true);
        await Assert.That(async () => await prefixed.SetAsync("rejected", "value").AsTask().WaitAsync(Limit))
            .Throws<RespireServerException>();
        await prefixed.SetAsync("next", "value").AsTask().WaitAsync(Limit);
        await Assert.That(client.Core).IsSameReferenceAs(core);
        await Assert.That(client.Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", promoted.Port));
        await Assert.That(promoted.ReceivedCommands).IsEquivalentTo(["ROLE", "SET tenant:next value"]);
        await Assert.That(oldPrimary.ReceivedCommands.Count(command => command.StartsWith("SET "))).IsEqualTo(1);
    }

    [Test]
    public async Task DisconnectReResolvesWithoutReplayingAnAcceptedWrite()
    {
        await using var oldPrimary = Primary();
        oldPrimary.CloseConnectionAfterCommand = 2;
        await using var promoted = Primary();
        var primaryPort = oldPrimary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref primaryPort));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        Volatile.Write(ref primaryPort, promoted.Port);
        await Assert.That(async () => await client.IncrementAsync("ambiguous").AsTask().WaitAsync(Limit))
            .Throws<RespireConnectionException>();
        await client.SetAsync("next", "value").AsTask().WaitAsync(Limit);
        await Assert.That(oldPrimary.ReceivedCommands).IsEquivalentTo(["ROLE", "INCR ambiguous"]);
        await Assert.That(promoted.ReceivedCommands).IsEquivalentTo(["ROLE", "SET next value"]);
    }

    [Test]
    public async Task ConcurrentFirstWritesShareOneValidatedDiscovery()
    {
        await using var primary = Primary();
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = RespireClient.Create(Options(sentinel.Port));
        await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(index => client.SetAsync($"key:{index}", "value").AsTask())).WaitAsync(Limit);
        await Assert.That(sentinel.ReceivedCommands.Count(command => command.StartsWith("SENTINEL GET-MASTER"))).IsEqualTo(1);
        await Assert.That(primary.ReceivedCommands.Count(command => command == "ROLE")).IsEqualTo(1);
        await Assert.That(primary.ReceivedCommands.Count(command => command.StartsWith("SET "))).IsEqualTo(16);
    }

    [Test]
    public async Task PromotionDoesNotMoveAnExistingWatchedTransaction()
    {
        var rejectWrites = false;
        await using var oldPrimary = Primary((_, command) => command.StartsWith("SET ") && Volatile.Read(ref rejectWrites)
            ? "-READONLY replica\r\n"u8.ToArray() : null);
        await using var promoted = Primary();
        var primaryPort = oldPrimary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref primaryPort));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await using var watched = await client.CreateTransactionAsync(["watched"]);
        var queued = watched.Set("old transaction", "value");
        Volatile.Write(ref primaryPort, promoted.Port);
        Volatile.Write(ref rejectWrites, true);
        await Assert.That(async () => await client.SetAsync("trigger", "value")).Throws<RespireServerException>();
        await client.SetAsync("new generation", "value").AsTask().WaitAsync(Limit);
        await Assert.That(async () => await watched.CommitAsync().AsTask().WaitAsync(Limit)).Throws<RespireConnectionException>();
        await Assert.That(queued.Status).IsEqualTo(RespirePendingStatus.Faulted);
        await Assert.That(oldPrimary.ReceivedCommands.Any(command => command is "MULTI" or "EXEC")).IsFalse();
        await Assert.That(promoted.ReceivedCommands.Any(command => command.StartsWith("WATCH ") || command is "MULTI" or "EXEC")).IsFalse();
    }

    [Test]
    public async Task AcceptedBlockingOperationDrainsThroughItsOriginalPoolAfterPromotion()
    {
        var rejectWrites = false;
        await using var oldPrimary = Primary((_, command) =>
        {
            if (command.StartsWith("SET ") && Volatile.Read(ref rejectWrites)) return "-READONLY replica\r\n"u8.ToArray();
            return null;
        });
        oldPrimary.SuppressReply = command => command.StartsWith("BLPOP ");
        await using var promoted = Primary();
        var primaryPort = oldPrimary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref primaryPort));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        var originalPool = client.Core.DedicatedPool;
        var pending = client.Lists.LeftPopAsync("queue", waitFor: Timeout.InfiniteTimeSpan).AsTask();
        await WaitForCommandAsync(oldPrimary, "BLPOP ");
        var index = oldPrimary.ReceivedCommands.ToList().FindIndex(command => command.StartsWith("BLPOP "));
        var connectionId = oldPrimary.ReceivedConnectionIds[index];
        Volatile.Write(ref primaryPort, promoted.Port);
        Volatile.Write(ref rejectWrites, true);
        await Assert.That(async () => await client.SetAsync("trigger", "value")).Throws<RespireServerException>();
        await client.SetAsync("next", "value").AsTask().WaitAsync(Limit);
        await Assert.That(ReferenceEquals(originalPool, client.Core.DedicatedPool)).IsFalse();
        await Assert.That(pending.IsCompleted).IsFalse();
        await oldPrimary.SendRawAsync("*2\r\n$5\r\nqueue\r\n$5\r\nvalue\r\n"u8.ToArray(), connectionId);
        await Assert.That(await pending.WaitAsync(Limit)).IsEqualTo("value");
        await originalPool.RetireAsync().AsTask().WaitAsync(Limit);
        await Assert.That(originalPool.CaptureRetirementState().Borrowed).IsEqualTo(0);
        await Assert.That(promoted.ReceivedCommands.Any(command => command.StartsWith("BLPOP "))).IsFalse();
    }

    [Test]
    public async Task CancelledDiscoveryDoesNotPublishAndTheNextOperationCanConnect()
    {
        await using var primary = Primary();
        await using var sentinel = Sentinel(() => primary.Port);
        sentinel.SuppressReply = command => command.StartsWith("SENTINEL GET-MASTER");
        await using var client = RespireClient.Create(Options(sentinel.Port));
        using var cancellation = new CancellationTokenSource();
        var pending = client.SetAsync("cancelled", "value", cancellationToken: cancellation.Token).AsTask();
        await WaitForCommandAsync(sentinel, "SENTINEL GET-MASTER");
        cancellation.Cancel();
        await Assert.That(async () => await pending.WaitAsync(Limit)).Throws<OperationCanceledException>();
        await Assert.That(client.IsConnected).IsFalse();
        await Assert.That(primary.CommandsSeen).IsEqualTo(0);
        sentinel.SuppressReply = null;
        await client.SetAsync("next", "value").AsTask().WaitAsync(Limit);
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["ROLE", "SET next value"]);
    }

    [Test]
    public async Task DisposalCancelsUnpublishedRoleValidation()
    {
        await using var primary = Primary();
        primary.SuppressReply = command => command == "ROLE";
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = RespireClient.Create(Options(sentinel.Port));
        var pending = client.SetAsync("never", "value").AsTask();
        await WaitForCommandAsync(primary, "ROLE");
        await client.DisposeAsync().AsTask().WaitAsync(Limit);
        await Assert.That(async () => await pending.WaitAsync(Limit)).Throws<Exception>();
        await Assert.That(client.IsConnected).IsFalse();
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["ROLE"]);
    }

    [Test]
    public async Task AReplicaReplacementIsNeverPublishedForApplicationWrites()
    {
        var rejectWrites = false;
        await using var oldPrimary = Primary((_, command) => command.StartsWith("SET ") && Volatile.Read(ref rejectWrites)
            ? "-READONLY replica\r\n"u8.ToArray() : null);
        await using var replica = Primary((_, command) => command == "ROLE"
            ? "*1\r\n$5\r\nslave\r\n"u8.ToArray() : null);
        await using var promoted = Primary();
        var primaryPort = oldPrimary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref primaryPort));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        Volatile.Write(ref primaryPort, replica.Port);
        Volatile.Write(ref rejectWrites, true);
        await Assert.That(async () => await client.SetAsync("trigger", "value")).Throws<RespireServerException>();
        await Assert.That(async () => await client.SetAsync("never", "value").AsTask().WaitAsync(Limit))
            .Throws<RespireConnectionException>();
        await Assert.That(replica.ReceivedCommands).IsEquivalentTo(["ROLE"]);
        await Assert.That(client.IsConnected).IsFalse();
        Volatile.Write(ref primaryPort, promoted.Port);
        await client.SetAsync("next", "value").AsTask().WaitAsync(Limit);
        await Assert.That(client.Endpoint.Port).IsEqualTo(promoted.Port);
    }

    [Test]
    public async Task EndpointEventsAndFailoverCounterFollowValidatedPublication()
    {
        var rejectWrites = false;
        await using var oldPrimary = Primary((_, command) => command.StartsWith("SET ") && Volatile.Read(ref rejectWrites)
            ? "-READONLY replica\r\n"u8.ToArray() : null);
        await using var promoted = Primary();
        var primaryPort = oldPrimary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref primaryPort));
        await using var client = RespireClient.Create(Options(sentinel.Port));
        var events = new ConcurrentQueue<RespireConnectionStateChange>();
        client.ConnectionStateChanged += events.Enqueue;
        var counted = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "respire.sentinel.failover")
                meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "server.port" && Equals(tag.Value, promoted.Port)) counted.TrySetResult(value);
        });
        listener.Start();
        await client.SetAsync("first", "value").AsTask().WaitAsync(Limit);
        Volatile.Write(ref primaryPort, promoted.Port);
        Volatile.Write(ref rejectWrites, true);
        await Assert.That(async () => await client.SetAsync("trigger", "value")).Throws<RespireServerException>();
        await client.SetAsync("next", "value").AsTask().WaitAsync(Limit);
        await Assert.That(await counted.Task.WaitAsync(Limit)).IsEqualTo(1);
        var ordered = events.ToArray();
        var disconnected = Array.FindIndex(ordered, change => change.Endpoint.Port == oldPrimary.Port
            && change.State == RespireConnectionState.Disconnected);
        var connected = Array.FindIndex(ordered, change => change.Endpoint.Port == promoted.Port
            && change.State == RespireConnectionState.Connected);
        await Assert.That(disconnected).IsGreaterThanOrEqualTo(0);
        await Assert.That(connected).IsGreaterThan(disconnected);
    }

    [Test]
    public async Task PromotionInvalidatesCachedReadsBeforeTheNextGenerationServesThem()
    {
        var rejectWrites = false;
        await using var oldPrimary = Primary((_, command) => command switch
        {
            "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
            "GET key" => "$3\r\nold\r\n"u8.ToArray(),
            _ when command.StartsWith("SET ") && Volatile.Read(ref rejectWrites) => "-READONLY replica\r\n"u8.ToArray(),
            _ => null,
        });
        await using var promoted = Primary((_, command) => command switch
        {
            "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
            "GET key" => "$3\r\nnew\r\n"u8.ToArray(),
            _ => null,
        });
        var primaryPort = oldPrimary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref primaryPort));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port) with
        {
            Protocol = RespProtocol.Resp3, ClientSideCache = new(),
        });
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("old");
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("old");
        await Assert.That(oldPrimary.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
        Volatile.Write(ref primaryPort, promoted.Port);
        Volatile.Write(ref rejectWrites, true);
        await Assert.That(async () => await client.SetAsync("trigger", "value")).Throws<RespireServerException>();
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
        await Assert.That(await client.GetStringAsync("key").AsTask().WaitAsync(Limit)).IsEqualTo("new");
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("new");
        await Assert.That(promoted.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
    }

    [Test]
    public async Task SubscriptionMovesToTheValidatedPrimaryAndReportsTheDeliveryGap()
    {
        var rejectWrites = false;
        static byte[]? SubscriptionReply(string command) => command switch
        {
            "SUBSCRIBE events" => "*3\r\n$9\r\nsubscribe\r\n$6\r\nevents\r\n:1\r\n"u8.ToArray(),
            "UNSUBSCRIBE events" => "*3\r\n$11\r\nunsubscribe\r\n$6\r\nevents\r\n:0\r\n"u8.ToArray(),
            _ => null,
        };
        await using var oldPrimary = Primary((_, command) => command.StartsWith("SET ") && Volatile.Read(ref rejectWrites)
            ? "-READONLY replica\r\n"u8.ToArray() : SubscriptionReply(command));
        await using var promoted = Primary((_, command) => SubscriptionReply(command));
        var primaryPort = oldPrimary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref primaryPort));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await using var subscription = await client.SubscribeAsync("events");
        var gap = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        subscription.DeliveryGap += _ => gap.TrySetResult();
        Volatile.Write(ref primaryPort, promoted.Port);
        Volatile.Write(ref rejectWrites, true);
        await Assert.That(async () => await client.SetAsync("trigger", "value")).Throws<RespireServerException>();
        await WaitForCommandAsync(promoted, "SUBSCRIBE events");
        await gap.Task.WaitAsync(Limit);
        var index = promoted.ReceivedCommands.ToList().FindIndex(command => command == "SUBSCRIBE events");
        var connectionId = promoted.ReceivedConnectionIds[index];
        await promoted.SendRawAsync("*3\r\n$7\r\nmessage\r\n$6\r\nevents\r\n$5\r\nvalue\r\n"u8.ToArray(), connectionId);
        await using var reader = subscription.GetAsyncEnumerator();
        await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(Limit)).IsTrue();
        await Assert.That(Encoding.UTF8.GetString(reader.Current.Payload.Span)).IsEqualTo("value");
        await Assert.That(promoted.ReceivedCommands.Where((_, position) => promoted.ReceivedConnectionIds[position] == connectionId).First())
            .IsEqualTo("ROLE");
    }

    private static async Task WaitForCommandAsync(FakeRespServer server, string prefix)
    {
        using var timeout = new CancellationTokenSource(Limit);
        while (!server.ReceivedCommands.Any(command => command.StartsWith(prefix)))
            await Task.Delay(5, timeout.Token);
    }

    private static RespireOptions Options(int sentinelPort) => new()
    {
        Endpoints = [new("127.0.0.1", sentinelPort)], SentinelPrimaryName = "mymaster",
        Connections = 1, ConnectTimeout = Limit, CommandTimeout = Limit, Protocol = RespProtocol.Resp2,
    };

    private static FakeRespServer Primary(Func<int, string, byte[]?>? reply = null)
        => new(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (connection, command) => reply?.Invoke(connection, command)
                ?? (command == "ROLE" ? PrimaryRole : FakeRespServer.OkReply),
        };

    private static FakeRespServer Sentinel(Func<int> primaryPort)
        => new(8, "*0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR-BY-NAME ")
                ? AddressReply(primaryPort()) : "*0\r\n"u8.ToArray(),
        };

    private static byte[] AddressReply(int port)
        => Encoding.ASCII.GetBytes($"*2\r\n$9\r\n127.0.0.1\r\n${port.ToString().Length}\r\n{port}\r\n");
}
