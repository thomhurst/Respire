using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Coordination.Tests;

public class LeaderElectionTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static RespireLeaderElection Election(IRespireClient client, Clock clock,
        TaskCompletionSource<RespireLeadership> elected, Func<RespireLeadership, Task>? revoked = null)
        => new(client, "{election}:lease", "{election}:fence", new()
        {
            LeaseDuration = Lease,
            OnElected = async term =>
            {
                elected.TrySetResult(term);
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, term.CancellationToken);
            },
            OnRevoked = revoked,
        }, clock);

    [Test]
    public async Task ContendersHaveOneLeaderAndShutdownHandsOffWithHigherFence()
    {
        var clock = new Clock();
        await using var server = new RespireFakeServer(clock);
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        await using var otherClient = await RespireClient.ConnectAsync(server.CreateOptions());
        var elected = Signal<RespireLeadership>();
        var successor = Signal<RespireLeadership>();
        using var first = Election(client, clock, elected);
        using var second = Election(otherClient, clock, successor);
        var changes = new ConcurrentQueue<RespireLeadershipChangedEventArgs>();
        first.LeadershipChanged += (_, args) => changes.Enqueue(args);
        // A broken observer must not suppress other observers or cleanup.
        first.LeadershipChanged += (_, _) => throw new InvalidOperationException("observer");
        await first.StartAsync(default);
        var term = await elected.Task.WaitAsync(Timeout);
        await second.StartAsync(default);
        await UntilAsync(() => clock.HasTimer(TimeSpan.FromSeconds(1)));
        await Assert.That(successor.Task.IsCompleted).IsFalse();
        await first.StopAsync(default).WaitAsync(Timeout);
        await Assert.That(term.CancellationToken.IsCancellationRequested).IsTrue();
        await Assert.That(first.CurrentLeadership).IsNull();
        clock.Advance(TimeSpan.FromSeconds(1));
        var next = await successor.Task.WaitAsync(Timeout);
        await Assert.That(next.FencingToken).IsGreaterThan(term.FencingToken);
        await Assert.That(changes.Select(x => x.IsLeader).SequenceEqual([true, false])).IsTrue();
        await second.StopAsync(default).WaitAsync(Timeout);
        await Assert.That(await client.GetStringAsync("{election}:lease")).IsNull();
        await Assert.That(await client.GetStringAsync("{election}:fence")).IsEqualTo("2");
    }

    [Test]
    public async Task RenewalPreservesFenceAndExtendsServerLease()
    {
        var clock = new Clock();
        await using var server = new RespireFakeServer(clock);
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        var elected = Signal<RespireLeadership>();
        using var election = Election(client, clock, elected);
        await election.StartAsync(default);
        var term = await elected.Task.WaitAsync(Timeout);
        var gate = new RespireFakeGate();
        using var renewal = server.InjectFault("EVALSHA", RespireFakeFault.Pause(gate, afterExecution: true));
        clock.Advance(TimeSpan.FromSeconds(15));
        await renewal.Matched.WaitAsync(Timeout);
        var scheduled = clock.ScheduledCount;
        gate.Release();
        await UntilAsync(() => clock.ScheduledCount > scheduled);
        // Original deadline was 30 seconds. The renewed lease now expires at 45.
        clock.Advance(TimeSpan.FromSeconds(16));
        await Assert.That(await client.GetStringAsync("{election}:lease")).IsNotNull();
        await Assert.That(election.CurrentLeadership!.FencingToken).IsEqualTo(term.FencingToken);
        await Assert.That(term.CancellationToken.IsCancellationRequested).IsFalse();
        await election.StopAsync(default).WaitAsync(Timeout);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LostRenewalRevokesBeforeTakeoverAndStaleCleanupCannotReleaseSuccessor(bool disconnect)
    {
        var clock = new Clock();
        await using var server = new RespireFakeServer(clock);
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        await using var otherClient = await RespireClient.ConnectAsync(server.CreateOptions());
        var elected = Signal<RespireLeadership>();
        var revoked = Signal<RespireLeadership>();
        var drain = Signal<bool>();
        using var first = Election(client, clock, elected, async term =>
        {
            revoked.TrySetResult(term);
            await drain.Task;
        });
        var successor = Signal<RespireLeadership>();
        using var second = Election(otherClient, clock, successor);
        var gate = new RespireFakeGate();
        try
        {
            await first.StartAsync(default);
            var old = await elected.Task.WaitAsync(Timeout);
            using var fault = server.InjectFault("EVALSHA", disconnect
                ? RespireFakeFault.Disconnect() : RespireFakeFault.Pause(gate));
            clock.Advance(TimeSpan.FromSeconds(15));
            await fault.Matched.WaitAsync(Timeout);
            // A lost or stalled renewal cannot keep the old term valid past its original deadline.
            clock.Advance(TimeSpan.FromSeconds(15));
            await revoked.Task.WaitAsync(Timeout);
            await Assert.That(old.CancellationToken.IsCancellationRequested).IsTrue();
            await Assert.That(first.CurrentLeadership).IsNull();
            await second.StartAsync(default);
            var next = await successor.Task.WaitAsync(Timeout);
            await Assert.That(next.FencingToken).IsGreaterThan(old.FencingToken);
            gate.Release();
            drain.TrySetResult(true);
            await first.StopAsync(default).WaitAsync(Timeout);
            await Assert.That(await otherClient.GetStringAsync("{election}:lease")).IsNotNull();
            await Assert.That(second.CurrentLeadership!.FencingToken).IsEqualTo(next.FencingToken);
        }
        finally
        {
            gate.Release();
            drain.TrySetResult(true);
            await first.StopAsync(default).WaitAsync(Timeout);
            await second.StopAsync(default).WaitAsync(Timeout);
        }
    }

    [Test]
    public async Task CrashedLeaderLeaseExpiresWithinLeaseTime()
    {
        var clock = new RespireFakeClock();
        await using var server = new RespireFakeServer(clock);
        await using var deadClient = await RespireClient.ConnectAsync(server.CreateOptions());
        await using var survivor = await RespireClient.ConnectAsync(server.CreateOptions());
        // A dead process sends neither renewals nor graceful release.
        var abandoned = await new RespireCoordination(deadClient).TryAcquireFencedLockAsync(
            "{election}:lease", "{election}:fence", Lease);
        await deadClient.DisposeAsync();
        var coordination = new RespireCoordination(survivor);
        await using var blocked = await coordination.TryAcquireFencedLockAsync("{election}:lease", "{election}:fence", Lease);
        await Assert.That(blocked.Acquired).IsFalse();
        clock.Advance(Lease);
        await using var next = await coordination.TryAcquireFencedLockAsync("{election}:lease", "{election}:fence", Lease);
        await Assert.That(next.Acquired).IsTrue();
        await Assert.That(next.Lock.FencingToken).IsGreaterThan(abandoned.Lock.FencingToken);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CallbackFailureRevokesAndReleases(bool failRevoked)
    {
        var clock = new Clock();
        await using var server = new RespireFakeServer(clock);
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        var revoked = Signal<RespireLeadership>();
        using var election = new RespireLeaderElection(client, "{election}:lease", "{election}:fence", new()
        {
            OnElected = _ => failRevoked ? Task.CompletedTask : Task.FromException(new InvalidOperationException("work")),
            OnRevoked = term =>
            {
                revoked.TrySetResult(term);
                return failRevoked ? Task.FromException(new InvalidOperationException("cleanup")) : Task.CompletedTask;
            },
        }, clock);
        await election.StartAsync(default);
        var term = await revoked.Task.WaitAsync(Timeout);
        await Assert.That(async () => await election.ExecuteTask!.WaitAsync(Timeout)).Throws<InvalidOperationException>();
        await Assert.That(term.CancellationToken.IsCancellationRequested).IsTrue();
        await Assert.That(election.CurrentLeadership).IsNull();
        await Assert.That(await client.GetStringAsync("{election}:lease")).IsNull();
    }

    [Test]
    public async Task ExpiryBetweenAcquisitionAndKeepAliveRetriesWithoutElectingExpiredTerm()
    {
        var clock = new Clock();
        await using var server = new RespireFakeServer(clock);
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with
        {
            CommandHook = new ExpireBeforeKeepAlive(clock),
        });
        var elected = Signal<RespireLeadership>();
        using var election = Election(client, clock, elected);
        try
        {
            await election.StartAsync(default);
            await UntilAsync(() => clock.HasTimer(TimeSpan.FromSeconds(1)));
            await Assert.That(elected.Task.IsCompleted).IsFalse();
            await Assert.That(election.ExecuteTask!.IsCompleted).IsFalse();
            clock.Advance(TimeSpan.FromSeconds(1));
            var term = await elected.Task.WaitAsync(Timeout);
            await Assert.That(term.FencingToken).IsEqualTo(2);
        }
        finally { await election.StopAsync(default).WaitAsync(Timeout); }
    }

    private sealed class ExpireBeforeKeepAlive(Clock clock) : RespireCommandHook
    {
        private int _expired;
        public override object? OnExecuting(RespireCommandContext context)
        {
            if (context.Command == "LOCK KEEPALIVE START" && Interlocked.Exchange(ref _expired, 1) == 0)
                clock.Advance(Lease);
            return null;
        }
        public override void OnCompleted(RespireCommandContext context, object? state,
            RespireCommandOutcome outcome, Exception? error) { }
    }

    [Test]
    public async Task LostAcquisitionReplyNeverElectsAndRetriesAfterExpiry()
    {
        var clock = new Clock();
        await using var server = new RespireFakeServer(clock);
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        var elected = Signal<RespireLeadership>();
        using var election = Election(client, clock, elected);
        var gate = new RespireFakeGate();
        using var lostReply = server.InjectFault("EVAL", RespireFakeFault.Pause(gate, afterExecution: true));
        try
        {
            await election.StartAsync(default);
            await lostReply.Matched.WaitAsync(Timeout);
            clock.Advance(Lease);
            await UntilAsync(() => clock.HasTimer(TimeSpan.FromSeconds(1)));
            await Assert.That(elected.Task.IsCompleted).IsFalse();
            gate.Release();
            clock.Advance(TimeSpan.FromSeconds(1));
            var term = await elected.Task.WaitAsync(Timeout);
            await Assert.That(term.FencingToken).IsEqualTo(2);
        }
        finally { gate.Release(); await election.StopAsync(default).WaitAsync(Timeout); }
    }

    [Test]
    [NotInParallel]
    public async Task MetricsReportBothTransitionsAndListenerFailuresCannotLeakLease()
    {
        var transitions = new ConcurrentQueue<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, owner) =>
            {
                if (instrument.Meter.Name == "Respire.Coordination.LeaderElection")
                    owner.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            if (instrument.Name != "respire.leader_election.changes") return;
            foreach (var tag in tags)
                if (tag.Key == "state") transitions.Enqueue((string)tag.Value!);
            throw new InvalidOperationException("metrics observer");
        });
        listener.Start();
        var clock = new Clock();
        await using var server = new RespireFakeServer(clock);
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        var elected = Signal<RespireLeadership>();
        using var election = Election(client, clock, elected);
        await election.StartAsync(default);
        await elected.Task.WaitAsync(Timeout);
        await election.StopAsync(default).WaitAsync(Timeout);
        await Assert.That(transitions.ToArray().SequenceEqual(["elected", "revoked"])).IsTrue();
        await Assert.That(await client.GetStringAsync("{election}:lease")).IsNull();
    }

    [Test]
    [Arguments(0, 1)]
    [Arguments(30, 0)]
    [Arguments(30, 31)]
    public async Task InvalidDurationsFailBeforeStarting(int leaseSeconds, int retrySeconds)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        await Assert.That(() => new RespireLeaderElection(client, "lease", "counter", new()
        {
            LeaseDuration = TimeSpan.FromSeconds(leaseSeconds),
            RetryDelay = TimeSpan.FromSeconds(retrySeconds),
            OnElected = _ => Task.CompletedTask,
        })).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task RevocationDoesNotWaitForUncooperativeWorkAndNeverOverlapsTerms()
    {
        var clock = new Clock();
        await using var server = new RespireFakeServer(clock);
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        var elected = Signal<RespireLeadership>();
        var revoked = Signal<RespireLeadership>();
        var drain = Signal<bool>();
        var count = 0;
        using var election = new RespireLeaderElection(client, "{election}:lease", "{election}:fence", new()
        {
            OnElected = async term => { Interlocked.Increment(ref count); elected.TrySetResult(term); await drain.Task; },
            OnRevoked = term => { revoked.TrySetResult(term); return Task.CompletedTask; },
        }, clock);
        try
        {
            await election.StartAsync(default);
            await elected.Task.WaitAsync(Timeout);
            var stopping = election.StopAsync(default);
            var old = await revoked.Task.WaitAsync(Timeout);
            await Assert.That(old.CancellationToken.IsCancellationRequested).IsTrue();
            await Assert.That(election.CurrentLeadership).IsNull();
            await Assert.That(stopping.IsCompleted).IsFalse();
            clock.Advance(Lease);
            await Assert.That(count).IsEqualTo(1);
            drain.TrySetResult(true);
            await stopping.WaitAsync(Timeout);
        }
        finally { drain.TrySetResult(true); await election.StopAsync(default).WaitAsync(Timeout); }
    }

    [Test]
    public async Task LinkedCallbackCancellationAfterRevocationAllowsReelection()
    {
        var clock = new Clock();
        await using var server = new RespireFakeServer(clock);
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        var elected = Signal<RespireLeadership>();
        var reelected = Signal<RespireLeadership>();
        using var election = new RespireLeaderElection(client, "{election}:lease", "{election}:fence", new()
        {
            LeaseDuration = Lease,
            OnElected = async term =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(term.CancellationToken);
                if (!elected.TrySetResult(term)) reelected.TrySetResult(term);
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, linked.Token);
            },
        }, clock);
        var gate = new RespireFakeGate();
        try
        {
            await election.StartAsync(default);
            var first = await elected.Task.WaitAsync(Timeout);
            using var fault = server.InjectFault("EVALSHA", RespireFakeFault.Pause(gate));
            clock.Advance(TimeSpan.FromSeconds(15));
            await fault.Matched.WaitAsync(Timeout);
            clock.Advance(TimeSpan.FromSeconds(15));
            gate.Release();
            await UntilAsync(() => clock.HasTimer(TimeSpan.FromSeconds(1)) || election.ExecuteTask!.IsCompleted);
            await Assert.That(election.ExecuteTask!.IsCompleted).IsFalse();
            clock.Advance(TimeSpan.FromSeconds(1));
            var next = await reelected.Task.WaitAsync(Timeout);
            await Assert.That(next.FencingToken).IsGreaterThan(first.FencingToken);
        }
        finally
        {
            gate.Release();
            await election.StopAsync(default).WaitAsync(Timeout);
        }
    }

    [Test]
    public async Task IndependentCallbackCancellationStillFaultsElection()
    {
        var clock = new Clock();
        await using var server = new RespireFakeServer(clock);
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        using var independent = new CancellationTokenSource();
        using var election = new RespireLeaderElection(client, "{election}:lease", "{election}:fence", new()
        {
            OnElected = async term =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(term.CancellationToken, independent.Token);
                independent.Cancel();
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, linked.Token);
            },
        }, clock);
        await election.StartAsync(default);
        await Assert.That(async () => await election.ExecuteTask!.WaitAsync(Timeout)).Throws<OperationCanceledException>();
        await Assert.That(election.CurrentLeadership).IsNull();
        await Assert.That(await client.GetStringAsync("{election}:lease")).IsNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OpenCircuitAcquisitionRetriesAndCanStopDuringDelay(bool stopDuringDelay)
    {
        var clock = new Clock();
        await using var server = new RespireFakeServer(clock);
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with
        {
            CircuitBreaker = new() { MinimumFailureCount = 1, OpenDuration = TimeSpan.FromSeconds(2) },
        });
        await client.GetStringAsync("warm");
        var circuit = client.Core.Circuits!.GetForTests(client.Endpoint);
        CircuitClock(circuit) = clock;
        await Assert.That(circuit.TryAcquire(out var permit, out _)).IsTrue();
        circuit.Complete(ref permit, CircuitOutcome.Failure);
        var elected = Signal<RespireLeadership>();
        using var election = Election(client, clock, elected);
        try
        {
            await election.StartAsync(default);
            await UntilAsync(() => clock.HasTimer(TimeSpan.FromSeconds(1)) || election.ExecuteTask!.IsCompleted);
            await Assert.That(election.ExecuteTask!.IsCompleted).IsFalse();
            await Assert.That(elected.Task.IsCompleted).IsFalse();
            if (stopDuringDelay)
            {
                await election.StopAsync(default).WaitAsync(Timeout);
                await Assert.That(elected.Task.IsCompleted).IsFalse();
                return;
            }

            // Another rejection while open must also leave the contender alive.
            clock.Advance(TimeSpan.FromSeconds(1));
            await UntilAsync(() => clock.HasTimer(TimeSpan.FromSeconds(1)) || election.ExecuteTask!.IsCompleted);
            await Assert.That(election.ExecuteTask!.IsCompleted).IsFalse();
            await Assert.That(elected.Task.IsCompleted).IsFalse();
            clock.Advance(TimeSpan.FromSeconds(1));
            var term = await elected.Task.WaitAsync(Timeout);
            await Assert.That(term.FencingToken).IsEqualTo(1);
            await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
        }
        finally { await election.StopAsync(default).WaitAsync(Timeout); }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task TransientServerRejectionRetriesOrStopsDuringDelay(bool readOnly, bool stopDuringDelay)
    {
        var clock = new Clock();
        await using var server = new RespireFakeServer(clock);
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        using var fault = server.InjectFault("EVAL", readOnly ? RespireFakeFault.ReadOnly() : RespireFakeFault.Loading());
        var elected = Signal<RespireLeadership>();
        using var election = Election(client, clock, elected);
        try
        {
            await election.StartAsync(default);
            await fault.Matched.WaitAsync(Timeout);
            await UntilAsync(() => clock.HasTimer(TimeSpan.FromSeconds(1)) || election.ExecuteTask!.IsCompleted);
            await Assert.That(election.ExecuteTask!.IsCompleted).IsFalse();
            await Assert.That(elected.Task.IsCompleted).IsFalse();
            if (stopDuringDelay)
            {
                await election.StopAsync(default).WaitAsync(Timeout);
                await Assert.That(elected.Task.IsCompleted).IsFalse();
                return;
            }
            clock.Advance(TimeSpan.FromSeconds(1));
            var term = await elected.Task.WaitAsync(Timeout);
            await Assert.That(term.FencingToken).IsEqualTo(1);
        }
        finally { await election.StopAsync(default).WaitAsync(Timeout); }
    }

    [Test]
    public async Task InvalidCounterStillFaultsAcquisition()
    {
        var clock = new Clock();
        await using var server = new RespireFakeServer(clock);
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        await client.SetAsync("{election}:fence", "invalid");
        var elected = Signal<RespireLeadership>();
        using var election = Election(client, clock, elected);
        await election.StartAsync(default);
        await Assert.That(async () => await election.ExecuteTask!.WaitAsync(Timeout)).Throws<RespireServerException>();
        await Assert.That(elected.Task.IsCompleted).IsFalse();
        await Assert.That(await client.GetStringAsync("{election}:lease")).IsNull();
    }

    [Test]
    [Arguments("LOADING", true)]
    [Arguments("BUSY", true)]
    [Arguments("CLUSTERDOWN", true)]
    [Arguments("TRYAGAIN", true)]
    [Arguments("MASTERDOWN", true)]
    [Arguments("READONLY", true)]
    [Arguments("NOPERM", false)]
    [Arguments("WRONGTYPE", false)]
    [Arguments("ERR", false)]
    public async Task ServerErrorClassificationPreservesPermanentFailures(string code, bool transient)
    {
        var clock = new Clock();
        await using var server = new Respire.Tests.Networking.FakeRespServer(
            System.Text.Encoding.UTF8.GetBytes($"-{code} test rejection\r\n"));
        await using var client = await Respire.Tests.Networking.FakeRespServer.ConnectClientAsync(server.Port);
        var elected = Signal<RespireLeadership>();
        using var election = Election(client, clock, elected);
        await election.StartAsync(default);
        await UntilAsync(() => clock.HasTimer(TimeSpan.FromSeconds(1)) || election.ExecuteTask!.IsCompleted);
        if (transient)
        {
            await Assert.That(election.ExecuteTask!.IsCompleted).IsFalse();
            clock.Advance(TimeSpan.FromSeconds(1));
            await UntilAsync(() => clock.HasTimer(TimeSpan.FromSeconds(1)) || election.ExecuteTask!.IsCompleted);
            await Assert.That(election.ExecuteTask!.IsCompleted).IsFalse();
            await election.StopAsync(default).WaitAsync(Timeout);
        }
        else
        {
            await Assert.That(async () => await election.ExecuteTask!.WaitAsync(Timeout)).Throws<RespireServerException>();
        }
        await Assert.That(elected.Task.IsCompleted).IsFalse();
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_clock")]
    private static extern ref TimeProvider CircuitClock(EndpointCircuitBreaker circuit);

    private static async Task UntilAsync(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(Timeout);
        while (!predicate()) await Task.Delay(1, deadline.Token);
    }

    // One clock drives server expiry, client monotonic deadlines, renewal, and retry timers.
    private sealed class Clock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ClockTimer> _timers = [];
        private long _ticks;
        private int _scheduled;
        public int ScheduledCount => Volatile.Read(ref _scheduled);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ClockTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }
        public bool HasTimer(TimeSpan remaining)
        {
            lock (_gate) return _timers.Any(timer => timer.Due == _ticks + remaining.Ticks);
        }
        public void Advance(TimeSpan elapsed)
        {
            ClockTimer[] due;
            lock (_gate)
            {
                _ticks += elapsed.Ticks;
                due = _timers.Where(timer => timer.Due <= _ticks).ToArray();
                foreach (var timer in due) _timers.Remove(timer);
            }
            foreach (var timer in due) timer.Fire();
        }
        private sealed class ClockTimer(Clock clock, TimerCallback callback, object? state) : ITimer
        {
            public long Due { get; private set; }
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._gate)
                {
                    if (_disposed) return false;
                    clock._timers.Remove(this);
                    if (dueTime != global::System.Threading.Timeout.InfiniteTimeSpan)
                    {
                        Due = clock._ticks + dueTime.Ticks;
                        clock._timers.Add(this);
                        Interlocked.Increment(ref clock._scheduled);
                    }
                    return true;
                }
            }
            public void Fire() { if (!_disposed) callback(state); }
            public void Dispose() { lock (clock._gate) { _disposed = true; clock._timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return default; }
        }
    }
}
