using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Coordination.Tests;

public partial class RedlockWireTests
{
    private static readonly byte[] Ok = "+OK\r\n"u8.ToArray();
    private static readonly byte[] Nil = "$-1\r\n"u8.ToArray();
    private static readonly byte[] Zero = ":0\r\n"u8.ToArray();
    private static readonly byte[] One = ":1\r\n"u8.ToArray();
    private static readonly byte[] ReleaseError = "-ERR injected release failure\r\n"u8.ToArray();

    [Test]
    [Arguments(10_000, 0, 0.01, 9_898)]
    [Arguments(10_000, 1_000, 0.0, 8_998)]
    [Arguments(10, 8, 0.0, 0)]
    [Arguments(10, 9, 0.0, 0)]
    [Arguments(1_000, 0, 0.999, 0)]
    public async Task ValiditySubtractsElapsedTimeAndDrift(int durationMs, int elapsedMs, double drift, int expectedMs)
    {
        var validity = RespireRedlockNodes.CalculateValidity(
            TimeSpan.FromMilliseconds(durationMs), TimeSpan.FromMilliseconds(elapsedMs), drift);
        await Assert.That(validity).IsEqualTo(TimeSpan.FromMilliseconds(expectedMs));
    }

    [Test]
    public async Task ValidityDoesNotOverflowForTheLongestDuration()
    {
        var validity = RespireRedlockNodes.CalculateValidity(TimeSpan.MaxValue, TimeSpan.Zero, 0.5);
        await Assert.That(validity > TimeSpan.Zero && validity < TimeSpan.MaxValue).IsTrue();
    }

    [Test]
    public async Task ValidityAndRemainingEstimateFollowTheLeaseClock()
    {
        await using var nodes = await Nodes.StartAsync(static (_, _) => null);
        var clock = new ManualClock();
        var group = new RespireRedlockGroup(nodes.Clients, timeProvider: clock);
        await using var attempt = await group.TryAcquireAsync("redlock:clock", TimeSpan.FromSeconds(10));
        await Assert.That(attempt.Acquired).IsTrue();
        var lease = attempt.Lock;
        await Assert.That(lease.Validity).IsEqualTo(TimeSpan.FromMilliseconds(9_898));
        await Assert.That(lease.RemainingEstimate).IsEqualTo(TimeSpan.FromMilliseconds(9_898));

        clock.Advance(TimeSpan.FromSeconds(5));
        await Assert.That(await lease.ResetExpiryAsync(TimeSpan.FromSeconds(20))).IsTrue();
        await Assert.That(lease.Duration).IsEqualTo(TimeSpan.FromSeconds(20));
        await Assert.That(lease.Validity).IsEqualTo(TimeSpan.FromMilliseconds(19_798));
        await Assert.That(lease.RemainingEstimate).IsEqualTo(TimeSpan.FromMilliseconds(19_798));

        clock.Advance(TimeSpan.FromMilliseconds(19_798));
        await Assert.That(lease.RemainingEstimate).IsEqualTo(TimeSpan.Zero);
        await Assert.That(lease.IsReleased).IsTrue();
        var commandsBefore = nodes.CommandsSeen;
        await Assert.That(await lease.ResetExpiryAsync(TimeSpan.FromSeconds(20))).IsFalse();
        await Assert.That(nodes.CommandsSeen).IsEqualTo(commandsBefore);
    }

    [Test]
    public async Task RemainingEstimateRetriesWhenLeaseChangesDuringClockRead()
    {
        await using var nodes = await Nodes.StartAsync(static (_, _) => null);
        var clock = new ManualClock();
        var group = new RespireRedlockGroup(nodes.Clients, timeProvider: clock);
        await using var attempt = await group.TryAcquireAsync("redlock:remaining-race", TimeSpan.FromSeconds(10));
        var lease = attempt.Lock;
        var timestampRead = clock.BlockNextTimestampRead();
        var remainingTask = Task.Run(() => lease.RemainingEstimate);
        await timestampRead.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            await Assert.That(await lease.ResetExpiryAsync(TimeSpan.FromSeconds(20))).IsTrue();
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        finally
        {
            timestampRead.Release.TrySetResult();
        }

        var remaining = await remainingTask.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(remaining).IsEqualTo(TimeSpan.FromMilliseconds(18_798));
    }

