using System.Runtime.CompilerServices;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ReadLatencySamplerTests
{
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
            selection.Consider(candidate, sampler.GetLatencyAsync(candidate, default).GetAwaiter().GetResult());
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
        await Assert.That(await survivor).IsEqualTo(100);
    }

    [Test]
    public async Task SamplingConcurrencyIsBoundedWithoutAWaitingQueue()
    {
        var reply = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var sampler = new ReadLatencySampler<object>((_, token) => new(reply.Task.WaitAsync(token)));
        var pending = Enumerable.Range(0, 4).Select(_ => sampler.GetLatencyAsync(new object(), default).AsTask()).ToArray();
        await Assert.That(await sampler.GetLatencyAsync(new object(), default)).IsEqualTo(ReadLatencySampler<object>.Unknown);
        await Assert.That(sampler.SamplesStarted).IsEqualTo(4);
        reply.SetResult(10);
        await Task.WhenAll(pending);
    }

    [Test]
    public async Task FreshSamplesAreReusedAndExpiredSamplesWaitForNewEvidence()
    {
        long now = 100;
        var reply = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var sampler = new ReadLatencySampler<object>((_, token) =>
            ++calls == 1 ? ValueTask.FromResult(100L) : new(reply.Task.WaitAsync(token)), () => now);
        var connection = new object();
        await Assert.That(await sampler.GetLatencyAsync(connection, default)).IsEqualTo(100);
        now += ReadLatencySampler<object>.IntervalMilliseconds;
        await Assert.That(await sampler.GetLatencyAsync(connection, default)).IsEqualTo(100);
        await Assert.That(calls).IsEqualTo(2);
        now += ReadLatencySampler<object>.MaximumAgeMilliseconds;
        var expired = sampler.GetLatencyAsync(connection, default).AsTask();
        await Assert.That(expired.IsCompleted).IsFalse();
        reply.SetResult(500);
        await Assert.That(await expired).IsEqualTo(200);
    }

    [Test]
    public async Task FailedSampleIsUnknownAndRecoversAtNextInterval()
    {
        long now = 100;
        var calls = 0;
        await using var sampler = new ReadLatencySampler<object>((_, _) => ++calls == 1
            ? ValueTask.FromException<long>(new IOException("unavailable")) : ValueTask.FromResult(50L), () => now);
        var connection = new object();
        await Assert.That(await sampler.GetLatencyAsync(connection, default)).IsEqualTo(ReadLatencySampler<object>.Unknown);
        await Assert.That(await sampler.GetLatencyAsync(connection, default)).IsEqualTo(ReadLatencySampler<object>.Unknown);
        await Assert.That(calls).IsEqualTo(1);
        now += ReadLatencySampler<object>.IntervalMilliseconds;
        await Assert.That(await sampler.GetLatencyAsync(connection, default)).IsEqualTo(50);
    }

    [Test]
    public async Task ReplacedPhysicalConnectionStartsWithoutOldLatency()
    {
        var calls = 0;
        await using var sampler = new ReadLatencySampler<object>((_, _) => ValueTask.FromResult((long)++calls));
        await Assert.That(await sampler.GetLatencyAsync(new object(), default)).IsEqualTo(1);
        await Assert.That(await sampler.GetLatencyAsync(new object(), default)).IsEqualTo(2);
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
        await Assert.That(await pending).IsEqualTo(ReadLatencySampler<object>.Unknown);
    }

    [Test]
    public async Task ProbeDeadlineReturnsUnknownAndReleasesCapacity()
    {
        await using var sampler = new ReadLatencySampler<object>(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        });
        var pending = Enumerable.Range(0, 4).Select(_ => sampler.GetLatencyAsync(new object(), default).AsTask()).ToArray();
        var results = await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(results.All(result => result == ReadLatencySampler<object>.Unknown)).IsTrue();
        var next = sampler.GetLatencyAsync(new object(), default).AsTask();
        await Assert.That(sampler.SamplesStarted).IsEqualTo(5);
        await sampler.DisposeAsync();
        await Assert.That(await next).IsEqualTo(ReadLatencySampler<object>.Unknown);
    }
}
