using System.Text;
using Microsoft.Extensions.Logging;
using Respire.Internal;
using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClusterShardedPubSubTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task DifferentSlotsUseOneDedicatedConnectionPerPrimary(int protocol)
    {
        await using var cluster = new Cluster(protocol);
        await using var client = cluster.CreateClient();
        await using var subscription = await client.SubscribeShardedAsync(["bar", "baz", "foo", "bar"]);
        await using var duplicate = await client.SubscribeShardedAsync("bar");
        await Assert.That(cluster.First.ReceivedCommands.Count(command => command == "SSUBSCRIBE bar")).IsEqualTo(1);
        var firstIds = ControlIds(cluster.First);
        await Assert.That(firstIds.Distinct().Count()).IsEqualTo(1);
        await Assert.That(ControlIds(cluster.Second).Distinct().Count()).IsEqualTo(1);
        await using var reader = subscription.GetAsyncEnumerator();
        await cluster.SendAsync(cluster.First, "bar", "one");
        await cluster.SendAsync(cluster.Second, "foo", "two");
        var received = new HashSet<string>();
        for (var i = 0; i < 2; i++)
        {
            await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(Deadline)).IsTrue();
            received.Add(reader.Current.Text!);
        }
        await Assert.That(received).IsEquivalentTo(["one", "two"]);
        await duplicate.DisposeAsync();
        await Assert.That(cluster.First.ReceivedCommands.Any(command => command == "SUNSUBSCRIBE bar")).IsFalse();
        await subscription.DisposeAsync();
        await Assert.That(cluster.First.ReceivedCommands.Count(command => command == "SUNSUBSCRIBE bar")).IsEqualTo(1);
        await Assert.That(cluster.Second.ReceivedCommands.Count(command => command == "SUNSUBSCRIBE foo")).IsEqualTo(1);
    }

    [Test]
    public async Task MovedSubscriptionUpdatesPublishRoutingWithoutCrossSlotCommands()
    {
        await using var cluster = new Cluster(2);
        cluster.SecondOverride = (_, command) => command == "SSUBSCRIBE foo"
            ? Encoding.ASCII.GetBytes($"-MOVED {ClusterHash.GetSlot("foo")} 127.0.0.1:{cluster.First.Port}\r\n") : null;
        await using var client = cluster.CreateClient();
        await using var subscription = await client.SubscribeShardedAsync("foo");
        await Assert.That(await client.PublishShardedAsync("foo", "payload")).IsEqualTo(1L);
        await Assert.That(cluster.First.ReceivedCommands).Contains("SPUBLISH foo payload");
        await Assert.That(cluster.First.ReceivedCommands).Contains("SSUBSCRIBE foo");
        await Assert.That(cluster.Second.ReceivedCommands.Any(command => command.StartsWith("SPUBLISH "))).IsFalse();
        var commands = cluster.First.ReceivedCommands;
        var ids = cluster.First.ReceivedConnectionIds;
        await Assert.That(ids[commands.ToList().IndexOf("SPUBLISH foo payload")])
            .IsNotEqualTo(ids[commands.ToList().IndexOf("SSUBSCRIBE foo")]);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task InitialMovedSubscriptionDoesNotRecoverProvisionalRoute(int protocol)
    {
        await using var cluster = new Cluster(protocol);
        cluster.SecondOverride = (_, command) => command == "SSUBSCRIBE foo"
            ? Encoding.ASCII.GetBytes($"-MOVED {ClusterHash.GetSlot("foo")} 127.0.0.1:{cluster.First.Port}\r\n") : null;
        await using var client = cluster.CreateClient(new()
        {
            InitialDelay = TimeSpan.FromSeconds(30), MaxDelay = TimeSpan.FromSeconds(30), JitterRatio = 0,
        });
        await using var subscription = await client.SubscribeShardedAsync("foo");
        await using var added = await client.SubscribeShardedAsync("bar").AsTask().WaitAsync(Deadline);
        await Assert.That(cluster.First.ReceivedCommands.Count(command => command == "SSUBSCRIBE foo")).IsEqualTo(1);
        await Assert.That(cluster.First.ReceivedCommands.Count(command => command == "SSUBSCRIBE bar")).IsEqualTo(1);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task AskSubscriptionSendsAskingBeforeSubscribeOnTargetPrimary(int protocol)
    {
        await using var cluster = new Cluster(protocol);
        cluster.SecondOverride = (_, command) => command == "SSUBSCRIBE foo"
            ? Encoding.ASCII.GetBytes($"-ASK {ClusterHash.GetSlot("foo")} 127.0.0.1:{cluster.First.Port}\r\n") : null;
        await using var client = cluster.CreateClient();
        await using var subscription = await client.SubscribeShardedAsync("foo");
        var commands = cluster.First.ReceivedCommands;
        var asking = commands.ToList().IndexOf("ASKING");
        var subscribe = commands.ToList().IndexOf("SSUBSCRIBE foo");
        await Assert.That(asking).IsGreaterThanOrEqualTo(0);
        await Assert.That(subscribe).IsGreaterThan(asking);
        await Assert.That(cluster.Second.ReceivedCommands.Contains("SSUBSCRIBE foo")).IsTrue();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task TopologyMoveResubscribesAndPublishesGapBeforeSameReadMessage(int protocol)
    {
        await using var cluster = new Cluster(protocol);
        await using var client = cluster.CreateClient();
        await using var subscription = await client.SubscribeShardedAsync("foo");
        cluster.FirstOverride = (_, command) => command == "SSUBSCRIBE foo"
            ? [.. cluster.Confirmation("ssubscribe", "foo"), .. cluster.Message("foo", "after")] : null;
        var router = client.Core.Cluster!;
        router.SetSlotOwner(ClusterHash.GetSlot("foo"), router.GetMultiplexer(new("127.0.0.1", cluster.First.Port)));
        await using var reader = subscription.GetAsyncEnumerator();
        await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(Deadline)).IsTrue();
        await Assert.That(reader.Current.Kind).IsEqualTo(RespireMessageKind.Gap);
        await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(Deadline)).IsTrue();
        await Assert.That(reader.Current.Text).IsEqualTo("after");
        await Assert.That(cluster.Second.ReceivedCommands).Contains("SUNSUBSCRIBE foo");
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task UnsolicitedSunsubscribeDoesNotCompleteAnotherPendingSubscription(int protocol)
    {
        await using var cluster = new Cluster(protocol);
        await using var client = cluster.CreateClient();
        await using var subscription = await client.SubscribeShardedAsync(["bar", "baz"]);
        cluster.First.SuppressReply = command => command == "SSUBSCRIBE b";
        var pending = client.SubscribeShardedAsync("b").AsTask();
        await WaitAsync(() => cluster.First.ReceivedCommands.Contains("SSUBSCRIBE b"));
        var socket = ControlIds(cluster.First).Last();
        await cluster.First.SendRawAsync(cluster.Confirmation("sunsubscribe", "bar"), socket);
        // A subsequent ordinary push proves the preceding SUNSUBSCRIBE was processed.
        await using var reader = subscription.GetAsyncEnumerator();
        await cluster.First.SendRawAsync(cluster.Message("baz", "bar removed"), socket);
        await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(Deadline)).IsTrue();
        await Assert.That(reader.Current.Text).IsEqualTo("bar removed");
        await Assert.That(pending.IsCompleted).IsFalse();
        cluster.FirstOverride = (_, command) => command == "SSUBSCRIBE bar"
            ? [.. cluster.Confirmation("ssubscribe", "bar"), .. cluster.Message("bar", "restored")] : null;
        await cluster.First.SendRawAsync(cluster.Confirmation("ssubscribe", "b"), socket);
        await using var added = await pending.WaitAsync(Deadline);
        await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(Deadline)).IsTrue();
        await Assert.That(reader.Current.Kind).IsEqualTo(RespireMessageKind.Gap);
        await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(Deadline)).IsTrue();
        await Assert.That(reader.Current.Text).IsEqualTo("restored");
    }

    [Test]
    public async Task ClosedPrimaryRestoresOnlyItsChannelsAndPreservesOtherPrimary()
    {
        await using var cluster = new Cluster(2);
        await using var client = cluster.CreateClient();
        await using var subscription = await client.SubscribeShardedAsync(["bar", "foo"]);
        cluster.First.CloseConnectionAfterCommand = cluster.First.CommandsSeen + 1;
        await Assert.That(async () => await client.SubscribeShardedAsync("baz")).Throws<RespireConnectionException>();
        await WaitAsync(() => cluster.First.ReceivedCommands.Count(command => command == "SSUBSCRIBE bar") == 2);
        await cluster.SendAsync(cluster.Second, "foo", "healthy");
        await using var reader = subscription.GetAsyncEnumerator();
        var messages = new List<RespireMessage>();
        for (var i = 0; i < 2; i++)
        {
            await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(Deadline)).IsTrue();
            messages.Add(reader.Current);
        }
        await Assert.That(messages.Count(message => message.Kind == RespireMessageKind.Gap)).IsEqualTo(1);
        await Assert.That(messages.Count(message => message.Text == "healthy")).IsEqualTo(1);
        await Assert.That(cluster.Second.ReceivedCommands.Count(command => command == "SSUBSCRIBE foo")).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConfiguredExhaustionCompletesShardedSubscriptionsAndLeavesRegularSubscriptionsUsable(bool throwingLogger)
    {
        await using var cluster = new Cluster(2);
        using var logger = new ThrowingRecoveryLogger();
        await using var client = cluster.CreateClient(new()
        {
            InitialDelay = TimeSpan.FromMilliseconds(10), MaxDelay = TimeSpan.FromMilliseconds(10),
            JitterRatio = 0, MaxAttempts = 2,
        }, throwingLogger ? logger : null);
        await using var subscription = await client.SubscribeShardedAsync("bar");
        cluster.FirstOverride = (_, command) => command == "SSUBSCRIBE bar" ? "-ERR denied\r\n"u8.ToArray() : null;
        await cluster.First.SendRawAsync(cluster.Confirmation("sunsubscribe", "bar"), ControlIds(cluster.First).Last());
        await Assert.That(await subscription.Completion.WaitAsync(Deadline)).IsEqualTo(RespireSubscriptionEndReason.ReconnectExhausted);
        await Assert.That(cluster.First.ReceivedCommands.Count(command => command == "SSUBSCRIBE bar")).IsEqualTo(3);
        await Assert.That(async () => await client.SubscribeShardedAsync("foo")).Throws<RespireReconnectLimitException>();
        await using var regular = await client.SubscribeAsync("regular");
        await Assert.That(regular.IsDisposed).IsFalse();
        await Assert.That(logger.Failures).IsEqualTo(throwingLogger ? 1 : 0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ServerRejectionPreservesHealthyChannelsOnTheSamePrimary(bool partialActivation)
    {
        await using var cluster = new Cluster(2);
        await using var client = cluster.CreateClient();
        await using var subscription = await client.SubscribeShardedAsync("bar");
        var identity = ControlIds(cluster.First).Single();
        cluster.FirstOverride = (_, command) => command == "SSUBSCRIBE baz" ? "-NOPERM denied\r\n"u8.ToArray() : null;
        await Assert.That(async () => await client.SubscribeShardedAsync(partialActivation ? ["b", "baz"] : ["baz"]))
            .ThrowsExactly<RespireServerException>();
        await using var reader = subscription.GetAsyncEnumerator();
        await cluster.First.SendRawAsync(cluster.Message("bar", "healthy"), identity);
        await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(Deadline)).IsTrue();
        await Assert.That(reader.Current.Text).IsEqualTo("healthy");
        await Assert.That(cluster.First.ReceivedCommands.Count(command => command == "SSUBSCRIBE bar")).IsEqualTo(1);
        await Assert.That(cluster.First.ReceivedCommands.Contains("SUNSUBSCRIBE b")).IsEqualTo(partialActivation);
        await Assert.That(ControlIds(cluster.First).Distinct().Count()).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RecoveryTracksEveryAffectedPrimaryThroughTerminalOutcome(bool exhaust)
    {
        await using var cluster = new Cluster(2);
        await using var client = cluster.CreateClient(new()
        {
            InitialDelay = TimeSpan.FromSeconds(1), MaxDelay = TimeSpan.FromSeconds(1),
            JitterRatio = 0, MaxAttempts = 3,
        });
        var clock = new RecoveryClock();
        await using var hub = new SubscriptionHub(client.Core, clock);
        await using var subscription = await hub.SubscribeAsync(SubscriptionKind.Sharded, ["bar", "foo", "{foo}:barrier"], new(), CancellationToken.None);
        await using var reader = subscription.GetAsyncEnumerator();
        var changes = new System.Collections.Concurrent.ConcurrentQueue<RespireConnectionStateChange>();
        client.ConnectionStateChanged += change =>
        {
            if (change.ReconnectSource == RespireReconnectSource.PubSub) changes.Enqueue(change);
        };
        cluster.FirstOverride = (_, command) => command == "SSUBSCRIBE bar" ? "-ERR denied\r\n"u8.ToArray() : null;
        cluster.SecondOverride = (_, command) => command == "SSUBSCRIBE foo" ? "-ERR denied\r\n"u8.ToArray() : null;
        await cluster.First.SendRawAsync(cluster.Confirmation("sunsubscribe", "bar"), ControlIds(cluster.First).Last());
        var first = await clock.NextAsync();
        await cluster.Second.SendRawAsync([.. cluster.Confirmation("sunsubscribe", "foo"),
            .. cluster.Message("{foo}:barrier", "both removed")], ControlIds(cluster.Second).Last());
        // The subsequent message proves the second migration frame was processed before retry.
        await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(Deadline)).IsTrue();
        await Assert.That(reader.Current.Text).IsEqualTo("both removed");
        first.Fire();
        var second = await clock.NextAsync();
        await WaitAsync(() => changes.Count(change => change.ReconnectAttempt == 2 && change.NextReconnectDelay is not null) == 2);
        await Assert.That(changes.Any(change => change.State == RespireConnectionState.Connected)).IsFalse();
        cluster.FirstOverride = null;
        second.Fire();
        var third = await clock.NextAsync();
        await WaitAsync(() => changes.Count(change => change.ReconnectAttempt == 3 && change.NextReconnectDelay is not null) == 2);
        await Assert.That(changes.Any(change => change.State == RespireConnectionState.Connected)).IsFalse();
        if (!exhaust) cluster.SecondOverride = null;
        third.Fire();
        await WaitAsync(() => changes.Count(change => change.NextReconnectDelay is null) == 2);
        var terminal = changes.Where(change => change.NextReconnectDelay is null).ToArray();
        await Assert.That(terminal.Select(change => change.Endpoint.Port)).IsEquivalentTo([cluster.First.Port, cluster.Second.Port]);
        await Assert.That(terminal.All(change => change.State == (exhaust
            ? RespireConnectionState.Disconnected : RespireConnectionState.Connected))).IsTrue();
        await Assert.That(terminal.All(change => change.ReconnectExhausted == exhaust)).IsTrue();
        if (exhaust)
            await Assert.That(await subscription.Completion.WaitAsync(Deadline)).IsEqualTo(RespireSubscriptionEndReason.ReconnectExhausted);

    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RecoveryRejectsNewSubscriptionsWithOrWithoutConfiguredPolicy(bool configured)
    {
        await using var cluster = new Cluster(2);
        await using var client = cluster.CreateClient(configured ? new()
        {
            InitialDelay = TimeSpan.FromSeconds(1), MaxDelay = TimeSpan.FromSeconds(1),
            JitterRatio = 0, MaxAttempts = 3,
        } : null);
        var clock = new RecoveryClock();
        await using var hub = new SubscriptionHub(client.Core, clock);
        await using var subscription = await hub.SubscribeAsync(SubscriptionKind.Sharded, ["bar"], new(), CancellationToken.None);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += change =>
        {
            if (change.ReconnectSource == RespireReconnectSource.PubSub && change.State == RespireConnectionState.Connected)
                recovered.TrySetResult();
        };
        cluster.FirstOverride = (_, command) => command == "SSUBSCRIBE bar" ? "-ERR denied\r\n"u8.ToArray() : null;
        await cluster.First.SendRawAsync(cluster.Confirmation("sunsubscribe", "bar"), ControlIds(cluster.First).Last());
        var retry = await clock.NextAsync();
        await Assert.That(async () => await hub.SubscribeAsync(SubscriptionKind.Sharded, ["foo"], new(), CancellationToken.None))
            .ThrowsExactly<RespireConnectionException>();
        await Assert.That(cluster.Second.ReceivedCommands.Contains("SSUBSCRIBE foo")).IsFalse();
        cluster.FirstOverride = null;
        retry.Fire();
        await recovered.Task.WaitAsync(Deadline);
        await using var added = await hub.SubscribeAsync(SubscriptionKind.Sharded, ["foo"], new(), CancellationToken.None);
        await Assert.That(cluster.Second.ReceivedCommands.Count(command => command == "SSUBSCRIBE foo")).IsEqualTo(1);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task UnrelatedSlotChangesDoNotStartShardedRecovery(int protocol)
    {
        await using var cluster = new Cluster(protocol);
        await using var client = cluster.CreateClient(new()
        {
            InitialDelay = TimeSpan.FromSeconds(30), MaxDelay = TimeSpan.FromSeconds(30), JitterRatio = 0,
        });
        var clock = new RecoveryClock();
        await using var hub = new SubscriptionHub(client.Core, clock);
        await using var subscription = await hub.SubscribeAsync(SubscriptionKind.Sharded, ["bar"], new(), CancellationToken.None);
        var router = client.Core.Cluster!;
        var first = router.GetMultiplexer(new("127.0.0.1", cluster.First.Port));
        var second = router.GetMultiplexer(new("127.0.0.1", cluster.Second.Port));
        // foo changes owner, but no subscription uses that slot. A nonzero recovery delay
        // must not make the healthy bar route reject admission for another healthy channel.
        router.SetSlotOwner(ClusterHash.GetSlot("foo"), first);
        await using var added = await hub.SubscribeAsync(SubscriptionKind.Sharded, ["baz"], new(), CancellationToken.None)
            .AsTask().WaitAsync(Deadline);
        await Assert.That(cluster.First.ReceivedCommands.Count(command => command == "SSUBSCRIBE bar")).IsEqualTo(1);
        await Assert.That(cluster.First.ReceivedCommands.Count(command => command == "SSUBSCRIBE baz")).IsEqualTo(1);

        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += change =>
        {
            if (change.ReconnectSource == RespireReconnectSource.PubSub && change.SourceState == RespireConnectionState.Connected)
                recovered.TrySetResult();
        };
        // A subscribed slot changing owner still starts recovery and migrates its route.
        router.SetSlotOwner(ClusterHash.GetSlot("bar"), second);
        var retry = await clock.NextAsync();
        retry.Fire();
        await recovered.Task.WaitAsync(Deadline);
        await Assert.That(cluster.Second.ReceivedCommands.Count(command => command == "SSUBSCRIBE bar")).IsEqualTo(1);
        await Assert.That(cluster.First.ReceivedCommands.Count(command => command == "SSUBSCRIBE baz")).IsEqualTo(1);
    }

    [Test]
    [Arguments(2, false)]
    [Arguments(2, true)]
    [Arguments(3, false)]
    [Arguments(3, true)]
    public async Task RecoveryRejectsNewSubscriptionWhileControlReplyIsPending(int protocol, bool configured)
    {
        await using var cluster = new Cluster(protocol);
        await using var client = cluster.CreateClient(configured
            ? new() { InitialDelay = TimeSpan.Zero, JitterRatio = 0, MaxAttempts = 3 } : null);
        await using var hub = new SubscriptionHub(client.Core);
        await using var subscription = await hub.SubscribeAsync(SubscriptionKind.Sharded, ["bar"], new(), CancellationToken.None);
        cluster.First.SuppressReply = command => command == "SSUBSCRIBE bar";
        try
        {
            await cluster.First.SendRawAsync(cluster.Confirmation("sunsubscribe", "bar"), ControlIds(cluster.First).Last());
            await WaitAsync(() => cluster.First.ReceivedCommands.Count(command => command == "SSUBSCRIBE bar") == 2);
            // Recovery now owns the control gate and is blocked on a real wire reply.
            var pending = hub.SubscribeAsync(SubscriptionKind.Sharded, ["foo"], new(), CancellationToken.None).AsTask();
            await Assert.That(pending.IsCompleted).IsTrue();
            await Assert.That(async () => await pending).ThrowsExactly<RespireConnectionException>();
            await Assert.That(cluster.Second.ReceivedCommands.Contains("SSUBSCRIBE foo")).IsFalse();
        }
        finally
        {
            // Interrupt the intentionally unanswered control operation before subscription disposal.
            await hub.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
    }

    [Test]
    public async Task HubDisposalDetachesTopologyHandlerWhileRouterRemainsAlive()
    {
        await using var cluster = new Cluster(2);
        await using var client = cluster.CreateClient();
        await using var hub = new SubscriptionHub(client.Core);
        await using var subscription = await hub.SubscribeAsync(SubscriptionKind.Sharded, ["bar"], new(), CancellationToken.None);
        var router = client.Core.Cluster!;
        var topologyEvent = router.GetType().GetField("TopologyChanged",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        bool IsAttached() => ((Delegate?)topologyEvent.GetValue(router))?.GetInvocationList()
            .Any(handler => ReferenceEquals(handler.Target, hub)) == true;
        await Assert.That(IsAttached()).IsTrue();
        await hub.DisposeAsync().AsTask().WaitAsync(Deadline);
        await Assert.That(IsAttached()).IsFalse();
        router.SetSlotOwner(ClusterHash.GetSlot("bar"), router.GetMultiplexer(new("127.0.0.1", cluster.Second.Port)));
        await Assert.That(await subscription.Completion).IsEqualTo(RespireSubscriptionEndReason.ClientDisposed);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task SameChannelMigrationDuringUnsubscribeDoesNotConsumeNextControlReply(int protocol)
    {
        await using var cluster = new Cluster(protocol);
        await using var client = cluster.CreateClient();
        await using var removed = await client.SubscribeShardedAsync("bar");
        await using var healthy = await client.SubscribeShardedAsync("baz");
        var socket = ControlIds(cluster.First).First();
        cluster.First.SuppressReply = command => command is "SUNSUBSCRIBE bar" or "SSUBSCRIBE b";
        var removal = removed.DisposeAsync().AsTask();
        await WaitAsync(() => cluster.First.ReceivedCommands.Contains("SUNSUBSCRIBE bar"));
        // The migration confirmation is indistinguishable from the command's own reply.
        await cluster.First.SendRawAsync(cluster.Confirmation("sunsubscribe", "bar"), socket);
        await removal.WaitAsync(Deadline);
        var pending = client.SubscribeShardedAsync("b").AsTask();
        await WaitAsync(() => cluster.First.ReceivedCommands.Contains("SSUBSCRIBE b"));
        await using var reader = healthy.GetAsyncEnumerator();
        await cluster.First.SendRawAsync([.. cluster.Confirmation("sunsubscribe", "bar"),
            .. cluster.Message("baz", "duplicate processed")], socket);
        await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(Deadline)).IsTrue();
        await Assert.That(reader.Current.Text).IsEqualTo("duplicate processed");
        await Assert.That(pending.IsCompleted).IsFalse();
        await cluster.First.SendRawAsync(cluster.Confirmation("ssubscribe", "b"), socket);
        await using var added = await pending.WaitAsync(Deadline);
    }

    private sealed class RecoveryClock : TimeProvider
    {
        private readonly System.Threading.Channels.Channel<RecoveryTimer> _timers =
            global::System.Threading.Channels.Channel.CreateUnbounded<RecoveryTimer>();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new RecoveryTimer(callback, state);
            _timers.Writer.TryWrite(timer);
            return timer;
        }
        internal Task<RecoveryTimer> NextAsync() => _timers.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
    }

    private sealed class RecoveryTimer(TimerCallback callback, object? state) : ITimer
    {
        private int _finished;
        internal void Fire() { if (Interlocked.Exchange(ref _finished, 1) == 0) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => Volatile.Read(ref _finished) == 0;
        public void Dispose() => Interlocked.Exchange(ref _finished, 1);
        public ValueTask DisposeAsync() { Dispose(); return default; }
    }

    [Test]
    public async Task ClientDisposalInterruptsStalledShardedActivation()
    {
        await using var cluster = new Cluster(2);
        await using var client = cluster.CreateClient();
        await using var subscription = await client.SubscribeShardedAsync("bar");
        cluster.Second.SuppressReply = command => command == "SSUBSCRIBE foo";
        var pending = client.SubscribeShardedAsync("foo").AsTask();
        await WaitAsync(() => cluster.Second.ReceivedCommands.Contains("SSUBSCRIBE foo"));
        await client.DisposeAsync().AsTask().WaitAsync(Deadline);
        await Assert.That(async () => await pending).Throws<Exception>();
        await Assert.That(await subscription.Completion).IsEqualTo(RespireSubscriptionEndReason.ClientDisposed);
    }

    [Test]
    public async Task CancelledActivationPreservesCallerTokenAndHealthyPrimary()
    {
        await using var cluster = new Cluster(2);
        await using var client = cluster.CreateClient();
        await using var subscription = await client.SubscribeShardedAsync("bar");
        cluster.Second.SuppressReply = command => command == "SSUBSCRIBE foo";
        using var cancellation = new CancellationTokenSource();
        var pending = client.SubscribeShardedAsync("foo", cancellation.Token).AsTask();
        await WaitAsync(() => cluster.Second.ReceivedCommands.Contains("SSUBSCRIBE foo"));
        await cancellation.CancelAsync();
        var error = await Assert.That(async () => await pending.WaitAsync(Deadline)).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await cluster.SendAsync(cluster.First, "bar", "still live");
        await using var reader = subscription.GetAsyncEnumerator();
        await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(Deadline)).IsTrue();
        await Assert.That(reader.Current.Text).IsEqualTo("still live");
        await Assert.That(cluster.First.ReceivedCommands.Count(command => command == "SSUBSCRIBE bar")).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SubscriptionPushFilterSeesWhetherCommandHasEnteredFifo(bool push)
    {
        var confirmation = Encoding.ASCII.GetBytes($"{(push ? '>' : '*')}3\r\n$12\r\nsunsubscribe\r\n$3\r\nbar\r\n:0\r\n");
        await using var server = new FakeRespServer(confirmation);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unsolicited = 0;
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new RespireConnectionOptions
            {
                PushHandler = (in RespValue _) => observed.TrySetResult(),
                SubscriptionPushFilter = (in RespValue _, bool pending) =>
                {
                    if (!pending) Interlocked.Increment(ref unsolicited);
                    return !pending;
                },
            });
        await server.SendRawAsync([.. confirmation, .. "*3\r\n$8\r\nsmessage\r\n$3\r\nbar\r\n$4\r\nsync\r\n"u8]);
        await observed.Task.WaitAsync(Deadline);
        await Assert.That(unsolicited).IsEqualTo(1);
        using var reply = await connection.SendAsync(new Cmd1(Verbs.SUnsubscribe, "bar"), CancellationToken.None)
            .AsTask().WaitAsync(Deadline);
        await Assert.That(reply.AsArray()[0].AsString()).IsEqualTo("sunsubscribe");
        await Assert.That(unsolicited).IsEqualTo(1);
    }

    [Test]
    public async Task RefreshedTopologyMovesSubscriptionsOnlyWhenOwnersChange()
    {
        await using var cluster = new Cluster(2);
        await using var client = cluster.CreateClient();
        await using var subscription = await client.SubscribeShardedAsync(["bar", "foo"]);
        var notifications = 0;
        client.Core.Cluster!.TopologyChanged += () => Interlocked.Increment(ref notifications);
        _ = await client.Core.Cluster.GetMasterConnectionsAsync(CancellationToken.None);
        await Assert.That(notifications).IsEqualTo(0);
        cluster.FirstOverride = (_, command) => command == "CLUSTER SLOTS"
            ? Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{cluster.First.Port}\r\n") : null;
        _ = await client.Core.Cluster.GetMasterConnectionsAsync(CancellationToken.None);
        await Assert.That(notifications).IsEqualTo(1);
        await using var reader = subscription.GetAsyncEnumerator();
        await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(Deadline)).IsTrue();
        await Assert.That(reader.Current.Kind).IsEqualTo(RespireMessageKind.Gap);
        await cluster.SendAsync(cluster.First, "foo", "new owner");
        await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(Deadline)).IsTrue();
        await Assert.That(reader.Current.Text).IsEqualTo("new owner");
        await Assert.That(cluster.First.ReceivedCommands.Count(command => command == "SSUBSCRIBE bar")).IsEqualTo(1);
    }

    private static int[] ControlIds(FakeRespServer server) => server.ReceivedCommands
        .Select((command, index) => (command, index)).Where(pair => pair.command.StartsWith("SSUBSCRIBE "))
        .Select(pair => server.ReceivedConnectionIds[pair.index]).ToArray();

    private static async Task WaitAsync(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(Deadline);
        while (!predicate()) await Task.Delay(5, deadline.Token);
    }

    private sealed class ThrowingRecoveryLogger : ILoggerFactory, ILogger
    {
        internal int Failures;
        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!formatter(state, exception).StartsWith("Sharded pub/sub recovery attempt", StringComparison.Ordinal)) return;
            Interlocked.Increment(ref Failures);
            throw new InvalidOperationException("Test logger failure");
        }
    }

    private sealed class Cluster : IAsyncDisposable
    {
        private readonly int _protocol;
        internal readonly FakeRespServer First = new(20, FakeRespServer.OkReply);
        internal readonly FakeRespServer Second = new(20, FakeRespServer.OkReply);
        internal Func<int, string, byte[]?>? FirstOverride;
        internal Func<int, string, byte[]?>? SecondOverride;

        internal Cluster(int protocol)
        {
            _protocol = protocol;
            First.ReplyOverride = (id, command) => FirstOverride?.Invoke(id, command) ?? Reply(command);
            Second.ReplyOverride = (id, command) => SecondOverride?.Invoke(id, command) ?? Reply(command);
        }
        internal RespireClient CreateClient(RespireReconnectPolicy? policy = null, ILoggerFactory? logger = null) => RespireClient.Create(new RespireOptions
        {
            UseCluster = true, Protocol = (RespProtocol)_protocol, Connections = 1, ReconnectPolicy = policy,
            LoggerFactory = logger,
            Endpoints = { new RespireEndpoint("127.0.0.1", First.Port) }, ConnectTimeout = TimeSpan.FromSeconds(1),
        });
        private byte[] Reply(string command)
        {
            if (command.StartsWith("HELLO ")) return "%1\r\n+proto\r\n:3\r\n"u8.ToArray();
            if (command == "ASKING") return "+OK\r\n"u8.ToArray();
            if (command == "CLUSTER SLOTS") return Encoding.ASCII.GetBytes(
                $"*2\r\n*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{First.Port}\r\n" +
                $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{Second.Port}\r\n");
            var parts = command.Split(' ', 2);
            if (parts[0] is "SSUBSCRIBE" or "SUNSUBSCRIBE" or "SUBSCRIBE" or "UNSUBSCRIBE")
                return Confirmation(parts[0].ToLowerInvariant(), parts[1]);
            return ":1\r\n"u8.ToArray();
        }
        internal byte[] Confirmation(string verb, string channel) => Frame(Encoding.UTF8.GetBytes(verb), Encoding.UTF8.GetBytes(channel), null);
        internal byte[] Message(string channel, string text) => Frame("smessage"u8.ToArray(), Encoding.UTF8.GetBytes(channel), Encoding.UTF8.GetBytes(text));
        private byte[] Frame(byte[] verb, byte[] channel, byte[]? payload)
        {
            using var stream = new MemoryStream();
            stream.Write(Encoding.ASCII.GetBytes(_protocol == 3 ? ">3\r\n" : "*3\r\n"));
            foreach (var value in new[] { verb, channel, payload })
            {
                if (value is null) stream.Write(":1\r\n"u8);
                else
                {
                    stream.Write(Encoding.ASCII.GetBytes($"${value.Length}\r\n"));
                    stream.Write(value);
                    stream.Write("\r\n"u8);
                }
            }
            return stream.ToArray();
        }
        internal Task SendAsync(FakeRespServer server, string channel, string text)
            => server.SendRawAsync(Message(channel, text), ControlIds(server).Last());
        public async ValueTask DisposeAsync()
        {
            await First.DisposeAsync();
            await Second.DisposeAsync();
        }
    }
}
