using System.Runtime.CompilerServices;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ReadLatencySamplerTests
{
    [Test]
    public async Task SampleStatesKeepOccupancySeparateFromMeasuredZero()
    {
        await Assert.That(() => ReadLatencyResult.Unknown.Ticks).Throws<InvalidOperationException>();
        await Assert.That(() => ReadLatencyResult.Pending.Ticks).Throws<InvalidOperationException>();
        await Assert.That(() => ReadLatencyResult.Measured(-1)).Throws<ArgumentOutOfRangeException>();
        var selection = new NearestReadSelection<string>();
        selection.Consider("unknown", ReadLatencyResult.Unknown);
        selection.Consider("pending", ReadLatencyResult.Pending);
        selection.Consider("measured", ReadLatencyResult.Measured(0));
        await Assert.That(selection.TryGet(out var selected)).IsTrue();
        await Assert.That(selected).IsEqualTo("measured");
    }

    [Test]
    public async Task CachedSampleDoesNotWaitForAnotherConnectionsPublicationGate()
    {
        var warm = new object();
        var cold = new object();
        var coldReply = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var clockReads = 0;
        await using var sampler = new ReadLatencySampler<object>((connection, token) =>
            ReferenceEquals(connection, warm) ? ValueTask.FromResult(10L) : new(coldReply.Task.WaitAsync(token)), () =>
            {
                // Warm start/publication and cold start read the clock first. The fourth
                // read is cold publication, which deliberately holds the shared gate.
                if (Interlocked.Increment(ref clockReads) == 4)
                {
                    publishing.TrySetResult();
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                }
                return 100;
            });
        await Assert.That(await sampler.GetLatencyAsync(warm, default)).IsEqualTo(ReadLatencyResult.Measured(10));
        var coldSample = sampler.GetLatencyAsync(cold, default).AsTask();
        coldReply.SetResult(20);
        Task<ReadLatencyResult>? cached = null;
        try
        {
            await publishing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cached = Task.Run(async () => await sampler.GetLatencyAsync(warm, default));
            await Assert.That(await cached.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo(ReadLatencyResult.Measured(10));
        }
        finally
        {
            release.Set();
            await coldSample;
            if (cached is not null) await cached;
        }
    }

    [Test]
    public async Task CachedSampleRechecksReservationAfterReadingClock()
    {
        using var readingClock = new ManualResetEventSlim();
        using var resumeClock = new ManualResetEventSlim();
        var pause = false;
        await using var sampler = new ReadLatencySampler<object>((_, _) => ValueTask.FromResult(10L), () =>
        {
            if (Volatile.Read(ref pause))
            {
                readingClock.Set();
                if (!resumeClock.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            }
            return 100;
        });
        var connection = new object();
        await Assert.That(await sampler.GetLatencyAsync(connection, default)).IsEqualTo(ReadLatencyResult.Measured(10));
        Volatile.Write(ref pause, true);
        var cached = Task.Run(async () => await sampler.GetLatencyAsync(connection, default));
        ReadLatencySampler<object>.ValidationReservation reservation = default;
        try
        {
            await Assert.That(readingClock.Wait(TimeSpan.FromSeconds(5))).IsTrue();
            await Assert.That(sampler.TryReserveForValidation(connection, out reservation)).IsTrue();
            resumeClock.Set();
            await Assert.That(await cached).IsEqualTo(ReadLatencyResult.Pending);
        }
        finally
        {
            resumeClock.Set();
            reservation.Dispose();
            await cached;
        }
    }

    [Test]
    public async Task ValidationReservationIsExclusiveAndDoesNotReserveOtherConnections()
    {
        await using var sampler = new ReadLatencySampler<object>((_, _) => ValueTask.FromResult(10L));
        var connection = new object();
        await Assert.That(sampler.TryReserveForValidation(connection, out var reservation)).IsTrue();
        using (reservation)
        {
            await Assert.That(sampler.TryReserveForValidation(connection, out _)).IsFalse();
            await Assert.That(await sampler.GetLatencyAsync(connection, default)).IsEqualTo(ReadLatencyResult.Pending);
            await Assert.That(await sampler.GetLatencyAsync(new object(), default)).IsEqualTo(ReadLatencyResult.Measured(10));
            await Assert.That(sampler.SamplesStarted).IsEqualTo(1);
        }
        await Assert.That(await sampler.GetLatencyAsync(connection, default)).IsEqualTo(ReadLatencyResult.Measured(10));
        await Assert.That(sampler.SamplesStarted).IsEqualTo(2);
    }

    [Test]
    public async Task OutstandingProbePreventsValidationReservation()
    {
        var reply = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var sampler = new ReadLatencySampler<object>((_, token) => new(reply.Task.WaitAsync(token)));
        var connection = new object();
        var probe = sampler.GetLatencyAsync(connection, default).AsTask();
        await Assert.That(sampler.TryReserveForValidation(connection, out _)).IsFalse();
        reply.SetResult(10);
        await probe;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (sampler.HasPendingProbe(connection)) await Task.Delay(1, deadline.Token);
        await Assert.That(sampler.TryReserveForValidation(connection, out var reservation)).IsTrue();
        reservation.Dispose();
    }

    [Test]
    public async Task DisposingOldLeaseCannotReleaseNewReservation()
    {
        await using var sampler = new ReadLatencySampler<object>((_, _) => ValueTask.FromResult(10L));
        var connection = new object();
        await Assert.That(sampler.TryReserveForValidation(connection, out var first)).IsTrue();
        first.Dispose();
        await Assert.That(sampler.TryReserveForValidation(connection, out var second)).IsTrue();
        using (second)
        {
            first.Dispose();
            await Assert.That(await sampler.GetLatencyAsync(connection, default)).IsEqualTo(ReadLatencyResult.Pending);
        }
        await Assert.That(await sampler.GetLatencyAsync(connection, default)).IsEqualTo(ReadLatencyResult.Measured(10));
    }

    [Test]
    public async Task SharedSamplingWaitDetachesEveryCandidateWithoutCancelingProbes()
    {
        var firstProbe = new TaskCompletionSource<ReadLatencyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondProbe = new TaskCompletionSource<ReadLatencyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var wait = NearestReadSelection.CreateWaitCancellation(NearestReadSelection.CreateDeadline(), default)!;
        var first = NearestReadSelection.GetLatencyAsync(new(firstProbe.Task), wait, default).AsTask();
        var second = NearestReadSelection.GetLatencyAsync(new(secondProbe.Task), wait, default).AsTask();
        await wait.CancelAsync();
        await Assert.That(await first).IsEqualTo(ReadLatencyResult.Pending);
        await Assert.That(await second).IsEqualTo(ReadLatencyResult.Pending);
        await Assert.That(firstProbe.Task.IsCompleted).IsFalse();
        await Assert.That(secondProbe.Task.IsCompleted).IsFalse();
        firstProbe.SetResult(ReadLatencyResult.Measured(10));
        secondProbe.SetResult(ReadLatencyResult.Measured(20));
        await Assert.That(await firstProbe.Task).IsEqualTo(ReadLatencyResult.Measured(10));
        await Assert.That(await secondProbe.Task).IsEqualTo(ReadLatencyResult.Measured(20));
    }

    [Test]
    public async Task ExpiredBudgetStartsNoProbeAndExcludesOnlyOutstandingOnes()
    {
        var outstanding = new object();
        var discovered = new object();
        var replies = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var sampler = new ReadLatencySampler<object>((_, _) => new ValueTask<long>(replies.Task));
        // Another selection's probe is already outstanding on this connection.
        _ = sampler.GetLatencyAsync(outstanding, default);
        await Assert.That(sampler.SamplesStarted).IsEqualTo(1);

        // Acquisition consumed the shared budget before these samples were taken; the sampler
        // rechecks the absolute deadline under its probe-publication gate.
        var deadline = Environment.TickCount64 - 1;
        await Assert.That(NearestReadSelection.CanStartProbe(deadline)).IsFalse();
        await Assert.That(NearestReadSelection.CreateWaitCancellation(deadline, default)).IsNull();
        var selection = new NearestReadSelection<object>();
        selection.QueueSample(outstanding, sampler.GetLatencyAsync(outstanding, default, probeDeadline: deadline));
        selection.QueueSample(discovered, sampler.GetLatencyAsync(discovered, default, probeDeadline: deadline));
        // No PING is queued ahead of a read on the late-discovered connection.
        await Assert.That(sampler.SamplesStarted).IsEqualTo(1);
        await Assert.That(sampler.HasPendingProbe(discovered)).IsFalse();
        while (selection.TryNextSample(out var pending))
        {
            var latency = await NearestReadSelection.GetLatencyAsync(pending.Latency, wait: null, default);
            selection.Consider(pending.Candidate, latency, pending.Linked, pending.Order);
        }
        await Assert.That(selection.TryGet(out var selected)).IsTrue();
        await Assert.That(selected).IsSameReferenceAs(discovered);
        replies.SetResult(10);
    }

    [Test]
    public async Task FailedRefreshDiscardsPreviousEstimateBeforeItsAgeLimit()
    {
        long now = 100;
        var calls = 0;
        await using var sampler = new ReadLatencySampler<object>((_, _) => ++calls switch
        {
            1 => ValueTask.FromResult(10L),
            2 => ValueTask.FromException<long>(new IOException("refresh failed")),
            _ => ValueTask.FromResult(100L),
        }, () => now);
        var connection = new object();
        await Assert.That(await sampler.GetLatencyAsync(connection, default)).IsEqualTo(ReadLatencyResult.Measured(10));
        now += ReadLatencySampler<object>.IntervalMilliseconds;
        await Assert.That(await sampler.GetLatencyAsync(connection, default)).IsEqualTo(ReadLatencyResult.Unknown);
        now += ReadLatencySampler<object>.IntervalMilliseconds;
        await Assert.That(await sampler.GetLatencyAsync(connection, default)).IsEqualTo(ReadLatencyResult.Measured(100));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TimedOutWireProbeRetainsItsSlotUntilReplyEvenWithCommandDeadlines(bool commandDeadline)
    {
        var suppress = true;
        await using var server = new FakeRespServer(2, FakeRespServer.PongReply)
        {
            SuppressReply = command => (command == "PING" || command == "GET blocked") && Volatile.Read(ref suppress),
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1,
            Endpoints = [new("127.0.0.1", server.Port)],
            CommandTimeout = commandDeadline ? TimeSpan.FromMilliseconds(250) : null,
            ConnectionIdleReadTimeout = null,
        });
        await using var sampler = ReadLatencySampler.Create();
        var connection = client.Core.Multiplexer.GetConnection();
        await Assert.That(await sampler.GetLatencyAsync(connection, default))
            .IsEqualTo(ReadLatencyResult.Pending);
        await Task.Delay(TimeSpan.FromMilliseconds(1_100));
        for (var index = 0; index < 20; index++) await sampler.GetLatencyAsync(connection, default);
        await Assert.That(sampler.SamplesStarted).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands.Count(command => command == "PING")).IsEqualTo(1);

        if (commandDeadline)
        {
            // The sampler's unarmed command must not hide a later user's armed deadline.
            await Assert.That(async () => await client.GetStringAsync("blocked").AsTask().WaitAsync(TimeSpan.FromSeconds(5)))
                .Throws<RespireTimeoutException>();
        }

        Volatile.Write(ref suppress, false);
        await server.SendRawAsync(commandDeadline ? "+PONG\r\n$5\r\nvalue\r\n"u8.ToArray() : FakeRespServer.PongReply);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (sampler.SamplesStarted == 1)
        {
            await sampler.GetLatencyAsync(connection, deadline.Token);
            await Task.Delay(1, deadline.Token);
        }
        await Assert.That((await sampler.GetLatencyAsync(connection, deadline.Token)).Kind)
            .IsEqualTo(ReadLatencyKind.Measured);
        await Assert.That(server.ReceivedCommands.Count(command => command == "PING")).IsEqualTo(2);
    }

    [Test]
    public async Task ConnectionFailuresHaveBoundedCooldownAndRecoverIndependently()
    {
        long now = 100;
        await using var sampler = new ReadLatencySampler<object>((_, _) => ValueTask.FromResult(1L), () => now);
        var first = new object();
        var second = new object();
        sampler.ConnectionFailed(first);
        await Assert.That(sampler.CanConnect(first)).IsFalse();
        await Assert.That(sampler.CanConnect(second)).IsTrue();
        now += ReadLatencySampler<object>.IntervalMilliseconds;
        await Assert.That(sampler.CanConnect(first)).IsTrue();
        sampler.ConnectionFailed(first);
        sampler.ConnectionSucceeded(first);
        await Assert.That(sampler.CanConnect(first)).IsTrue();
    }

    [Test]
    [NotInParallel]
    public async Task WarmSamplesAndSelectionAllocateNothingWithPositiveControl()
    {
        await using var sampler = new ReadLatencySampler<object>((_, _) => ValueTask.FromResult(10L), () => 100);
        var candidate = new object();
        await sampler.GetLatencyAsync(candidate, default);
        _ = MeasureWarm(sampler, candidate, false);
        _ = MeasureWarm(sampler, candidate, true);
        var allocated = AllocationMeasurement.WithoutConcurrentGc(() => MeasureWarm(sampler, candidate, false));
        var control = AllocationMeasurement.WithoutConcurrentGc(() => MeasureWarm(sampler, candidate, true));
        await Assert.That(allocated).IsEqualTo(0L);
        await Assert.That(control).IsGreaterThanOrEqualTo(37_000L);
        await Assert.That(sampler.SamplesStarted).IsEqualTo(1);
    }

    private static object? _escape;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureWarm(ReadLatencySampler<object> sampler, object candidate, bool allocate)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1_000; index++)
        {
            if (!sampler.CanConnect(candidate)) throw new InvalidOperationException();
            sampler.ConnectionSucceeded(candidate);
            var selection = new NearestReadSelection<object>();
            selection.QueueSample(candidate, NearestReadSelection.GetLatencyAsync(sampler.GetLatencyAsync(candidate, default),
                wait: null, default));
            if (selection.TryNextSample(out _)) throw new InvalidOperationException("Warm sample unexpectedly queued");
            if (!selection.TryGet(out var result) || !ReferenceEquals(candidate, result)) throw new InvalidOperationException();
            if (allocate) Volatile.Write(ref _escape, new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Test]
    public async Task SamplesAreCoalescedAndCallerCancellationIsIndependent()
    {
        var reply = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var sampler = new ReadLatencySampler<object>((_, token) => new(reply.Task.WaitAsync(token)));
        var connection = new object();
        using var cancellation = new CancellationTokenSource();
        var canceled = sampler.GetLatencyAsync(connection, cancellation.Token).AsTask();
        var survivor = sampler.GetLatencyAsync(connection, default).AsTask();
        cancellation.Cancel();
        await Assert.That(async () => await canceled).Throws<OperationCanceledException>();
        await Assert.That(sampler.SamplesStarted).IsEqualTo(1);
        reply.SetResult(100);
        await Assert.That(await survivor).IsEqualTo(ReadLatencyResult.Measured(100));
    }

    [Test]
    public async Task SamplingConcurrencyIsBoundedWithoutAWaitingQueue()
    {
        var reply = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var sampler = new ReadLatencySampler<object>((_, token) => new(reply.Task.WaitAsync(token)));
        var pending = Enumerable.Range(0, 4).Select(_ => sampler.GetLatencyAsync(new object(), default).AsTask()).ToArray();
        await Assert.That(await sampler.GetLatencyAsync(new object(), default)).IsEqualTo(ReadLatencyResult.Unknown);
        await Assert.That(sampler.SamplesStarted).IsEqualTo(4);
        reply.SetResult(10);
        await Task.WhenAll(pending);
    }

    [Test]
    public async Task FreshSamplesWaitForOutstandingProbesBeforeTheyCanBeReused()
    {
        long now = 100;
        var reply = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var sampler = new ReadLatencySampler<object>((_, token) =>
            ++calls == 1 ? ValueTask.FromResult(100L) : new(reply.Task.WaitAsync(token)), () => now);
        var connection = new object();
        await Assert.That(await sampler.GetLatencyAsync(connection, default)).IsEqualTo(ReadLatencyResult.Measured(100));
        await Assert.That(await sampler.GetLatencyAsync(connection, default)).IsEqualTo(ReadLatencyResult.Measured(100));
        now += ReadLatencySampler<object>.IntervalMilliseconds;
        var fresh = sampler.GetLatencyAsync(connection, default).AsTask();
        await Assert.That(fresh.IsCompleted).IsFalse();
        await Assert.That(await NearestReadSelection.GetLatencyAsync(new(fresh), null, default))
            .IsEqualTo(ReadLatencyResult.Pending);
        await Assert.That(calls).IsEqualTo(2);
        now += ReadLatencySampler<object>.MaximumAgeMilliseconds;
        var expired = sampler.GetLatencyAsync(connection, default).AsTask();
        await Assert.That(expired.IsCompleted).IsFalse();
        reply.SetResult(500);
        await Assert.That(await fresh).IsEqualTo(ReadLatencyResult.Measured(200));
        await Assert.That(await expired).IsEqualTo(ReadLatencyResult.Measured(200));
    }

    [Test]
    public async Task FailedSampleIsUnknownAndRecoversAtNextInterval()
    {
        long now = 100;
        var calls = 0;
        await using var sampler = new ReadLatencySampler<object>((_, _) => ++calls == 1
            ? ValueTask.FromException<long>(new IOException("unavailable")) : ValueTask.FromResult(50L), () => now);
        var connection = new object();
        await Assert.That(await sampler.GetLatencyAsync(connection, default)).IsEqualTo(ReadLatencyResult.Unknown);
        await Assert.That(await sampler.GetLatencyAsync(connection, default)).IsEqualTo(ReadLatencyResult.Unknown);
        await Assert.That(calls).IsEqualTo(1);
        now += ReadLatencySampler<object>.IntervalMilliseconds;
        await Assert.That(await sampler.GetLatencyAsync(connection, default)).IsEqualTo(ReadLatencyResult.Measured(50));
    }

    [Test]
    public async Task ReplacedPhysicalConnectionStartsWithoutOldLatency()
    {
        var calls = 0;
        await using var sampler = new ReadLatencySampler<object>((_, _) => ValueTask.FromResult((long)++calls));
        await Assert.That(await sampler.GetLatencyAsync(new object(), default)).IsEqualTo(ReadLatencyResult.Measured(1));
        await Assert.That(await sampler.GetLatencyAsync(new object(), default)).IsEqualTo(ReadLatencyResult.Measured(2));
    }

    [Test]
    public async Task DisposalCancelsActiveSamples()
    {
        await using var sampler = new ReadLatencySampler<object>(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        });
        var pending = sampler.GetLatencyAsync(new object(), default).AsTask();
        await sampler.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(await pending).IsEqualTo(ReadLatencyResult.Unknown);
    }

    [Test]
    public async Task ProbeDeadlineExcludesOutstandingCommandsButKeepsUnsampledCandidatesEligible()
    {
        await using var sampler = new ReadLatencySampler<object>(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        });
        var pending = Enumerable.Range(0, 4).Select(_ => sampler.GetLatencyAsync(new object(), default).AsTask()).ToArray();
        var results = await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(results.All(result => result == ReadLatencyResult.Pending)).IsTrue();
        var next = sampler.GetLatencyAsync(new object(), default).AsTask();
        await Assert.That(await next).IsEqualTo(ReadLatencyResult.Unknown);
        await Assert.That(sampler.SamplesStarted).IsEqualTo(4);
        var selection = new NearestReadSelection<string>();
        foreach (var latency in results) selection.Consider("blocked", latency);
        await Assert.That(selection.TryGet(out _)).IsFalse();
        selection.Consider("unsampled", await next);
        await Assert.That(selection.TryGet(out var selected)).IsTrue();
        await Assert.That(selected).IsEqualTo("unsampled");
        await sampler.DisposeAsync();
    }
}
