using System.Runtime.CompilerServices;
using Respire.Internal;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class DedicatedLeaseAcquisitionTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    [Test]
    [Arguments(true, true)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task ReportedConnectTimeoutCanRacePoolRetirement(bool retirePool, bool connectTimeout)
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var replacement = new DedicatedConnectionPool(
            "127.0.0.1", server.Port, RespireConnectionOptions.Default, null);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The timeout has already been selected before retirement is published. Delay its
        // delivery to the route, rather than relying on a timer/retirement scheduling race.
        var failure = new RespireTimeoutException(connectTimeout ? "CONNECT" : "SELECT", Limit,
            new OperationCanceledException(),
            RespireTimeoutDiagnostics.Capture(connectTimeout ? RespireCommandStage.Connecting : RespireCommandStage.Unknown));
        await using var original = new DedicatedConnectionPool("127.0.0.1", server.Port,
            new RespireConnectionOptions
            {
                TestingStreamFactory = async (_, _, _) =>
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(Limit);
                    throw failure;
                },
            }, null);
        var state = new RouteState(replacement);
        var pending = RentAsync(original, state).AsTask();
        Task? retirement = null;
        try
        {
            await entered.Task.WaitAsync(Limit);
            if (retirePool) retirement = original.RetireAsync().AsTask();
            release.TrySetResult();
            if (retirePool && connectTimeout)
            {
                var lease = await pending.WaitAsync(Limit);
                await Assert.That(lease.Pool).IsSameReferenceAs(replacement);
                await Assert.That(state.Selections).IsEqualTo(1);
                await Assert.That(state.Retirements).IsEqualTo(1);
                await Assert.That(state.TerminalError).IsNull();
            }
            else
            {
                var error = await Assert.That(async () => await pending.WaitAsync(Limit))
                    .ThrowsExactly<RespireTimeoutException>();
                await Assert.That(error).IsSameReferenceAs(failure);
                await Assert.That(state.Selections).IsEqualTo(0);
                await Assert.That(state.Retirements).IsEqualTo(0);
            }
            await Assert.That(state.Completions).IsEqualTo(1);
            if (retirement is not null) await retirement.WaitAsync(Limit);
        }
        finally
        {
            release.TrySetResult();
            try
            {
                var lease = await pending.WaitAsync(Limit);
                lease.Pool.Return(lease.Connection);
            }
            catch (Exception) when (pending.IsCompleted) { }
            if (retirement is not null) await retirement.WaitAsync(Limit);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OpenCircuitOnStoppedOrStalePoolDoesNotRejectHealthyReplacement(bool stalePublication)
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], UseCluster = true,
            CircuitBreaker = new() { MinimumFailureCount = 1 }, ThreadPoolMonitoring = false,
        });
        await using var replacement = new DedicatedConnectionPool(
            "127.0.0.1", server.Port, RespireConnectionOptions.Default, null);
        var endpoint = new RespireEndpoint("127.0.0.1", 9000);
        await using var original = new DedicatedConnectionPool(endpoint.Host, endpoint.Port,
            RespireConnectionOptions.Default, null)
        {
            MovingOwner = stalePublication ? client.Core.Cluster!.GetMultiplexer(endpoint) : null,
            // A different publication is stale even before the old pool starts stopping.
            MovingPublication = new object(),
        };
        if (!stalePublication) await original.RetireAsync();
        var circuits = client.Core.Circuits!;
        var admission = circuits.Acquire(endpoint, default);
        try { admission.Failed(new RespireConnectionException("Injected old endpoint failure."), default); }
        finally { admission.Dispose(); }
        var state = new RouteState(replacement);
        var lease = await DedicatedLeaseAcquisition.RentAsync(original, new TestRoute(state), default,
            reuseIdle: true, DedicatedLeaseKind.Ordinary, circuits: circuits);
        try
        {
            await Assert.That(lease.Pool).IsSameReferenceAs(replacement);
            await Assert.That(state.Selections).IsEqualTo(1);
            await Assert.That(state.Retirements).IsEqualTo(1);
            await Assert.That(state.TerminalError).IsNull();
            await Assert.That(state.Completions).IsEqualTo(1);
        }
        finally { lease.Pool.Return(lease.Connection); }
    }

    [Test]
    public async Task SelectingSameStoppedPoolPreservesFailureAndStops()
    {
        await using var pool = new DedicatedConnectionPool("127.0.0.1", 6379, RespireConnectionOptions.Default, null);
        await pool.RetireAsync();
        var state = new RouteState(pool);
        var error = await Assert.That(async () => await RentAsync(pool, state)).ThrowsExactly<ObjectDisposedException>();
        await Assert.That(state.Selections).IsEqualTo(1);
        await Assert.That(state.Retirements).IsEqualTo(1);
        await Assert.That(state.TerminalError).IsSameReferenceAs(error);
        await Assert.That(state.Completions).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReplacementLeaseKeepsItsPoolAndIdlePolicy(bool reuseIdle)
    {
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply);
        await using var old = new DedicatedConnectionPool("127.0.0.1", server.Port, RespireConnectionOptions.Default, null);
        await using var replacement = new DedicatedConnectionPool("127.0.0.1", server.Port, RespireConnectionOptions.Default, null);
        var idle = await replacement.RentAsync(CancellationToken.None);
        replacement.Return(idle);
        await old.RetireAsync();
        var state = new RouteState(replacement);
        var lease = await RentAsync(old, state, reuseIdle: reuseIdle);
        await Assert.That(lease.Pool).IsSameReferenceAs(replacement);
        await Assert.That(ReferenceEquals(lease.Connection, idle)).IsEqualTo(reuseIdle);
        await Assert.That(state.Completions).IsEqualTo(1);
        lease.Pool.Return(lease.Connection);
    }

    [Test]
    public async Task CancellationDuringSelectionKeepsCallerToken()
    {
        await using var pool = new DedicatedConnectionPool("127.0.0.1", 6379, RespireConnectionOptions.Default, null);
        await pool.RetireAsync();
        var state = new RouteState(pool) { SelectionGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var cancellation = new CancellationTokenSource();
        var acquisition = RentAsync(pool, state, cancellation.Token).AsTask();
        await state.SelectionStarted.Task.WaitAsync(Limit);
        cancellation.Cancel();
        var error = await Assert.That(async () => await acquisition).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(state.Selections).IsEqualTo(1);
        await Assert.That(state.Completions).IsEqualTo(1);
    }

    [Test]
    public async Task CancelledCallerCannotTakeIdleLease()
    {
        await using var server = new FakeRespServer();
        await using var pool = new DedicatedConnectionPool("127.0.0.1", server.Port, RespireConnectionOptions.Default, null);
        var connection = await pool.RentAsync(CancellationToken.None);
        pool.Return(connection);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var state = new RouteState(pool);
        await Assert.That(async () => await RentAsync(pool, state, cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(pool.CaptureRetirementState().Borrowed).IsEqualTo(0);
        await Assert.That(state.Selections).IsEqualTo(0);
        await Assert.That(state.Completions).IsEqualTo(1);
    }

    [Test]
    public async Task DisposedOwnerCannotRent()
    {
        await using var pool = new DedicatedConnectionPool("127.0.0.1", 6379, RespireConnectionOptions.Default, null);
        var state = new RouteState(pool) { IsDisposed = true };
        await Assert.That(async () => await RentAsync(pool, state)).ThrowsExactly<ObjectDisposedException>();
        await Assert.That(pool.CaptureRetirementState().Connecting).IsEqualTo(0);
        await Assert.That(state.Selections).IsEqualTo(0);
        await Assert.That(state.Completions).IsEqualTo(1);
    }

    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WarmRentalHasNoStrategyAllocation(bool cluster)
    {
        await using var server = new FakeRespServer();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Endpoints = [new("127.0.0.1", server.Port)], Connections = 1, UseCluster = cluster,
        });
        var pool = await client.Core.GetDedicatedPoolAsync(CancellationToken.None);
        var connection = await pool.RentAsync(CancellationToken.None);
        pool.Return(connection);
        Measure(client.Core, pool, false);
        Measure(client.Core, pool, true);
        var measured = AllocationMeasurement.WithoutConcurrentGc(() =>
            (Actual: Measure(client.Core, pool, false), Control: Measure(client.Core, pool, true)));
        await Assert.That(measured.Actual).IsEqualTo(0);
        await Assert.That(measured.Control).IsGreaterThanOrEqualTo(37_000);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure(ClientCore core, DedicatedConnectionPool pool, bool control)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1_000; index++)
        {
            var pending = core.Cluster is { } cluster
                ? cluster.RentDedicatedConnectionAsync(pool, slot: null, CancellationToken.None, discovery: null)
                : core.RentDedicatedConnectionAsync(pool, CancellationToken.None);
            if (!pending.IsCompletedSuccessfully) throw new InvalidOperationException("Warm idle rental must complete synchronously.");
            var lease = pending.Result;
            lease.Pool.Return(lease.Connection);
            if (control) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static ValueTask<(DedicatedConnectionPool Pool, RespireConnection Connection)> RentAsync(
        DedicatedConnectionPool pool, RouteState state, CancellationToken cancellationToken = default, bool reuseIdle = true)
        => DedicatedLeaseAcquisition.RentAsync(pool, new TestRoute(state), cancellationToken, reuseIdle, DedicatedLeaseKind.Ordinary);

    private sealed class RouteState(DedicatedConnectionPool replacement)
    {
        internal readonly DedicatedConnectionPool Replacement = replacement;
        internal readonly TaskCompletionSource SelectionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<DedicatedConnectionPool>? SelectionGate;
        internal int Selections;
        internal int Retirements;
        internal int Completions;
        internal bool IsDisposed;
        internal Exception? TerminalError;
    }

    private readonly struct TestRoute(RouteState state) : IDedicatedLeaseRoute
    {
        public void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(state.IsDisposed, state);
        public bool CanRetry(int attempt, CancellationToken cancellationToken) => !state.IsDisposed;
        public void RecordRetirement(Exception error, int attempt) => state.Retirements++;
        public ValueTask<DedicatedConnectionPool> SelectReplacementAsync(CancellationToken cancellationToken)
        {
            state.Selections++;
            state.SelectionStarted.TrySetResult();
            return state.SelectionGate is { } gate
                ? new(gate.Task.WaitAsync(cancellationToken)) : ValueTask.FromResult(state.Replacement);
        }
        public void SetTerminalError(Exception error) => state.TerminalError = error;
        public void Dispose() => state.Completions++;
    }
}
