using System.Runtime.CompilerServices;
using Respire.Internal;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public partial class ErrorMetricsTests
{
    [Test]
    [MatrixDataSource]
    public async Task DeferredObservationIgnoresExternalPendingMonitor([Matrix(false, true)] bool enabled)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = enabled ? RespireMetricGroups.Resiliency : RespireMetricGroups.None });
        using var capture = new Capture();
        var pending = new RespirePending<int>();
        var observation = pending.Observation;
        var error = new RespireServerException("WRONGTYPE private-key");
        using var completed = new ManualResetEventSlim();
        Task worker;
        bool completedWhileLocked;
        lock (pending)
        {
            worker = Task.Run(() =>
            {
                try
                {
                    observation.SetAttempts(2);
                    pending.Fail(error);
                    if (!observation.TryHandled(error)) throw new InvalidOperationException();
                    pending.AddErrorAttempts(1);
                    _ = observation.Attempts;
                    _ = pending.ReportError();
                }
                finally { completed.Set(); }
            });
            completedWhileLocked = completed.Wait(TimeSpan.FromSeconds(5));
        }
        await worker;
        await Assert.That(completedWhileLocked).IsTrue();
        await Assert.That(observation.Attempts).IsEqualTo(4);
        await Assert.That(pending.Error).IsSameReferenceAs(error);
        _ = pending.ReportError();
        await Assert.That(capture.Items.Count).IsEqualTo(enabled ? 2 : 0);
        if (enabled)
        {
            var final = capture.Items.Single(item => !(bool)item.Tags["redis.client.errors.internal"]!);
            await Assert.That(final.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(4);
        }
    }

    [Test]
    [MatrixDataSource]
    public async Task DeferredObservationRejectsHandledErrorsAfterPublication(
        [Matrix(false, true)] bool enabled, [Matrix(false, true)] bool faulted)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = enabled ? RespireMetricGroups.Resiliency : RespireMetricGroups.None });
        using var capture = new Capture();
        var pending = new RespirePending<int>();
        var observation = pending.Observation;
        var error = new RespireServerException("WRONGTYPE private-key");
        await Assert.That(observation.IsOpen).IsTrue();
        await Assert.That(observation.TryHandled(error)).IsTrue();
        await Assert.That(observation.Attempts).IsEqualTo(1);
        if (faulted) pending.Fail(error);
        else pending.Succeed(42);
        await Assert.That(pending.ReportError()).IsEqualTo(faulted);

        await Assert.That(observation.IsOpen).IsFalse();
        await Assert.That(observation.TryHandled(error)).IsFalse();
        observation.SetAttempts(50);
        pending.AddErrorAttempts(50);
        _ = pending.ReportError();
        await Assert.That(observation.Attempts).IsEqualTo(1);
        var expectedErrors = enabled ? 1 + (faulted ? 1 : 0) : 0;
        await Assert.That(capture.Items.Count).IsEqualTo(expectedErrors);
        if (faulted) await Assert.That(pending.Error).IsSameReferenceAs(error);
        else await Assert.That(pending.Result).IsEqualTo(42);
    }

    [Test]
    [MatrixDataSource]
    public async Task DeferredInspectionCountsEachLifecycleFailureOnce(
        [Matrix(false, true)] bool enabled, [Matrix(false, true)] bool awaiter)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = enabled ? RespireMetricGroups.Resiliency : RespireMetricGroups.None });
        using var capture = new Capture(throwOnMeasurement: true);
        var pending = new RespirePending<long>();
        long Read() => awaiter ? pending.GetAwaiter().GetResult() : pending.Result;
        await Assert.That(Read).Throws<RespirePendingNotReadyException>();
        await Assert.That(Read).Throws<RespirePendingNotReadyException>();
        pending.Succeed(42);
        await Assert.That(Read()).IsEqualTo(42L);
        pending.Abort();
        await Assert.That(Read).Throws<RespireTransactionAbortedException>();
        await Assert.That(Read).Throws<RespireTransactionAbortedException>();
        await Assert.That(capture.Items.Count).IsEqualTo(enabled ? 2 : 0);
    }

    [Test]
    [MatrixDataSource]
    public async Task DiscardedDeferredInspectionCountsOnceAfterCleanup([Matrix(false, true)] bool transaction)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture(throwOnMeasurement: true);
        RespirePending<bool> pending;
        if (transaction)
        {
            var queue = client.CreateTransaction();
            pending = queue.Set("key", "value");
            await queue.DisposeAsync();
        }
        else
        {
            var queue = client.CreateBatch();
            pending = queue.Set("key", "value");
            queue.Dispose();
        }
        await Assert.That(capture.Items.Count).IsEqualTo(0);
        await Assert.That(() => pending.Result).Throws<RespireException>();
        await Assert.That(() => pending.GetAwaiter().GetResult()).Throws<RespireException>();
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That(capture.Items.Single().Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
    }

    [Test]
    [MatrixDataSource]
    public async Task DurabilityArgumentsOwnFailureBeforeCommandConstruction(
        [Matrix(false, true)] bool aof, [Matrix(false, true)] bool negativeTimeout)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var batch = client.CreateBatch();
        _ = batch.Set("key", "value");
        using var capture = new Capture(throwOnMeasurement: true);
        await Assert.That(async () =>
        {
            var replicas = negativeTimeout ? 1 : -1;
            var timeout = negativeTimeout ? TimeSpan.FromTicks(-1) : TimeSpan.Zero;
            if (aof) await batch.ExecuteAndWaitForAofAsync(true, replicas, timeout);
            else await batch.ExecuteAndWaitForReplicationAsync(replicas, timeout);
        }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That(batch.IsSent).IsFalse();
    }

    [Test]
    public async Task EmptyCommitCancellationKeepsOriginalTokenAndReportsOnce()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var transaction = client.CreateTransaction();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var capture = new Capture(throwOnMeasurement: true);
        var error = await Assert.That(async () => await transaction.CommitAsync(cancellation.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(capture.Items.Count).IsEqualTo(1);
    }

    [Test]
    [MatrixDataSource]
    public async Task HashImportConstructionHasOneFinalOwner([Matrix("prepare", "set", "discard")] string operation)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(8, FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var session = await client.Hashes.CreateImportSessionAsync();
        using var capture = new Capture(throwOnMeasurement: true);
        await Assert.That(async () =>
        {
            switch (operation)
            {
                case "prepare": await session.PrepareAsync(default, ["field"]); break;
                case "set": await session.SetAsync("key", default, ["value"]); break;
                default: await session.DiscardAsync(default); break;
            }
        }).Throws<ArgumentException>();
        await Assert.That(capture.Items.Count).IsEqualTo(1);
    }

    [Test]
    [MatrixDataSource]
    public async Task DurabilityFinalErrorFollowsCompletedCacheFence(
        [Matrix(false, true)] bool aof, [Matrix(false, true)] bool commandFailure)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "HELLO 3" ? "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray()
                : command.StartsWith("WAIT ", StringComparison.Ordinal) ? ":-1\r\n"u8.ToArray()
                : command.StartsWith("WAITAOF ", StringComparison.Ordinal) ? "*2\r\n:-1\r\n:1\r\n"u8.ToArray()
                : commandFailure && command.StartsWith("SET ", StringComparison.Ordinal) ? "-NOPERM denied\r\n"u8.ToArray() : null,
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp3, Connections = 1, ClientSideCache = new(),
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        var mutationsAtFinal = -1;
        using var capture = new Capture(throwOnMeasurement: true, onMeasurement: item =>
        {
            if (!(bool)item.Tags["redis.client.errors.internal"]!)
                mutationsAtFinal = client.Core.ClientCache!.InspectForTests().ActiveMutationCount;
        });
        using var batch = client.CreateBatch();
        _ = batch.Set("key", "value");
        await Assert.That(async () =>
        {
            if (aof) await batch.ExecuteAndWaitForAofAsync(true, 1, TimeSpan.Zero);
            else await batch.ExecuteAndWaitForReplicationAsync(1, TimeSpan.Zero);
        }).Throws<RespireException>();
        await Assert.That(mutationsAtFinal).IsEqualTo(0);
        await Assert.That(capture.Items.Count).IsEqualTo(1);
    }

    [Test]
    [MatrixDataSource]
    public async Task SuccessfulDeferredExecutionRentsNoErrorObservation(
        [Matrix(false, true)] bool transaction, [Matrix(false, true)] bool enabled,
        [Matrix(2, 3)] int protocol)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = enabled ? RespireMetricGroups.Resiliency : RespireMetricGroups.None });
        using var capture = new Capture();
        await using var server = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => "%1\r\n+proto\r\n:3\r\n"u8.ToArray(),
                "INCR counter" or "GET key" when transaction => "+QUEUED\r\n"u8.ToArray(),
                "INCR counter" => ":42\r\n"u8.ToArray(),
                "GET key" => "$5\r\nvalue\r\n"u8.ToArray(),
                "EXEC" => "*2\r\n:42\r\n$5\r\nvalue\r\n"u8.ToArray(),
                _ => null,
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = (RespProtocol)protocol, Connections = 1,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        await ExecuteAsync();
        long legacyRentals = 0, failureRentals = 0;
        RespireTelemetry.ErrorObservation.RentalObserverForTests = () => Interlocked.Increment(ref legacyRentals);
        ErrorObservation.RentalObserverForTests = () => Interlocked.Increment(ref failureRentals);
        try
        {
            await ExecuteAsync();
            await Assert.That(Interlocked.Read(ref legacyRentals)).IsEqualTo(0L);
            await Assert.That(Interlocked.Read(ref failureRentals)).IsEqualTo(0L);
            await Assert.That(capture.Items).IsEmpty();

            // These controls detect rentals even when both pools return warmed storage.
            using (RespireTelemetry.ErrorObservation.Rent(force: true)) { }
            await Assert.That(Interlocked.Read(ref legacyRentals)).IsEqualTo(1L);
            var owner = ErrorObservation.StartFailure();
            owner.Complete();
            await Assert.That(Interlocked.Read(ref failureRentals)).IsEqualTo(1L);
        }
        finally
        {
            RespireTelemetry.ErrorObservation.RentalObserverForTests = null;
            ErrorObservation.RentalObserverForTests = null;
        }

        async Task ExecuteAsync()
        {
            using var batch = transaction ? null : client.CreateBatch();
            await using var multi = transaction ? client.CreateTransaction() : null;
            var increment = transaction ? multi!.Increment("counter") : batch!.Increment("counter");
            var get = transaction ? multi!.GetString("key") : batch!.GetString("key");
            if (transaction) await multi!.CommitAsync();
            else await batch!.ExecuteAsync();
            await Assert.That(increment.Result).IsEqualTo(42L);
            await Assert.That(get.GetAwaiter().GetResult()).IsEqualTo("value");
        }
    }

    [Test]
    public async Task DeferredSuccessAddsNoObservationAllocation()
    {
        var pending = new RespirePending<int>();
        pending.Succeed(42);
        _ = MeasureDeferredSuccess(pending, false);
        _ = MeasureDeferredSuccess(pending, true);
        var allocated = AllocationMeasurement.WithoutConcurrentGc(() => MeasureDeferredSuccess(pending, false));
        var control = AllocationMeasurement.WithoutConcurrentGc(() => MeasureDeferredSuccess(pending, true));
        await Assert.That(allocated).IsEqualTo(0L);
        await Assert.That(control).IsGreaterThanOrEqualTo(37_000L);
    }

    private static object? _deferredAllocationAnchor;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureDeferredSuccess(RespirePending<int> pending, bool allocate)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1_000; index++)
        {
            if (pending.Result != 42 || pending.ReportError()) throw new InvalidOperationException();
            if (allocate) Volatile.Write(ref _deferredAllocationAnchor, new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