    [Test]
    public async Task RemainingEstimateRechecksReleaseAfterClockRead()
    {
        await using var nodes = await Nodes.StartAsync(static (_, _) => null);
        var clock = new ManualClock();
        var group = new RespireRedlockGroup(nodes.Clients, timeProvider: clock);
        await using var attempt = await group.TryAcquireAsync("redlock:release-race", TimeSpan.FromSeconds(10));
        var timestampRead = clock.BlockNextTimestampRead();
        var remainingTask = Task.Run(() => attempt.Lock.RemainingEstimate);
        await timestampRead.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            await Assert.That(await attempt.Lock.ReleaseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
        }
        finally
        {
            timestampRead.Release.TrySetResult();
        }

        await Assert.That(await remainingTask.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo(TimeSpan.Zero);
    }

    [Test]
    public async Task ShorterRenewalBoundsVisibleLeaseBeforeNodeReplies()
    {
        await using var nodes = await Nodes.StartAsync(static (_, _) => null);
        foreach (var server in nodes.Servers) server.DelayReply(1, 250);
        var group = new RespireRedlockGroup(nodes.Clients);
        await using var attempt = await group.TryAcquireAsync("redlock:short-renew", TimeSpan.FromSeconds(10));
        var renewal = attempt.Lock.ResetExpiryAsync(TimeSpan.FromSeconds(1)).AsTask();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (nodes.CommandsSeen < nodes.Servers.Length * 2) await Task.Delay(5, timeout.Token);

        var remaining = attempt.Lock.RemainingEstimate;
        await Assert.That(remaining > TimeSpan.Zero && remaining <= TimeSpan.FromSeconds(1)).IsTrue();
        await Assert.That(await renewal.WaitAsync(timeout.Token)).IsTrue();
    }

    [Test]
    public async Task RenewalWithoutQuorumEndsTheLeaseAndReleasesEveryNode()
    {
        await using var nodes = await Nodes.StartAsync(static (node, command) =>
            node < 2 && command.StartsWith("SET ", StringComparison.Ordinal) && command.Contains(" IFEQ ", StringComparison.Ordinal)
                ? Nil
                : null);
        var group = new RespireRedlockGroup(nodes.Clients);
        await using var attempt = await group.TryAcquireAsync("redlock:renew", TimeSpan.FromSeconds(10));
        await Assert.That(attempt.Acquired).IsTrue();

        await Assert.That(await attempt.Lock.ResetExpiryAsync(TimeSpan.FromSeconds(10))).IsFalse();
        await Assert.That(attempt.Lock.IsReleased).IsTrue();
        await Assert.That(attempt.Lock.RemainingEstimate).IsEqualTo(TimeSpan.Zero);
        foreach (var server in nodes.Servers)
            await Assert.That(server.ReceivedCommands.Count(static c => c.StartsWith("DELEX ", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(await attempt.Lock.ReleaseAsync()).IsFalse();
    }

    [Test]
    public async Task RenewalCompletingAfterPreviousValidityCannotRestoreOwnership()
    {
        await using var nodes = await Nodes.StartAsync(static (_, _) => null, untimedCommands: true);
        var clock = new ManualClock();
        var group = new RespireRedlockGroup(nodes.Clients,
            new RespireRedlockOptions { NodeTimeout = RespireRedlockNodes.MaxNodeTimeout }, timeProvider: clock);
        await using var attempt = await group.TryAcquireAsync("redlock:late-renewal", TimeSpan.FromMilliseconds(100));
        await Assert.That(attempt.Acquired).IsTrue();

        // Advance the lease clock only after all renewal commands arrive, and before any
        // can complete. A fixed reply delay can elapse while the test is descheduled.
        var renewalReplies = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewalReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = 0;
        foreach (var server in nodes.Servers)
            server.GateCommand("SET ", renewalReplies.Task, () =>
            {
                if (Interlocked.Increment(ref received) == nodes.Servers.Length)
                    renewalReceived.TrySetResult();
            });
        using var cancellation = new CancellationTokenSource();
        try
        {
            var renewal = attempt.Lock.ResetExpiryAsync(TimeSpan.FromSeconds(10), cancellation.Token).AsTask();
            await renewalReceived.Task.WaitAsync(TimeSpan.FromSeconds(30));
            clock.Advance(TimeSpan.FromMilliseconds(100));
            renewalReplies.SetResult();

            await Assert.That(await renewal.WaitAsync(TimeSpan.FromSeconds(30))).IsFalse();
            await Assert.That(attempt.Lock.IsReleased).IsTrue();
            await Assert.That(attempt.Lock.RemainingEstimate).IsEqualTo(TimeSpan.Zero);
            foreach (var server in nodes.Servers)
                await Assert.That(server.ReceivedCommands.Count(static command => command.StartsWith("DELEX ", StringComparison.Ordinal)))
                    .IsEqualTo(1);
        }
        finally
        {
            cancellation.Cancel();
            renewalReplies.TrySetResult();
        }
    }

    [Test]
    public async Task CallerCancellationDuringRenewalEndsLeaseAndCleansUpEveryNode()
    {
        var hooks = CreateHooks();
        await using var nodes = await Nodes.StartAsync(static (_, _) => null, untimedCommands: true, hooks: hooks);
        var group = new RespireRedlockGroup(nodes.Clients, new RespireRedlockOptions { NodeTimeout = RespireRedlockNodes.MaxNodeTimeout });
        await using var attempt = await group.TryAcquireAsync("redlock:renew-cancel", TimeSpan.FromSeconds(10));
        await Assert.That(attempt.Acquired).IsTrue();

        // Hold the renewal SET replies until the test has cancelled. A wall-clock cancel could
        // fire before renewal contacts any node, and then nothing would end the lease.
        var renewalReplies = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (var server in nodes.Servers) server.GateCommand("SET ", renewalReplies.Task);
        using var cancellation = new CancellationTokenSource();
        try
        {
            var renewal = attempt.Lock.ResetExpiryAsync(TimeSpan.FromSeconds(10), cancellation.Token).AsTask();
            // The limit only bounds a failing run; a passing run never depends on it.
            using (var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                while (!nodes.Servers.All(static server => server.ReceivedCommands.Count(static c => c.StartsWith("SET ", StringComparison.Ordinal)) == 2))
                    await Task.Delay(1, limit.Token);
            }
            cancellation.Cancel();
            renewalReplies.SetResult();
            await Assert.That(async () => await renewal).Throws<OperationCanceledException>();
            await Assert.That(attempt.Lock.IsReleased).IsTrue();
            foreach (var hook in hooks)
                await Assert.That(hook.Completed.Single(static e => e.Context.Command == "REDLOCK RENEW").Outcome)
                    .IsEqualTo(RespireCommandOutcome.Canceled);
            foreach (var server in nodes.Servers)
                await Assert.That(server.ReceivedCommands.Count(static command => command.StartsWith("DELEX ", StringComparison.Ordinal))).IsEqualTo(1);
        }
        finally
        {
            // Untimed clients wait on the gate for as long as it stays closed. On any early exit,
            // cancel and open it before disposal: disposing the attempt waits for the renewal.
            cancellation.Cancel();
            renewalReplies.TrySetResult();
        }
    }

    [Test]
    public async Task ReleaseCompletesOnEveryNodeDespiteCallerCancellation()
    {
        await using var nodes = await Nodes.StartAsync(static (_, _) => null, delayReleaseMs: 300);
        var group = new RespireRedlockGroup(nodes.Clients, new RespireRedlockOptions { NodeTimeout = TimeSpan.FromSeconds(5) });
        await using var attempt = await group.TryAcquireAsync("redlock:release", TimeSpan.FromSeconds(10));
        await Assert.That(attempt.Acquired).IsTrue();

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.That(await attempt.Lock.ReleaseAsync(cancellation.Token)).IsTrue();
        await Assert.That(cancellation.IsCancellationRequested).IsTrue();
        await Assert.That(attempt.Lock.IsReleased).IsTrue();
    }

    [Test]
    public async Task ReleaseWithoutQuorumCanRetryAndConfirmAbsentTokens()
    {
        await using var nodes = await Nodes.StartAsync(static (_, _) => null);
        var releaseReplies = new int[2];
        for (var i = 0; i < releaseReplies.Length; i++)
        {
            var node = i;
            nodes.Servers[node].ReplyOverride = (_, command) =>
            {
                if (!command.StartsWith("DELEX ", StringComparison.Ordinal)) return null;
                return Interlocked.Increment(ref releaseReplies[node]) == 1 ? ReleaseError : Zero;
            };
        }

        var group = new RespireRedlockGroup(nodes.Clients);
        await using var attempt = await group.TryAcquireAsync("redlock:release-retry", TimeSpan.FromSeconds(10));
        await Assert.That(attempt.Acquired).IsTrue();

        await Assert.That(await attempt.Lock.ReleaseAsync()).IsFalse();
        await Assert.That(attempt.Lock.IsReleased).IsTrue();
        await Assert.That(await attempt.Lock.ReleaseAsync()).IsTrue();
        var commandsAfterRetry = nodes.CommandsSeen;
        await Assert.That(await attempt.Lock.ReleaseAsync()).IsFalse();
        await Assert.That(nodes.CommandsSeen).IsEqualTo(commandsAfterRetry);
    }

    [Test]
    public async Task UnrepresentableDurationIsRejectedBeforeAnyNodeCommand()
    {
        await using var nodes = await Nodes.StartAsync(static (_, _) => null);
        var group = new RespireRedlockGroup(nodes.Clients, timeProvider: new ManualClock());
        await Assert.That(async () => await group.TryAcquireAsync("redlock:overflow", TimeSpan.MaxValue))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(nodes.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    public async Task ValidityBelowOneClockTickIsNotReportedAsAcquired()
    {
        await using var nodes = await Nodes.StartAsync(static (_, _) => null);
        // At 100 Hz, the 2.95 ms validity of a 5 ms lease truncates to zero timestamp ticks.
        var group = new RespireRedlockGroup(nodes.Clients, timeProvider: new ManualClock(frequency: 100));
        await using var attempt = await group.TryAcquireAsync("redlock:coarse", TimeSpan.FromMilliseconds(5));
        await Assert.That(attempt.Acquired).IsFalse();
        foreach (var server in nodes.Servers)
            await Assert.That(server.ReceivedCommands.Count(static c => c.StartsWith("DELEX ", StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Test]
    public async Task QuorumSurvivesOneNodeThatNeverReplies()
    {
        await using var nodes = await Nodes.StartAsync(static (_, _) => null);
        nodes.Servers[2].SuppressReply = static command => command.StartsWith("SET ", StringComparison.Ordinal);
        // The timeout must also cover the healthy nodes' first SET, which can take more than
        // 100 ms when JIT and parallel tests load the runner. A healthy node that times out
        // would cost the quorum.
        var nodeTimeout = TimeSpan.FromSeconds(1);
        var group = new RespireRedlockGroup(nodes.Clients, new RespireRedlockOptions { NodeTimeout = nodeTimeout });
        await using var attempt = await group.TryAcquireAsync("redlock:silent", TimeSpan.FromSeconds(10));
        await Assert.That(attempt.Acquired).IsTrue();
        await Assert.That(attempt.Lock.Validity < TimeSpan.FromSeconds(10) - nodeTimeout).IsTrue();
    }

    [Test]
    public async Task CallerCancellationDuringAcquisitionReleasesEveryNode()
    {
        // Every node withholds its SET reply until the test has cancelled, so acquisition is
        // still in flight when Cancel runs however long the runner stalls in between. The gated
        // clients have no command timeout and the node timeout is the maximum, so only the
        // caller's cancellation can end the SET wait; a timeout would start cleanup early.
        var setReplies = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = CreateHooks();
        await using var nodes = await Nodes.StartAsync(static (_, _) => null, untimedCommands: true, hooks: hooks);
        foreach (var server in nodes.Servers) server.GateCommand("SET ", setReplies.Task);
        var group = new RespireRedlockGroup(nodes.Clients, new RespireRedlockOptions { NodeTimeout = RespireRedlockNodes.MaxNodeTimeout });
        using var cancellation = new CancellationTokenSource();
        try
        {
            var acquisition = group.TryAcquireAsync("redlock:cancel", TimeSpan.FromSeconds(10), cancellation.Token).AsTask();
            // Cancel only once every node holds the SET. A wall-clock cancel could fire before
            // acquisition starts on a slow runner; no node is contacted then, so none needs a release.
            // The limit only bounds a failing run; a passing run never depends on it.
            using (var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                while (!nodes.Servers.All(static server => server.ReceivedCommands.Any(static c => c.StartsWith("SET ", StringComparison.Ordinal))))
                    await Task.Delay(1, limit.Token);
            }
            cancellation.Cancel();
            // The late SET replies now land on cancelled waiters; the gate also holds the DELEX
            // that follows on each single-connection node, so cleanup needs it open.
            setReplies.SetResult();
            await Assert.That(async () => await acquisition).Throws<OperationCanceledException>();
            foreach (var hook in hooks)
                await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Canceled);
            foreach (var server in nodes.Servers)
                await Assert.That(server.ReceivedCommands.Count(static c => c.StartsWith("DELEX ", StringComparison.Ordinal))).IsEqualTo(1);
        }
        finally
        {
            // Untimed clients wait on the gate for as long as it stays closed. On any early exit,
            // cancel and open it so the acquisition and its cleanup finish before disposal.
            cancellation.Cancel();
            setReplies.TrySetResult();
        }
    }

    [Test]
    public async Task CancellationBeforeAcquisitionContactsNoNode()
    {
        await using var nodes = await Nodes.StartAsync(static (_, _) => null);
        var group = new RespireRedlockGroup(nodes.Clients, new RespireRedlockOptions { NodeTimeout = TimeSpan.FromSeconds(5) });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () => await group.TryAcquireAsync("redlock:precancel", TimeSpan.FromSeconds(10), cancellation.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(nodes.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    public async Task NodeTimeoutBeyondTheTimerRangeIsRejected()
    {
        await using var nodes = await Nodes.StartAsync(static (_, _) => null);
        await Assert.That(() => new RespireRedlockGroup(nodes.Clients, new RespireRedlockOptions { NodeTimeout = TimeSpan.MaxValue }))
            .Throws<ArgumentOutOfRangeException>();
        _ = new RespireRedlockGroup(nodes.Clients, new RespireRedlockOptions { NodeTimeout = RespireRedlockNodes.MaxNodeTimeout });
    }

    private sealed class Nodes : IAsyncDisposable
    {
        private Nodes(FakeRespServer[] servers, RespireClient[] clients)
        {
            Servers = servers;
            Clients = clients;
        }

        public FakeRespServer[] Servers { get; }
        public RespireClient[] Clients { get; }
        public int CommandsSeen => Servers.Sum(static server => server.CommandsSeen);

        public static async Task<Nodes> StartAsync(Func<int, string, byte[]?> reply, int delayReleaseMs = 0,
            bool untimedCommands = false, RespireCommandHook?[]? hooks = null)
        {
            var servers = new FakeRespServer[3];
            var clients = new RespireClient[3];
            for (var i = 0; i < servers.Length; i++)
            {
                var node = i;
                servers[i] = new FakeRespServer(Ok)
                {
                    ReplyOverride = (_, command) => reply(node, command)
                        ?? (command.StartsWith("DELEX ", StringComparison.Ordinal) ? One : Ok),
                };
                if (delayReleaseMs > 0) servers[i].DelayReply(1, delayReleaseMs);
                clients[i] = !untimedCommands && hooks is null
                    ? await FakeRespServer.ConnectClientAsync(servers[i].Port)
                    // A gated command waits for the test, not the clock: disable the command timeout.
                    : await RespireClient.ConnectAsync(new RespireOptions
                    {
                        Protocol = RespireProtocol.Resp2,
                        Endpoints = { new RespireEndpoint("127.0.0.1", servers[i].Port) },
                        Connections = 1,
                        CommandTimeout = untimedCommands ? null : TimeSpan.FromSeconds(5),
                        CommandHook = hooks?[i],
                    });
            }

            return new Nodes(servers, clients);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var client in Clients) await client.DisposeAsync();
            foreach (var server in Servers) await server.DisposeAsync();
        }
    }

    private sealed class ManualClock(long frequency = TimeSpan.TicksPerSecond) : TimeProvider
    {
        private readonly long _frequency = frequency;
        private long _timestamp = frequency;
        private TaskCompletionSource? _timestampReadEntered;
        private TaskCompletionSource? _releaseTimestampRead;

        public override long TimestampFrequency => _frequency;

        public override long GetTimestamp()
        {
            var entered = Interlocked.Exchange(ref _timestampReadEntered, null);
            if (entered is not null)
            {
                entered.TrySetResult();
                Volatile.Read(ref _releaseTimestampRead)!.Task.GetAwaiter().GetResult();
            }
            return Volatile.Read(ref _timestamp);
        }

        public (TaskCompletionSource Entered, TaskCompletionSource Release) BlockNextTimestampRead()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _releaseTimestampRead, release);
            Volatile.Write(ref _timestampReadEntered, entered);
            return (entered, release);
        }

        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks * _frequency / TimeSpan.TicksPerSecond);
    }
}
