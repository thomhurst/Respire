using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SentinelMonitoringTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Test]
    public async Task DnsStartSharesStopGateAndRejectsLateEvidence()
    {
        using var lifetime = new CancellationTokenSource();
        var gate = new object();
        var monitor = new SentinelMonitoring(new() { SentinelPrimaryName = "service" }, null,
            gate, new([]), lifetime, (_, _, _, _) => ValueTask.CompletedTask, (_, _, _) => { });
        var reply = new TaskCompletionSource<System.Net.IPAddress[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queries = new List<string>();
        var startedUnderGate = false;
        monitor.HostResolver = (host, _) =>
        {
            startedUnderGate = Monitor.IsEntered(gate);
            queries.Add(host);
            return reply.Task;
        };
        var resolution = monitor.ResolveAddressesAsync("old.alias", default).AsTask();
        monitor.Stop();
        // No token cancellation is required to close ownership. The resolver may ignore
        // cancellation, and linked cancellation callbacks may not have run yet.
        reply.SetResult([System.Net.IPAddress.Loopback]);
        var result = await resolution.WaitAsync(Limit);
        await Assert.That(startedUnderGate).IsTrue();
        await Assert.That(result).IsNull();
        await Assert.That(await monitor.ResolveAddressesAsync("new.alias", default)).IsNull();
        await Assert.That(await monitor.ResolveAddressesAsync("127.0.0.1", default)).IsNull();
        await Assert.That(queries).IsEquivalentTo(new[] { "old.alias" });
    }

    [Test]
    public async Task ProbeDoesNotReportCancellationForNormalCompletion()
    {
        using var lifetime = new CancellationTokenSource();
        var probe = new SentinelMonitorProbe();
        await using var client = probe.Client;
        await using var subscription = await client.SubscribeAsync(lifetime.Token);
        await using var messages = subscription.GetAsyncEnumerator(lifetime.Token);
        probe.Messages.Writer.TryComplete();
        await Assert.That(await messages.MoveNextAsync()).IsFalse();
        await Assert.That(probe.Cancelled.Task.IsCompleted).IsFalse();
    }

    [Test]
    public async Task ProbeReportsObservedCancellationWhenCleanupRemovesItsCallback()
    {
        using var lifetime = new CancellationTokenSource();
        var probe = new SentinelMonitorProbe();
        await using var client = probe.Client;
        await using var subscription = await client.SubscribeAsync(lifetime.Token);
        await using var messages = subscription.GetAsyncEnumerator(lifetime.Token);
        var read = messages.MoveNextAsync().AsTask();
        // Token callbacks run in reverse registration order. Force cleanup to remove
        // the probe's earlier callback before it can signal Cancelled, as a completed
        // cancellation-aware read can do during real monitor teardown.
        using var cleanup = lifetime.Token.Register(() =>
        {
            var disposal = subscription.DisposeAsync();
            if (!disposal.IsCompleted)
                throw new InvalidOperationException("Probe cleanup must complete synchronously for this ordering test.");
            disposal.GetAwaiter().GetResult();
        });
        await lifetime.CancelAsync();
        await Assert.That(() => read).Throws<OperationCanceledException>();
        await Assert.That(probe.SubscriptionCleanup.Task.IsCompletedSuccessfully).IsTrue();
        await Assert.That(probe.Cancelled.Task.IsCompletedSuccessfully).IsTrue();
    }

    [Test]
    public async Task ValidationCoversOnlySubscriptionsPresentBeforeDiscovery()
    {
        using var lifetime = new CancellationTokenSource();
        var gaps = new List<(RespireEndpoint, bool)>();
        var monitor = Create(lifetime, (endpoint, initial) => gaps.Add((endpoint, initial)));
        var first = new RespireEndpoint("first", 26379);
        var second = new RespireEndpoint("second", 26379);
        monitor.SubscriptionEstablished(first, true);
        var lookup = monitor.SubscriptionVersion;
        monitor.SubscriptionEstablished(second, true);
        monitor.Validated(first, lookup);
        await Assert.That(monitor.NeedsStartupValidation(first, lookup)).IsFalse();
        await Assert.That(monitor.NeedsStartupValidation(second, monitor.SubscriptionVersion)).IsTrue();
        // A newer lookup of the first reporter cannot cover the second reporter's view.
        monitor.Validated(first, monitor.SubscriptionVersion);
        await Assert.That(monitor.NeedsStartupValidation(second, monitor.SubscriptionVersion)).IsTrue();
        monitor.Validated(second, monitor.SubscriptionVersion);
        monitor.Validated(second, lookup); // A late older completion cannot reopen a covered gap.
        await Assert.That(monitor.NeedsStartupValidation(second, monitor.SubscriptionVersion)).IsFalse();
        monitor.SubscriptionEstablished(new("first", 26379), false);
        await Assert.That(monitor.SubscriptionVersion).IsEqualTo(2L);
        await Assert.That(gaps.Count).IsEqualTo(3);
        await Assert.That(gaps[2].Item2).IsFalse();
        monitor.Stop();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task IndependentGapCannotBeSkippedAfterStartupValidation(bool reverse)
    {
        var startup = SentinelHint.FromGap(new("first", 26379)) with { StartupSubscriptionVersion = 2 };
        var independent = SentinelHint.FromGap(new("second", 26379));
        var mixed = reverse ? SentinelNotificationCoalescer.Merge(independent, startup)
            : SentinelNotificationCoalescer.Merge(startup, independent);
        await Assert.That(mixed.StartupSubscriptionVersion).IsEqualTo(0L);
        await Assert.That(mixed.MustRediscover).IsTrue();
        var otherStartup = independent with { StartupSubscriptionVersion = 3 };
        var merged = SentinelNotificationCoalescer.Merge(startup, otherStartup);
        await Assert.That(merged.StartupSubscriptionVersion).IsEqualTo(3L);
    }

    [Test]
    public async Task RestartedMonitorSubscriptionIsAnIndependentGap()
    {
        using var lifetime = new CancellationTokenSource();
        var gaps = new List<bool>();
        var monitor = Create(lifetime, (_, initial) => gaps.Add(initial));
        var endpoint = new RespireEndpoint("sentinel", 26379);
        monitor.SubscriptionEstablished(endpoint, true);
        monitor.Validated(endpoint, monitor.SubscriptionVersion);
        // A replacement monitor task starts with its local subscribedBefore=false.
        monitor.SubscriptionEstablished(endpoint, true);
        await Assert.That(gaps).IsEquivalentTo(new[] { true, false });
        await Assert.That(monitor.SubscribedCount).IsEqualTo(1);
        monitor.Stop();
    }

    [Test]
    public async Task BlockingGapCallbackDoesNotHoldDisposalGate()
    {
        using var lifetime = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = Create(lifetime, (_, _) =>
        {
            started.TrySetResult();
            if (!release.Wait(Limit)) throw new TimeoutException("Gap callback was not released.");
        });
        var subscription = Task.Run(() => monitor.SubscriptionEstablished(new("sentinel", 26379), true));
        Task? stopped = null;
        try
        {
            await started.Task.WaitAsync(Limit);
            await Assert.That(monitor.SubscribedCount).IsEqualTo(0);
            stopped = Task.Run(monitor.Stop);
            await stopped.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
            await subscription.WaitAsync(Limit);
            if (stopped is not null) await stopped.WaitAsync(Limit);
        }
    }

    [Test]
    public async Task StoppedMonitorRejectsLateSubscriptionAndPublication()
    {
        using var lifetime = new CancellationTokenSource();
        var gaps = 0;
        var monitor = Create(lifetime, (_, _) => gaps++);
        var signal = monitor.CurrentMonitorRearm();
        monitor.Stop();
        monitor.SubscriptionEstablished(new("late", 26379), true);
        monitor.Published();
        await Assert.That(gaps).IsEqualTo(0);
        await Assert.That(monitor.SubscribedCount).IsEqualTo(0);
        await Assert.That(signal.IsCompleted).IsFalse();
        await Assert.That(monitor.Stop().All(task => task.IsCompleted)).IsTrue();
    }

    [Test]
    public async Task ComponentOwnsStartupParsingReconnectAndShutdown()
    {
        await using var sentinel = new FakeRespServer(16, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("SUBSCRIBE ")
                ? Encoding.ASCII.GetBytes(string.Concat(command.Split(' ').Skip(1).Select((channel, index) =>
                    $"*3\r\n$9\r\nsubscribe\r\n${channel.Length}\r\n{channel}\r\n:{index + 1}\r\n")))
                : FakeRespServer.OkReply,
        };
        using var lifetime = new CancellationTokenSource();
        var endpoint = new RespireEndpoint("127.0.0.1", sentinel.Port);
        var startup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gap = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource<SentinelEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var options = new RespireOptions
        {
            SentinelPrimaryName = "service", ConnectTimeout = Limit,
            ReconnectPolicy = new() { InitialDelay = TimeSpan.FromMilliseconds(10), JitterRatio = 0 },
        };
        var monitor = new SentinelMonitoring(options, null, new object(), new([endpoint]), lifetime,
            (_, parsed, _, _) => { Interlocked.Increment(ref count); received.TrySetResult(parsed); return ValueTask.CompletedTask; },
            (_, version, _) => { if (version > 0) startup.TrySetResult(); else gap.TrySetResult(); });
        try
        {
            monitor.Published();
            await startup.Task.WaitAsync(Limit);
            await Send("other 127.0.0.1 6379 127.0.0.1 6380");
            await Send("service 127.0.0.1 6379 127.0.0.1 6380");
            var parsed = await received.Task.WaitAsync(Limit);
            await Assert.That(parsed.NewPrimary).IsEqualTo(new RespireEndpoint("127.0.0.1", 6380));
            await Assert.That(Volatile.Read(ref count)).IsEqualTo(1);
            var publication = monitor.CurrentMonitorRearm();
            await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(monitor.Published)));
            await publication.WaitAsync(Limit);
            sentinel.CloseConnections();
            await gap.Task.WaitAsync(Limit);
            await Assert.That(monitor.SubscribedCount).IsEqualTo(1);
        }
        finally
        {
            var tasks = monitor.Stop();
            lifetime.Cancel();
            await Task.WhenAll(tasks).WaitAsync(Limit);
        }

        Task Send(string text)
        {
            var index = sentinel.ReceivedCommands.ToList().FindIndex(command => command.StartsWith("SUBSCRIBE "));
            return sentinel.SendRawAsync(Encoding.ASCII.GetBytes(
                $"*3\r\n$7\r\nmessage\r\n$14\r\n+switch-master\r\n${text.Length}\r\n{text}\r\n"),
                sentinel.ReceivedConnectionIds[index]);
        }
    }

    private static SentinelMonitoring Create(CancellationTokenSource lifetime, Action<RespireEndpoint, bool> gap)
        => new(new() { SentinelPrimaryName = "service" }, null, new object(), new([]), lifetime,
            (_, _, _, _) => ValueTask.CompletedTask, (endpoint, version, _) => gap(endpoint, version > 0));

    [Test]
    public async Task ReadinessWaitRequiresAllValidatedCallbacksAndHonorsCancellationAndStop()
    {
        using var lifetime = new CancellationTokenSource();
        var fail = true;
        var monitor = Create(lifetime, (_, _) => { if (fail) throw new IOException("queue failed"); });
        using var deadline = new CancellationTokenSource(Limit);
        var ready = monitor.WaitForSubscriptionsAsync(2, deadline.Token);
        await Assert.That(() => monitor.SubscriptionEstablished(new("first", 26379), true)).ThrowsExactly<IOException>();
        await Assert.That(monitor.SubscribedCount).IsEqualTo(0);
        await Assert.That(ready.IsCompleted).IsFalse();
        fail = false;
        monitor.SubscriptionEstablished(new("first", 26379), false);
        await Assert.That(ready.IsCompleted).IsFalse();
        monitor.SubscriptionEstablished(new("second", 26379), true);
        await ready.WaitAsync(Limit);
        using var caller = new CancellationTokenSource();
        var cancelled = monitor.WaitForSubscriptionsAsync(3, caller.Token);
        caller.Cancel();
        var error = await Assert.That(() => cancelled).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
        var stopped = monitor.WaitForSubscriptionsAsync(3, deadline.Token);
        monitor.Stop();
        await Assert.That(() => stopped).ThrowsExactly<ObjectDisposedException>();
    }

    [Test]
    public async Task RecoveryCleanupAttemptsBothResourcesBeforePropagatingFatalFailures()
    {
        using var lifetime = new CancellationTokenSource();
        var first = new OutOfMemoryException("explicitly injected subscription cleanup failure");
        var second = new IOException("client cleanup failure");
        var probe = new SentinelMonitorProbe
        {
            DisposeSubscription = () => ValueTask.FromException(first),
            DisposeClient = () => ValueTask.FromException(second),
        };
        var monitor = new SentinelMonitoring(new() { SentinelPrimaryName = "service" }, null,
            new object(), new([new RespireEndpoint("seed", 26379)]), lifetime,
            (_, _, _, _) => ValueTask.CompletedTask, (_, _, _) => { }) { ClientFactory = _ => probe.Client };
        monitor.Published();
        using var deadline = new CancellationTokenSource(Limit);
        await monitor.WaitForSubscriptionsAsync(1, deadline.Token);
        probe.Messages.Writer.TryComplete();
        // A fatal unsubscribe failure must not skip client cleanup or become a
        // recoverable aggregate that restarts this episode.
        await probe.ClientCleanup.Task.WaitAsync(Limit);
        var tasks = monitor.Stop();
        await lifetime.CancelAsync();
        var error = await Assert.That(() => CleanupTasks.WhenAllAsync(tasks).WaitAsync(Limit)).ThrowsExactly<AggregateException>();
        await Assert.That(error!.Flatten().InnerExceptions).Contains(first);
        await Assert.That(error.Flatten().InnerExceptions).Contains(second);
    }

    [Test]
    public async Task RemovalAndReadditionBetweenSnapshotsRestartsTheMonitor()
    {
        using var lifetime = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(Limit);
        using var clock = new PausedSupervisorClock();
        var seed = new RespireEndpoint("seed", 26379);
        var peer = new RespireEndpoint("peer", 26379);
        var discovery = new SentinelDiscoveryState([seed]);
        discovery.TryAdd(peer);
        var first = new SentinelMonitorProbe();
        var removed = new SentinelMonitorProbe();
        var replacement = new SentinelMonitorProbe();
        var starts = 0;
        var gaps = new System.Collections.Concurrent.ConcurrentQueue<(RespireEndpoint Endpoint, long Version)>();
        var monitor = new SentinelMonitoring(new() { SentinelPrimaryName = "service" }, null,
            new object(), discovery, lifetime, (_, _, _, _) => ValueTask.CompletedTask,
            (endpoint, version, _) => gaps.Enqueue((endpoint, version)))
        {
            Clock = clock,
            ClientFactory = options => options.Endpoints[0] == seed ? first.Client
                : Interlocked.Increment(ref starts) == 1 ? removed.Client : replacement.Client,
        };
        try
        {
            monitor.Published();
            await clock.Waiting.Task.WaitAsync(Limit);
            await monitor.WaitForSubscriptionsAsync(2, deadline.Token);
            await Assert.That(discovery.TryRemove(peer)).IsTrue();
            await Assert.That(discovery.TryAdd(peer)).IsTrue();
            // The supervisor has not returned from its first wait yet, so it can only
            // observe the final membership containing this same endpoint address.
            clock.Release.Set();
            await removed.Cancelled.Task.WaitAsync(Limit);
            await removed.ClientCleanup.Task.WaitAsync(Limit);
            await removed.SubscriptionCleanup.Task.WaitAsync(Limit);
            await monitor.WaitForSubscriptionsAsync(2, deadline.Token);
            await Assert.That(starts).IsEqualTo(2);
            await Assert.That(gaps.Where(gap => gap.Endpoint == peer).Select(gap => gap.Version == 0).ToArray())
                .IsEquivalentTo(new[] { false, true });
        }
        finally
        {
            clock.Release.Set();
            var tasks = monitor.Stop();
            await lifetime.CancelAsync();
            await CleanupTasks.WhenAllAsync(tasks).WaitAsync(Limit);
        }
    }

    private sealed class PausedSupervisorClock : TimeProvider, IDisposable
    {
        internal readonly TaskCompletionSource Waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Release = new();
        private int _waits;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (Interlocked.Increment(ref _waits) == 1)
            {
                Waiting.TrySetResult();
                if (!Release.Wait(Limit)) throw new TimeoutException("Supervisor snapshot was not released.");
            }
            return System.CreateTimer(callback, state, dueTime, period);
        }
        public void Dispose() => Release.Dispose();
    }

    [Test]
    public async Task DiscoveryRemovalCancelsAndJoinsMonitorAndRejectsItsLateMessages()
    {
        using var lifetime = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(Limit);
        var gate = new object();
        var background = new SentinelBackgroundWork(gate);
        var seed = new RespireEndpoint("seed", 26379);
        var peer = new RespireEndpoint("peer", 26379);
        var discovery = new SentinelDiscoveryState([seed]);
        var first = new SentinelMonitorProbe();
        var removed = new SentinelMonitorProbe { IgnoreCancellation = true };
        var replacement = new SentinelMonitorProbe();
        var peerStarts = 0;
        var received = 0;
        var gaps = new System.Collections.Concurrent.ConcurrentQueue<(RespireEndpoint Endpoint, long Version)>();
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = new SentinelMonitoring(new() { SentinelPrimaryName = "service" }, null, gate, discovery, lifetime,
            (_, _, _, _) => { Interlocked.Increment(ref received); observed.TrySetResult(); return ValueTask.CompletedTask; },
            (endpoint, version, _) => gaps.Enqueue((endpoint, version)), background)
        {
            ClientFactory = options => options.Endpoints[0] == seed ? first.Client
                : Interlocked.Increment(ref peerStarts) == 1 ? removed.Client : replacement.Client,
        };
        try
        {
            monitor.Published();
            await monitor.WaitForSubscriptionsAsync(1, deadline.Token);
            await Assert.That(discovery.TryAdd(peer)).IsTrue();
            await monitor.WaitForSubscriptionsAsync(2, deadline.Token);
            var message = new RespireMessage("+switch-master", null,
                "service 127.0.0.1 6379 127.0.0.1 6380"u8.ToArray(), null!);
            removed.Messages.Writer.TryWrite(message);
            await observed.Task.WaitAsync(Limit);
            await Assert.That(Volatile.Read(ref received)).IsEqualTo(1);
            await Assert.That(discovery.TryRemove(seed)).IsFalse();
            await Assert.That(discovery.TryRemove(peer)).IsTrue();
            await removed.Cancelled.Task.WaitAsync(Limit);
            await Assert.That(monitor.SubscribedCount).IsEqualTo(1);
            await Assert.That(background.Count(SentinelWorkKind.MonitorRemoval)).IsEqualTo(1);
            removed.Messages.Writer.TryWrite(message);
            removed.Messages.Writer.TryComplete();
            await removed.SubscriptionCleanup.Task.WaitAsync(Limit);
            await Assert.That(Volatile.Read(ref received)).IsEqualTo(1);
            await Assert.That(discovery.TryAdd(peer)).IsTrue();
            await monitor.WaitForSubscriptionsAsync(2, deadline.Token);
            await Assert.That(peerStarts).IsEqualTo(2);
            await Assert.That(gaps.Last().Version).IsEqualTo(0L);
        }
        finally
        {
            removed.Messages.Writer.TryComplete();
            var tasks = monitor.Stop();
            await lifetime.CancelAsync();
            await CleanupTasks.WhenAllAsync(tasks).WaitAsync(Limit);
        }
    }
}
