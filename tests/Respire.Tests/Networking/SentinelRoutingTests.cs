using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using System.Threading.Channels;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SentinelRoutingTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly byte[] PrimaryRole = "*3\r\n$6\r\nmaster\r\n:0\r\n*0\r\n"u8.ToArray();

    [Test]
    [NotInParallel]
    public async Task EndpointSnapshotsNeverMixPublishedGenerations()
    {
        await using var client = RespireClient.Create(Options(26379));
        var router = client.Core.Sentinel!;
        await using var first = new Respire.Internal.SentinelRouter.Generation(router, client.Core,
            Options(26379) with { Endpoints = [new("first.invalid", 6379)] });
        await using var second = new Respire.Internal.SentinelRouter.Generation(router, client.Core,
            Options(26379) with { Endpoints = [new("second.invalid", 6380)] });
        var current = typeof(Respire.Internal.SentinelRouter).GetField("_current",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        // Isolate the atomic publication boundary without connecting to synthetic endpoints.
        current.SetValue(router, first);
        var stop = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = Task.Factory.StartNew(() =>
        {
            started.TrySetResult();
            while (Volatile.Read(ref stop) == 0)
            {
                current.SetValue(router, second);
                current.SetValue(router, first);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        RespireEndpoint? mixed = null;
        try
        {
            await started.Task.WaitAsync(Limit);
            for (var index = 0; index < 1_000_000; index++)
            {
                var endpoint = client.Endpoint;
                if (endpoint != first.Endpoint && endpoint != second.Endpoint)
                {
                    mixed = endpoint;
                    break;
                }
            }
        }
        finally
        {
            Volatile.Write(ref stop, 1);
            await publisher.WaitAsync(Limit);
            current.SetValue(router, null);
        }
        await Assert.That(mixed).IsNull();
    }

    [Test]
    [NotInParallel]
    public async Task RapidFailoversDoNotWaitForBlockedObserversAndDrainNotificationsInOrder()
    {
        const int handoffs = 12;
        static byte[]? Reply(int _, string command) => command.StartsWith("SET retire", StringComparison.Ordinal)
            ? "-READONLY replica\r\n"u8.ToArray() : null;
        await using var first = Primary(Reply);
        await using var second = Primary(Reply);
        var port = first.Port;
        await using var sentinel = new FakeRespServer(handoffs + 1, "*0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR-BY-NAME ")
                ? AddressReply(Volatile.Read(ref port)) : "*0\r\n"u8.ToArray(),
        };
        await using var client = RespireClient.Create(Options(sentinel.Port));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var endpoints = new ConcurrentQueue<int>();
        var measurements = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "respire.sentinel.failover")
                meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "server.port" && (Equals(tag.Value, first.Port) || Equals(tag.Value, second.Port)))
                    Interlocked.Add(ref measurements, value);
        });
        listener.Start();
        client.ConnectionStateChanged += change =>
        {
            if (change.State != RespireConnectionState.Connected) return;
            endpoints.Enqueue(change.Endpoint.Port);
            if (endpoints.Count == 1)
            {
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }
            if (endpoints.Count == handoffs + 1) drained.TrySetResult();
        };
        try
        {
            await client.SetAsync("initial", "value").AsTask().WaitAsync(Limit);
            await entered.Task.WaitAsync(Limit);
            for (var index = 1; index <= handoffs; index++)
            {
                Volatile.Write(ref port, index % 2 == 0 ? first.Port : second.Port);
                await Assert.That(async () => await client.SetAsync("retire", "value").AsTask().WaitAsync(Limit))
                    .Throws<RespireServerException>();
                await client.SetAsync("current", "value").AsTask().WaitAsync(Limit);
                await Assert.That(client.Endpoint.Port).IsEqualTo(port);
            }
            await Assert.That(endpoints.Count).IsEqualTo(1);
            await Assert.That(Interlocked.Read(ref measurements)).IsEqualTo(0L);
        }
        finally { release.TrySetResult(); }
        await drained.Task.WaitAsync(Limit);
        await Assert.That(endpoints.SequenceEqual(
            Enumerable.Range(0, handoffs + 1).Select(index => index % 2 == 0 ? first.Port : second.Port))).IsTrue();
        await Assert.That(Interlocked.Read(ref measurements)).IsEqualTo((long)handoffs);
    }

    [Test]
    public async Task RoleDemotionRetiresTheGenerationBeforeTheNextWrite()
    {
        var replica = false;
        await using var oldPrimary = Primary((_, command) => command == "ROLE" && Volatile.Read(ref replica)
            ? "*1\r\n$5\r\nslave\r\n"u8.ToArray() : null);
        await using var promoted = Primary();
        var primaryPort = oldPrimary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref primaryPort));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        Volatile.Write(ref primaryPort, promoted.Port);
        Volatile.Write(ref replica, true);
        using (var role = await client.ExecuteAsync($"ROLE"))
            await Assert.That(role[0].AsString()).IsEqualTo("slave");
        await Assert.That(client.IsConnected).IsFalse();
        await client.SetAsync("next", "value").AsTask().WaitAsync(Limit);
        await Assert.That(oldPrimary.ReceivedCommands).IsEquivalentTo(["ROLE", "ROLE"]);
        await Assert.That(promoted.ReceivedCommands).IsEquivalentTo(["ROLE", "SET next value"]);
    }

    [Test]
    public async Task RetirementRejectsAnUnacceptedWaiterAndDrainsAcceptedCommands()
    {
        var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acceptedCount = 0;
        await using var oldPrimary = Primary();
        oldPrimary.SuppressReply = command =>
        {
            if (command != "PING") return false;
            if (Interlocked.Increment(ref acceptedCount) == 4) full.TrySetResult();
            return true;
        };
        await using var promoted = Primary();
        var primaryPort = oldPrimary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref primaryPort));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port) with { MaxInflightCommands = 4 });
        var original = client.Core.Multiplexer.GetConnection();
        var accepted = Enumerable.Range(0, 4).Select(_ => original.SendAsync(
            new Respire.Commands.RawCommand(FakeRespServer.PingFrame), default, armCommandDeadline: false).AsTask()).ToArray();
        await full.Task.WaitAsync(Limit);
        // Pin this waiter to the full old connection before retirement. A public call can
        // still be in async route acquisition and legitimately select the replacement.
        var waiter = original.SendAsync(new Respire.Commands.Cmd2(Respire.Commands.Verbs.Set,
            "unaccepted", "value"), default, armCommandDeadline: false).AsTask();
        await Assert.That(waiter.IsCompleted).IsFalse();
        Volatile.Write(ref primaryPort, promoted.Port);
        await oldPrimary.SendRawAsync("-READONLY replica\r\n"u8.ToArray());
        using (var rejected = await accepted[0].WaitAsync(Limit)) await Assert.That(rejected.IsError).IsTrue();
        try
        {
            await Assert.That(async () => await waiter.WaitAsync(Limit)).Throws<RespireException>();
            await Assert.That(original.IsAcceptingCommands).IsFalse();
            await client.SetAsync("new", "value").AsTask().WaitAsync(Limit);
            await Assert.That(accepted.Skip(1).All(task => !task.IsCompleted)).IsTrue();
            await Assert.That(oldPrimary.ReceivedCommands).IsEquivalentTo(["ROLE", "PING", "PING", "PING", "PING"]);
            await Assert.That(promoted.ReceivedCommands).IsEquivalentTo(["ROLE", "SET new value"]);
        }
        finally
        {
            await oldPrimary.SendRawAsync("+PONG\r\n+PONG\r\n+PONG\r\n"u8.ToArray());
            foreach (var pending in accepted.Skip(1)) { using var reply = await pending.WaitAsync(Limit); }
        }
    }

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
    public async Task DisconnectRevalidatesTheSamePrimaryBeforeAcceptingNewWork()
    {
        await using var primary = Primary();
        primary.CloseConnectionAfterCommand = 2;
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        var original = client.Core.Sentinel!.Current!;
        await Assert.That(async () => await client.IncrementAsync("ambiguous").AsTask().WaitAsync(Limit))
            .Throws<RespireConnectionException>();
        primary.CloseConnectionAfterCommand = null;
        await client.SetAsync("next", "value").AsTask().WaitAsync(Limit);
        await Assert.That(original.IsRetired).IsTrue();
        await Assert.That(client.Core.Sentinel.Current).IsNotSameReferenceAs(original);
        await Assert.That(sentinel.ReceivedCommands.Count(command => command.StartsWith("SENTINEL GET-MASTER"))).IsEqualTo(2);
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["ROLE", "INCR ambiguous", "ROLE", "SET next value"]);
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
        await Assert.That(async () => await watched.CommitAsync().AsTask().WaitAsync(Limit)).Throws<RespireException>();
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
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += change =>
        {
            events.Enqueue(change);
            if (change.Endpoint.Port == promoted.Port && change.State == RespireConnectionState.Connected)
                published.TrySetResult();
        };
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
        await published.Task.WaitAsync(Limit);
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
        await Assert.That(reader.Current.Kind).IsEqualTo(RespireMessageKind.Gap);
        await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(Limit)).IsTrue();
        await Assert.That(reader.Current.Kind).IsEqualTo(RespireMessageKind.Message);
        await Assert.That(Encoding.UTF8.GetString(reader.Current.Payload.Span)).IsEqualTo("value");
        await Assert.That(promoted.ReceivedCommands.Where((_, position) => promoted.ReceivedConnectionIds[position] == connectionId).First())
            .IsEqualTo("ROLE");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NewBatchUsesThePromotedGenerationAndDurabilityKeepsOneSocket(bool durability)
    {
        var rejectWrites = false;
        await using var oldPrimary = Primary((_, command) => command.StartsWith("SET ") && Volatile.Read(ref rejectWrites)
            ? "-READONLY replica\r\n"u8.ToArray() : null);
        await using var promoted = Primary((_, command) => command.StartsWith("WAIT ") ? ":1\r\n"u8.ToArray() : null);
        var primaryPort = oldPrimary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref primaryPort));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        Volatile.Write(ref primaryPort, promoted.Port);
        Volatile.Write(ref rejectWrites, true);
        await Assert.That(async () => await client.SetAsync("trigger", "value")).Throws<RespireServerException>();
        using var batch = client.CreateBatch();
        var pending = batch.Set("batched", "value");
        if (durability)
            await Assert.That(await batch.ExecuteAndWaitForReplicationAsync(1, TimeSpan.FromSeconds(1)).AsTask().WaitAsync(Limit))
                .IsEqualTo(1);
        else
            await batch.ExecuteAsync().AsTask().WaitAsync(Limit);
        await Assert.That(pending.Result).IsTrue();
        await Assert.That(oldPrimary.ReceivedCommands.Any(command => command == "SET batched value")).IsFalse();
        var index = promoted.ReceivedCommands.ToList().IndexOf("SET batched value");
        await Assert.That(index).IsGreaterThanOrEqualTo(0);
        var connection = promoted.ReceivedConnectionIds[index];
        var frames = promoted.ReceivedCommands.Where((_, position) => promoted.ReceivedConnectionIds[position] == connection).ToArray();
        string[] expected = durability
            ? ["ROLE", "SET batched value", "WAIT 1 1000"] : ["ROLE", "SET batched value"];
        await Assert.That(frames).IsEquivalentTo(expected);
    }

    [Test]
    public async Task TrackedCorrectionRetainsItsOriginalPeerAfterPromotion()
    {
        var rejectWrites = false;
        static byte[]? ScriptReply(string command, int clientId)
            => command == "CLIENT ID" ? Encoding.ASCII.GetBytes($":{clientId}\r\n")
                : command.StartsWith("CLIENT KILL ") ? ":0\r\n"u8.ToArray()
                : command.StartsWith("EVAL") ? ":1\r\n"u8.ToArray() : null;
        await using var oldPrimary = Primary((_, command) => command.StartsWith("SET ") && Volatile.Read(ref rejectWrites)
            ? "-READONLY replica\r\n"u8.ToArray() : ScriptReply(command, 41));
        await using var promoted = Primary((_, command) => ScriptReply(command, 42));
        var primaryPort = oldPrimary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref primaryPort));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        var script = RespireScript.Create("return 1");
        var execution = await client.StartTrackedScriptExecutionAsync(script, ["key"], [], default, true);
        using (var reply = await execution.Response) await Assert.That(reply.AsInteger()).IsEqualTo(1);
        var original = execution.ConnectionIdentity;
        await Assert.That(original.ServerClientId).IsEqualTo(41);
        Volatile.Write(ref primaryPort, promoted.Port);
        Volatile.Write(ref rejectWrites, true);
        await Assert.That(async () => await client.SetAsync("trigger", "value")).Throws<RespireServerException>();
        await client.EnsureReliableCorrectionOrderingAsync().AsTask().WaitAsync(Limit);
        var next = await client.StartTrackedScriptExecutionAsync(script, ["next"], [], default, true);
        using (var reply = await next.Response) await Assert.That(reply.AsInteger()).IsEqualTo(1);
        await Assert.That(next.ConnectionIdentity.ServerClientId).IsEqualTo(42);
        await Assert.That(next.ConnectionIdentity.Endpoint.Port).IsEqualTo(promoted.Port);
        await client.ExecuteOnAllConnectionsAsync(script, ["key"], [], original).AsTask().WaitAsync(Limit);
        await Assert.That(oldPrimary.ReceivedCommands).Contains("EVAL return 1 1 key");
        await Assert.That(promoted.ReceivedCommands.Any(command => command == "EVAL return 1 1 key")).IsFalse();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task ManagedLockInitializesTheSelectedGenerationAfterPromotion(bool release, bool rejectIdentity)
    {
        var rejectWrites = false;
        static byte[]? IdentityReply(string command, int clientId)
            => command == "CLIENT ID" ? Encoding.ASCII.GetBytes($":{clientId}\r\n")
                : command.StartsWith("CLIENT KILL ") ? ":0\r\n"u8.ToArray() : null;
        await using var oldPrimary = Primary((_, command) => command.StartsWith("SET ") && Volatile.Read(ref rejectWrites)
            ? "-READONLY replica\r\n"u8.ToArray() : IdentityReply(command, 41));
        await using var promoted = Primary((_, command) => command == "CLIENT ID" && rejectIdentity
            ? "-ERR identity unavailable\r\n"u8.ToArray()
            : command.StartsWith("DELEX ") ? ":1\r\n"u8.ToArray() : IdentityReply(command, 42));
        var primaryPort = oldPrimary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref primaryPort));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await client.EnsureReliableCorrectionOrderingAsync().AsTask().WaitAsync(Limit);
        await Assert.That(oldPrimary.ReceivedCommands).Contains("CLIENT ID");

        // Fail over after the managed-lock preflight, before selecting its command connection.
        Volatile.Write(ref primaryPort, promoted.Port);
        Volatile.Write(ref rejectWrites, true);
        await Assert.That(async () => await client.SetAsync("trigger", "value")).Throws<RespireServerException>();
        if (rejectIdentity)
        {
            await Assert.That(async () =>
            {
                var execution = await client.StartLockExecutionAsync("key", "token", release ? null : 1000, true, default);
                await execution.Response;
            }).ThrowsExactly<RespireServerException>();
            await Assert.That(promoted.ReceivedCommands.Any(IsLockMutation)).IsFalse();
        }
        else
        {
            var execution = await client.StartLockExecutionAsync("key", "token", release ? null : 1000, true, default);
            await Assert.That(await execution.Response).IsTrue();
            await Assert.That(execution.ConnectionIdentity.ServerClientId).IsEqualTo(42);
            await Assert.That(execution.ConnectionIdentity.Endpoint.Port).IsEqualTo(promoted.Port);
            var commands = promoted.ReceivedCommands.ToList();
            await Assert.That(commands.IndexOf("CLIENT ID")).IsLessThan(commands.FindIndex(IsLockMutation));
        }
        await Assert.That(oldPrimary.ReceivedCommands.Any(IsLockMutation)).IsFalse();

        static bool IsLockMutation(string command) => command.StartsWith("SET key ") || command.StartsWith("DELEX key ");
    }

    [Test]
    public async Task CapturedServerPoolRemainsPinnedAfterPromotion()
    {
        var rejectWrites = false;
        await using var oldPrimary = Primary((_, command) => command == "PING" ? FakeRespServer.PongReply
            : command.StartsWith("SET ") && Volatile.Read(ref rejectWrites) ? "-READONLY replica\r\n"u8.ToArray() : null);
        await using var promoted = Primary();
        var primaryPort = oldPrimary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref primaryPort));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        var pool = client.Core.CreateServerPool(client.Endpoint);
        try
        {
            var connection = await pool.RentAsync(default);
            try
            {
                Volatile.Write(ref primaryPort, promoted.Port);
                Volatile.Write(ref rejectWrites, true);
                await Assert.That(async () => await client.SetAsync("trigger", "value")).Throws<RespireServerException>();
                await client.SetAsync("next", "value").AsTask().WaitAsync(Limit);
                using var reply = await connection.SendAsync(new Respire.Commands.RawCommand(FakeRespServer.PingFrame));
                await Assert.That(reply.AsString()).IsEqualTo("PONG");
                await Assert.That(connection.Port).IsEqualTo(oldPrimary.Port);
                await Assert.That(promoted.ReceivedCommands.Any(command => command == "PING")).IsFalse();
            }
            finally { pool.Return(connection); }
        }
        finally { await client.Core.ReleaseServerPoolAsync(pool); }
    }

    [Test]
    public async Task DisposalJoinsRetirementWithAnAcceptedBlockingCommand()
    {
        var rejectWrites = false;
        await using var oldPrimary = Primary((_, command) => command.StartsWith("SET ") && Volatile.Read(ref rejectWrites)
            ? "-READONLY replica\r\n"u8.ToArray() : null);
        oldPrimary.SuppressReply = command => command.StartsWith("BLPOP ");
        await using var promoted = Primary();
        var primaryPort = oldPrimary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref primaryPort));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        var original = client.Core.Sentinel!.Current!;
        var blocking = client.Lists.LeftPopAsync("queue", waitFor: Timeout.InfiniteTimeSpan).AsTask();
        await WaitForCommandAsync(oldPrimary, "BLPOP ");
        Volatile.Write(ref primaryPort, promoted.Port);
        Volatile.Write(ref rejectWrites, true);
        await Assert.That(async () => await client.SetAsync("trigger", "value")).Throws<RespireServerException>();
        await client.SetAsync("next", "value").AsTask().WaitAsync(Limit);
        await Assert.That(original.Retirement.IsCompleted).IsFalse();
        await client.DisposeAsync().AsTask().WaitAsync(Limit);
        await Assert.That(async () => await blocking.WaitAsync(Limit)).Throws<Exception>();
        await Assert.That(original.Retirement.IsCompleted).IsTrue();
        await Assert.That(client.IsConnected).IsFalse();
        await Assert.That(promoted.ReceivedCommands.Any(command => command.StartsWith("BLPOP "))).IsFalse();
    }

    [Test]
    public async Task FailedUnpublishedCandidateDoesNotFlushThePublishedCache()
    {
        static byte[]? Hello(string command) => command == "HELLO 3"
            ? "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray() : null;
        await using var primary = Primary((_, command) => command == "GET key"
            ? "$5\r\nvalue\r\n"u8.ToArray() : Hello(command));
        await using var replica = Primary((_, command) => command == "ROLE"
            ? "*1\r\n$5\r\nslave\r\n"u8.ToArray() : Hello(command));
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port) with
        {
            Protocol = RespProtocol.Resp3, ClientSideCache = new(),
        });
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(1);
        var current = client.Core.Sentinel!.Current;
        // Exercise an unpublished candidate while the published generation remains healthy.
        // The discovery owner disposes this candidate after ROLE rejects it.
        await using var candidate = new Respire.Internal.SentinelRouter.Generation(client.Core.Sentinel, client.Core,
            Options(sentinel.Port) with { Endpoints = [new("127.0.0.1", replica.Port)], SentinelPrimaryName = null,
                Protocol = RespProtocol.Resp3 });
        await Assert.That(async () => await candidate.Multiplexer.EnsureConnectedAsync(default))
            .Throws<RespireConnectionException>();
        await Assert.That(candidate.IsRetired).IsTrue();
        await Assert.That(candidate.Retirement).IsSameReferenceAs(Task.CompletedTask);
        await Assert.That(client.Core.Sentinel.Current).IsSameReferenceAs(current);
        await Assert.That(client.IsConnected).IsTrue();
        await Assert.That(client.ClientSideCache.Count).IsEqualTo(1);
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(primary.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
    }

    [Test]
    [Arguments("EXEC")]
    [Arguments("EVAL")]
    [Arguments("EVALSHA")]
    [Arguments("EVAL_RO")]
    [Arguments("EVALSHA_RO")]
    [Arguments("FCALL")]
    [Arguments("FCALL_RO")]
    public async Task NestedReadOnlyInHeterogeneousRepliesRetiresTheGeneration(string operation)
    {
        await using var primary = Primary((_, command) => command == operation
            ? "*1\r\n*1\r\n-READONLY replica\r\n"u8.ToArray() : null);
        await using var promoted = Primary();
        var primaryPort = primary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref primaryPort));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        Volatile.Write(ref primaryPort, promoted.Port);
        using (var response = await client.ExecuteAsync((RespireCommand)operation, []))
            await Assert.That(response[0][0].IsError).IsTrue();
        await Assert.That(client.IsConnected).IsFalse();
        await client.SetAsync("next", "value").AsTask().WaitAsync(Limit);
        await Assert.That(promoted.ReceivedCommands).IsEquivalentTo(["ROLE", "SET next value"]);
    }

    [Test]
    [Arguments("%1\r\n+k\r\n-READONLY replica\r\n", true)]
    [Arguments("~1\r\n-READONLY replica\r\n", true)]
    [Arguments("*2\r\n:1\r\n%1\r\n+k\r\n~1\r\n-READONLY replica\r\n", true)]
    [Arguments("%1\r\n+k\r\n~2\r\n:1\r\n:2\r\n", false)]
    public async Task ReadOnlyTraversalIncludesResp3MapsAndSets(string reply, bool readOnly)
    {
        await using var primary = Primary((_, command) => command switch
        {
            "HELLO 3" => "%1\r\n+proto\r\n:3\r\n"u8.ToArray(),
            "EVAL" => Encoding.ASCII.GetBytes(reply),
            _ => null,
        });
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port) with { Protocol = RespProtocol.Resp3 });
        using var response = await client.ExecuteAsync((RespireCommand)"EVAL", []);
        await Assert.That(client.IsConnected).IsEqualTo(!readOnly);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NestedReadOnlyScanResumesAfterDeepNonErrorArrays(bool readOnly)
    {
        // Every level has a scalar sibling before and after its child. The final error
        // requires returning through all pending parents rather than stopping at a scalar.
        var reply = "*3\r\n:0\r\n" + string.Concat(Enumerable.Repeat("*3\r\n:1\r\n", 256))
            + ":2\r\n" + string.Concat(Enumerable.Repeat(":3\r\n", 256))
            + (readOnly ? "-READONLY replica\r\n" : ":4\r\n");
        await using var primary = Primary((_, command) => command == "EVAL" ? Encoding.ASCII.GetBytes(reply) : null);
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        using var response = await client.ExecuteAsync((RespireCommand)"EVAL", []);
        await Assert.That(client.IsConnected).IsEqualTo(!readOnly);
    }

    [Test]
    [Arguments("GET")]
    [Arguments("MGET")]
    [Arguments("HGET")]
    public async Task RetirementDuringCacheLookupRejectsTheOldValue(string operation)
    {
        static byte[]? Reply(string command, string value) => command switch
        {
            "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
            "GET key" or "HGET key field" => Encoding.ASCII.GetBytes($"$3\r\n{value}\r\n"),
            "MGET key" => Encoding.ASCII.GetBytes($"*1\r\n$3\r\n{value}\r\n"),
            _ => null,
        };
        await using var primary = Primary((_, command) => Reply(command, "old"));
        await using var promoted = Primary((_, command) => Reply(command, "new"));
        var port = primary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port) with
        {
            Protocol = RespProtocol.Resp3, ClientSideCache = new(),
        });
        await Assert.That(await ReadAsync()).IsEqualTo("old");
        var generation = client.Core.Sentinel!.Current!;
        var connection = generation.Multiplexer.GetConnection();
        var intercept = new AsyncLocal<bool>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "respire.client_cache.hits")
                meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) =>
        {
            if (!intercept.Value) return;
            intercept.Value = false;
            Volatile.Write(ref port, promoted.Port);
            using var error = Respire.Protocol.RespValue.Error("READONLY replica");
            generation.ObserveResponse(connection, "SET", in error);
        });
        listener.Start();
        intercept.Value = true;
        await Assert.That(await ReadAsync().WaitAsync(Limit)).IsEqualTo("new");
        await Assert.That(generation.IsRetired).IsTrue();
        await Assert.That(promoted.ReceivedCommands.Any(command => command.StartsWith(operation + " "))).IsTrue();

        async Task<string?> ReadAsync()
        {
            if (operation == "GET") return await client.GetStringAsync("key");
            if (operation == "MGET") return (await client.Strings.GetManyAsync(["key"]))[0];
            using var result = await client.ExecuteAsync((RespireCommand)"HGET", ["key", "field"]);
            return result.AsString();
        }
    }

    [Test]
    [Arguments("batch")]
    [Arguments("durability")]
    [Arguments("transaction")]
    [Arguments("blocking")]
    [Arguments("script")]
    public async Task FirstLazyOperationIsSampledWithThePrimaryEndpoint(string kind)
    {
        await using var primary = Primary((_, command) => command switch
        {
            "SET key value" when kind == "transaction" => "+QUEUED\r\n"u8.ToArray(),
            "EXEC" => "*1\r\n+OK\r\n"u8.ToArray(),
            "WAIT 1 1000" => ":1\r\n"u8.ToArray(),
            "BLPOP key 0" => "*2\r\n$3\r\nkey\r\n$5\r\nvalue\r\n"u8.ToArray(),
            _ when command.StartsWith("EVALSHA ") => ":1\r\n"u8.ToArray(),
            _ => null,
        });
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = RespireClient.Create(Options(sentinel.Port));
        var samples = new ConcurrentQueue<Dictionary<string, object?>>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Respire",
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
            {
                if (options.Name == "SET" || options.Name == "BLPOP" || options.Name.StartsWith("EVALSHA")) samples.Enqueue(options.Tags!.ToDictionary(tag => tag.Key, tag => tag.Value));
                return ActivitySamplingResult.AllDataAndRecorded;
            },
        };
        ActivitySource.AddActivityListener(listener);
        if (kind == "blocking")
            await client.Lists.LeftPopAsync("key", waitFor: Timeout.InfiniteTimeSpan).AsTask().WaitAsync(Limit);
        else if (kind == "script")
        {
            using var result = await client.Scripts.ExecuteAsync(RespireScript.Create("return 1"));
            await Assert.That(result.AsInteger()).IsEqualTo(1);
        }
        else if (kind == "transaction")
        {
            await using var transaction = client.CreateTransaction();
            _ = transaction.Set("key", "value");
            await transaction.CommitAsync();
        }
        else
        {
            using var batch = client.CreateBatch();
            _ = batch.Set("key", "value");
            if (kind == "durability") await batch.ExecuteAndWaitForReplicationAsync(1, TimeSpan.FromSeconds(1));
            else await batch.ExecuteAsync();
        }
        await Assert.That(samples.Any(tags => Equals(tags["server.port"], primary.Port))).IsTrue();
        await Assert.That(samples.Any(tags => Equals(tags["server.port"], sentinel.Port))).IsFalse();
    }

    [Test]
    [Arguments("batch")]
    [Arguments("durability")]
    [Arguments("transaction")]
    public async Task BatchDurationKeepsTheAdmittedPrimaryAfterPromotion(string kind)
    {
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var primary = Primary((_, command) => command.StartsWith("SET delayed") && kind == "transaction"
            ? "+QUEUED\r\n"u8.ToArray() : null);
        primary.SuppressReply = command =>
        {
            var last = kind switch
            {
                "transaction" => command == "EXEC",
                "durability" => command == "WAIT 1 1000",
                _ => command == "SET delayed2 value",
            };
            if (last) admitted.TrySetResult();
            return kind switch
            {
                "transaction" => command == "EXEC",
                "durability" => command == "WAIT 1 1000",
                _ => command.StartsWith("SET delayed"),
            };
        };
        await using var promoted = Primary();
        var port = primary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        var original = client.Core.Sentinel!.Current!;
        var samples = new ConcurrentQueue<Dictionary<string, object?>>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "db.client.operation.duration")
                meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            var values = tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value);
            if (Equals(values.GetValueOrDefault("db.operation.batch.size"), 2)
                && (Equals(values.GetValueOrDefault("server.port"), primary.Port)
                    || Equals(values.GetValueOrDefault("server.port"), promoted.Port))) samples.Enqueue(values);
        });
        listener.Start();
        var pending = ExecuteAsync();
        await admitted.Task.WaitAsync(Limit);
        Volatile.Write(ref port, promoted.Port);
        using (var rejection = Respire.Protocol.RespValue.Error("READONLY replica"))
            original.ObserveResponse(original.Multiplexer.GetConnection(), "SET", in rejection);
        await client.SetAsync("next", "value").AsTask().WaitAsync(Limit);
        await Assert.That(pending.IsCompleted).IsFalse();
        var commandIndex = primary.ReceivedCommands.ToList().IndexOf("SET delayed1 value");
        var connectionId = primary.ReceivedConnectionIds[commandIndex];
        var response = kind switch
        {
            "transaction" => "*2\r\n+OK\r\n+OK\r\n",
            "durability" => ":1\r\n",
            _ => "+OK\r\n+OK\r\n",
        };
        await primary.SendRawAsync(Encoding.ASCII.GetBytes(response), connectionId);
        await pending.WaitAsync(Limit);
        await Assert.That(samples.Count).IsEqualTo(1);
        await Assert.That(samples.Single()["server.port"]).IsEqualTo(primary.Port);
        await Assert.That(samples.Single()["server.address"]).IsEqualTo("127.0.0.1");

        async Task ExecuteAsync()
        {
            if (kind == "transaction")
            {
                await using var transaction = client.CreateTransaction();
                _ = transaction.Set("delayed1", "value");
                _ = transaction.Set("delayed2", "value");
                await transaction.CommitAsync();
            }
            else
            {
                using var batch = client.CreateBatch();
                _ = batch.Set("delayed1", "value");
                _ = batch.Set("delayed2", "value");
                if (kind == "durability") await batch.ExecuteAndWaitForReplicationAsync(1, TimeSpan.FromSeconds(1));
                else await batch.ExecuteAsync();
            }
        }
    }

    [Test]
    public async Task StateObserverCanSynchronouslyDisposeTheClient()
    {
        await using var primary = Primary();
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = RespireClient.Create(Options(sentinel.Port));
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += change =>
        {
            if (change.State != RespireConnectionState.Connected) return;
            try
            {
                client.DisposeAsync().AsTask().GetAwaiter().GetResult();
                disposed.TrySetResult();
            }
            catch (Exception error) { disposed.TrySetException(error); }
        };
        try { await client.SetAsync("key", "value"); }
        catch (Exception) when (client.Core.Disposed) { }
        await disposed.Task.WaitAsync(Limit);
        await Assert.That(client.IsConnected).IsFalse();
    }

    [Test]
    public async Task ConsecutiveFailoversRetainAndDisposeBothDrainingGenerations()
    {
        await using var first = Primary((_, command) => command.StartsWith("SET retire")
            ? "-READONLY replica\r\n"u8.ToArray() : null);
        await using var second = Primary((_, command) => command.StartsWith("SET retire")
            ? "-READONLY replica\r\n"u8.ToArray() : null);
        first.SuppressReply = second.SuppressReply = command => command.StartsWith("BLPOP ");
        await using var third = Primary();
        var port = first.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        var original = client.Core.Sentinel!.Current!;
        var firstRead = client.Lists.LeftPopAsync("first", waitFor: Timeout.InfiniteTimeSpan).AsTask();
        await WaitForCommandAsync(first, "BLPOP ");
        Volatile.Write(ref port, second.Port);
        await Assert.That(async () => await client.SetAsync("retire:first", "value")).Throws<RespireServerException>();
        await client.SetAsync("second", "value");
        var replacement = client.Core.Sentinel.Current!;
        var secondRead = client.Lists.LeftPopAsync("second", waitFor: Timeout.InfiniteTimeSpan).AsTask();
        await WaitForCommandAsync(second, "BLPOP ");
        Volatile.Write(ref port, third.Port);
        await Assert.That(async () => await client.SetAsync("retire:second", "value")).Throws<RespireServerException>();
        await client.SetAsync("third", "value");
        await Assert.That(original.Retirement.IsCompleted).IsFalse();
        await Assert.That(replacement.Retirement.IsCompleted).IsFalse();
        long retained = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "respire.sentinel.generations.retired")
                meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => retained = value);
        listener.Start();
        listener.RecordObservableInstruments();
        await Assert.That(retained).IsGreaterThanOrEqualTo(2);
        await client.DisposeAsync().AsTask().WaitAsync(Limit);
        await Assert.That(async () => await firstRead.WaitAsync(Limit)).Throws<Exception>();
        await Assert.That(async () => await secondRead.WaitAsync(Limit)).Throws<Exception>();
        await Assert.That(original.Retirement.IsCompleted).IsTrue();
        await Assert.That(replacement.Retirement.IsCompleted).IsTrue();
        await Assert.That(original.CountedAsRetired).IsFalse();
        await Assert.That(replacement.CountedAsRetired).IsFalse();
        await Assert.That(third.ReceivedCommands.Any(command => command.StartsWith("BLPOP "))).IsFalse();
    }

    [Test]
    public async Task DisposalDoesNotDropAnAlreadyPublishedFailoverMeasurement()
    {
        await using var primary = Primary((_, command) => command.StartsWith("SET retire")
            ? "-READONLY replica\r\n"u8.ToArray() : null);
        await using var promoted = Primary();
        var port = primary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        await using var client = RespireClient.Create(Options(sentinel.Port));
        var observerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseObserver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var measured = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "respire.sentinel.failover")
                meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "server.port" && Equals(tag.Value, promoted.Port)) measured.TrySetResult(value);
        });
        listener.Start();
        client.ConnectionStateChanged += change =>
        {
            if (change.Endpoint.Port != primary.Port || change.State != RespireConnectionState.Connected) return;
            observerEntered.TrySetResult();
            releaseObserver.Task.GetAwaiter().GetResult();
        };
        try
        {
            await client.SetAsync("first", "value");
            await observerEntered.Task.WaitAsync(Limit);
            Volatile.Write(ref port, promoted.Port);
            await Assert.That(async () => await client.SetAsync("retire", "value")).Throws<RespireServerException>();
            await client.SetAsync("promoted", "value");
            await client.DisposeAsync().AsTask().WaitAsync(Limit);
        }
        finally { releaseObserver.TrySetResult(); }
        await Assert.That(await measured.Task.WaitAsync(Limit)).IsEqualTo(1L);
    }

    [Test]
    public async Task PermanentlyFailingFenceBacksOffUntilDisposalCancelsTheDelay()
    {
        var rejectFences = false;
        await using var primary = Primary((_, command) => command switch
        {
            "CLIENT ID" => ":41\r\n"u8.ToArray(),
            _ when command.StartsWith("CLIENT KILL ") => Volatile.Read(ref rejectFences)
                ? "-ERR fencing unavailable\r\n"u8.ToArray() : ":0\r\n"u8.ToArray(),
            _ => null,
        });
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await client.EnsureReliableCorrectionOrderingAsync().AsTask().WaitAsync(Limit);
        var router = client.Core.Sentinel!;
        var generation = router.Current!;
        var clock = new FenceClock();
        router.Clock = clock;
        Volatile.Write(ref rejectFences, true);
        // Lose an accepted command after capturing CLIENT ID, so retirement must fence it.
        primary.CloseConnectionAfterCommand = primary.CommandsSeen + 1;
        await Assert.That(async () => await client.SetAsync("lost", "value").AsTask().WaitAsync(Limit))
            .Throws<RespireConnectionException>();
        var first = await clock.Timers.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        await Assert.That(first.Delay).IsEqualTo(TimeSpan.FromSeconds(1));
        await Assert.That(generation.Multiplexer.HasPendingCorrectionFences).IsTrue();
        await Assert.That(generation.Retirement.IsCompleted).IsFalse();
        first.Fire();
        var second = await clock.Timers.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        await Assert.That(second.Delay).IsEqualTo(TimeSpan.FromSeconds(2));
        await Assert.That(generation.CountedAsRetired).IsTrue();
        await client.DisposeAsync().AsTask().WaitAsync(Limit);
        await second.Disposed.Task.WaitAsync(Limit);
        await Assert.That(generation.Retirement.IsCompleted).IsTrue();
        await Assert.That(generation.CountedAsRetired).IsFalse();
        await Assert.That(clock.Timers.Reader.TryRead(out _)).IsFalse();
    }

    private sealed class FenceClock : TimeProvider
    {
        internal Channel<FenceTimer> Timers { get; } = Channel.CreateUnbounded<FenceTimer>();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FenceTimer(callback, state, dueTime);
            Timers.Writer.TryWrite(timer);
            return timer;
        }
    }

    private sealed class FenceTimer(TimerCallback callback, object? state, TimeSpan delay) : ITimer
    {
        internal TimeSpan Delay => delay;
        internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Fire() { if (!Disposed.Task.IsCompleted) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() => Disposed.TrySetResult();
        public ValueTask DisposeAsync() { Dispose(); return default; }
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
