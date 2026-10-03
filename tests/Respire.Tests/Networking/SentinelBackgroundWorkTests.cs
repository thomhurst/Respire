using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SentinelBackgroundWorkTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    [Test]
    public async Task ConcurrentStopIncludesEveryAdmittedTaskAndRejectsLateWork()
    {
        var owner = new SentinelBackgroundWork(new object());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registrations = new System.Collections.Concurrent.ConcurrentBag<Task>();
        var workers = Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            if (owner.TryStart(SentinelWorkKind.Monitor, () => release.Task) is { } task) registrations.Add(task);
        })).ToArray();
        // Ensure at least one registration precedes Stop, regardless of worker scheduling.
        var first = owner.TryStart(SentinelWorkKind.Supervisor, () => release.Task)!;
        var snapshot = owner.Stop();
        await Task.WhenAll(workers).WaitAsync(Limit);
        await Assert.That(snapshot.Contains(first)).IsTrue();
        foreach (var admitted in registrations)
            await Assert.That(snapshot.Contains(admitted)).IsTrue();
        var called = false;
        await Assert.That(owner.TryStart(SentinelWorkKind.Rediscovery, () => { called = true; return Task.CompletedTask; })).IsNull();
        await Assert.That(called).IsFalse();
        release.TrySetResult();
        await Task.WhenAll(snapshot).WaitAsync(Limit);
    }

    [Test]
    public async Task CompletedFailuresRemainOwnedForAggregateCleanup()
    {
        var owner = new SentinelBackgroundWork(new object());
        var first = new IOException("first");
        var second = new InvalidOperationException("second");
        var tasks = new[]
        {
            owner.TryStart(SentinelWorkKind.SourceResolution, () => Task.FromException(first))!,
            owner.TryStart(SentinelWorkKind.Monitor, () => Task.FromException(second))!,
        };
        try { await Task.WhenAll(tasks).WaitAsync(Limit); } catch (Exception) { }
        var error = await Assert.That(() => CleanupTasks.WhenAllAsync(owner.Stop())).ThrowsExactly<AggregateException>();
        await Assert.That(error!.InnerExceptions).Contains(first);
        await Assert.That(error.InnerExceptions).Contains(second);
    }

    [Test]
    public async Task RepeatedMonitorFailuresRetainBoundedShutdownEvidence()
    {
        var owner = new SentinelBackgroundWork(new object());
        var tasks = Enumerable.Range(0, 128).Select(index => owner.TryStart(SentinelWorkKind.Monitor,
            () => Task.FromException(new IOException($"monitor episode {index}")))!).ToArray();
        try { await Task.WhenAll(tasks).WaitAsync(Limit); } catch (Exception) { }
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        Task[] report;
        do
        {
            report = owner.Stop();
            // Observe each snapshot, including any synthetic omitted-history diagnostic.
            try { await CleanupTasks.WhenAllAsync(report); } catch (Exception) { }
            if (report.Length <= SentinelBackgroundWork.MaximumRetainedFailuresPerKind + 1) break;
            await Task.Delay(1);
        } while (System.Diagnostics.Stopwatch.GetElapsedTime(started) < Limit);
        await Assert.That(report.Length).IsLessThanOrEqualTo(SentinelBackgroundWork.MaximumRetainedFailuresPerKind + 1);
        var error = await Assert.That(() => CleanupTasks.WhenAllAsync(report)).ThrowsExactly<AggregateException>();
        await Assert.That(error!.Flatten().InnerExceptions.Count).IsEqualTo(9);
        await Assert.That(error.ToString()).Contains("120 earlier Sentinel background failures");
    }

    [Test]
    public async Task LearnedRemovalSignalsMembershipAndFreesCapacityWithoutRemovingSeeds()
    {
        var seed = new RespireEndpoint("seed", 26379);
        var discovery = new SentinelDiscoveryState([seed]);
        for (var index = 0; index < SentinelDiscoveryState.MaximumDiscoveredEndpoints; index++)
            await Assert.That(discovery.TryAdd(new($"learned-{index}", 26379))).IsTrue();
        discovery.Snapshot(out var changed);
        await Assert.That(discovery.TryRemove(new("SEED", 26379))).IsFalse();
        await Assert.That(discovery.TryRemove(new("missing", 26379))).IsFalse();
        await Assert.That(changed.IsCompleted).IsFalse();
        await Assert.That(discovery.TryAdd(new("extra", 26379))).IsFalse();
        await Assert.That(discovery.TryRemove(new("LEARNED-0", 26379))).IsTrue();
        await changed.WaitAsync(Limit);
        await Assert.That(discovery.TryAdd(new("extra", 26379))).IsTrue();
        await Assert.That(discovery.Snapshot()[0]).IsEqualTo(seed);
        await Assert.That(discovery.Snapshot().Length).IsEqualTo(65);
    }
}
