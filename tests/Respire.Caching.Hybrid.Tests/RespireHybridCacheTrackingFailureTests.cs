using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Respire.Testing.Containers;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Caching.Hybrid.Tests;

public class RespireHybridCacheTrackingFailureTests
{
    [Test]
    [NotInParallel]
    public async Task CancelledCallerDoesNotLeaveTrackingDisconnectUnobserved()
    {
        var unobserved = new ConcurrentBag<int>();

        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs _)
        {
            if (sender is Task task)
            {
                unobserved.Add(task.Id);
            }
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;

        try
        {
            var (trackingId, tracking) = await CancelReadAndDisconnectAsync();
            // Keep pumping finalizers after the task becomes unreachable. The holder
            // that reports the exception can be finalized on a later collection.
            for (var attempt = 0; attempt < 10; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Yield();
            }

            await Assert.That(tracking.IsAlive).IsFalse();
            await Assert.That(unobserved.Contains(trackingId)).IsFalse();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<(int Id, WeakReference Reference)> CancelReadAndDisconnectAsync()
    {
        // CLIENT PAUSE affects the whole server. Give this test its own Redis instance.
        await using var server = await RespireContainerFixture.StartAsync(new() { Image = RedisTestContainer.ReferenceRedisImage });
        await using var source = RespireClient.Create(new RespireOptions
        {
            Endpoints = [server.DataEndpoints[0]],
            Protocol = RespireProtocol.Resp3,
            CommandTimeout = null,
        });
        var services = new ServiceCollection();
        services.AddSingleton<IRespireClient>(source);
        services.AddRespireHybridCache().WithRespireClientSideCoherence();
        await using var provider = services.BuildServiceProvider();
        var cache = (RespireCoherentHybridCache)provider.GetRequiredService<HybridCache>();
        using var setupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await cache.TrackingClient.PingAsync(setupDeadline.Token);

        using (await source.ExecuteAsync("CLIENT", setupDeadline.Token, "PAUSE", "60000", "ALL"))
        {
        }

        using var cancellation = new CancellationTokenSource();
        var key = Guid.NewGuid().ToString("N");
        var read = cache.GetOrCreateAsync<string>(key, _ => throw new InvalidOperationException("L2 must not run."),
            cancellationToken: cancellation.Token).AsTask();

        // Capture the exact producer task without consuming its result or exception.
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var observations = (IDictionary)typeof(RespireCoherentHybridCache).GetField("_observations", fields)!.GetValue(cache)!;
        var observation = observations[key]!;
        var tracking = (Task)observation.GetType().GetField("Tracking", fields)!.GetValue(observation)!;
        await Assert.That(tracking.IsCompleted).IsFalse();

        await cancellation.CancelAsync();
        await Assert.That(async () => await read.WaitAsync(TimeSpan.FromSeconds(5))).Throws<OperationCanceledException>();
        await Assert.That(cache.ObservationCount).IsEqualTo(0);
        await Assert.That(tracking.IsCompleted).IsFalse();

        await cache.TrackingClient.DisposeAsync();
        await Task.WhenAny(tracking).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(tracking.IsFaulted).IsTrue();
        return (tracking.Id, new WeakReference(tracking));
    }
}
