using System.Runtime.CompilerServices;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ReadLatencySamplerTests
{
    [Test]
    public async Task SharedSamplingWaitDetachesEveryCandidateWithoutCancelingProbes()
    {
        var firstProbe = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondProbe = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var wait = NearestReadSelection.CreateWaitCancellation(NearestReadSelection.CreateDeadline(), default)!;
        var first = NearestReadSelection.GetLatencyAsync(new(firstProbe.Task), wait, default).AsTask();
        var second = NearestReadSelection.GetLatencyAsync(new(secondProbe.Task), wait, default).AsTask();
        await wait.CancelAsync();
        await Assert.That(await first).IsEqualTo(ReadLatencySampler.Pending);
        await Assert.That(await second).IsEqualTo(ReadLatencySampler.Pending);
        await Assert.That(firstProbe.Task.IsCompleted).IsFalse();
        await Assert.That(secondProbe.Task.IsCompleted).IsFalse();
        firstProbe.SetResult(10);
        secondProbe.SetResult(20);
        await Assert.That(await firstProbe.Task).IsEqualTo(10);
        await Assert.That(await secondProbe.Task).IsEqualTo(20);
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
        await Assert.That(await sampler.GetLatencyAsync(connection, default)).IsEqualTo(10);
        now += ReadLatencySampler<object>.IntervalMilliseconds;
        await Assert.That(await sampler.GetLatencyAsync(connection, default)).IsEqualTo(ReadLatencySampler<object>.Unknown);
        now += ReadLatencySampler<object>.IntervalMilliseconds;
        await Assert.That(await sampler.GetLatencyAsync(connection, default)).IsEqualTo(100);
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
            .IsEqualTo(ReadLatencySampler.Pending);
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
        await Assert.That(await sampler.GetLatencyAsync(connection, deadline.Token))
            .IsLessThan(ReadLatencySampler<Respire.Networking.RespireConnection>.Unknown);
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
    public async Task ProbeDeadlineExcludesOutstandingCommandsButKeepsUnsampledCandidatesEligible()
    {
        await using var sampler = new ReadLatencySampler<object>(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        });
        var pending = Enumerable.Range(0, 4).Select(_ => sampler.GetLatencyAsync(new object(), default).AsTask()).ToArray();
        var results = await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(results.All(result => result == ReadLatencySampler.Pending)).IsTrue();
        var next = sampler.GetLatencyAsync(new object(), default).AsTask();
        await Assert.That(await next).IsEqualTo(ReadLatencySampler<object>.Unknown);
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
