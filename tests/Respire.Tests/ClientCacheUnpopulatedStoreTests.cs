using System.Reflection;
using Respire.Internal;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ClientCacheUnpopulatedStoreTests
{
    [Test]
    [Arguments("_removalLock", false)]
    [Arguments("_dependencyLock", false)]
    [Arguments("_removalLock", true)]
    [Arguments("_dependencyLock", true)]
    public async Task UnpopulatedStoreSkipsStoreGatesButFencesPendingReads(string gateName, bool queryRead)
    {
        var cache = new ClientSideCacheCoordinator(new());
        RespireKey key = "key";
        var query = new ClientCacheCommandKey("STRLEN", key.AsValue());
        var request = new ClientSideCacheCoordinator.QueryRequest(query, key);
        var read = queryRead ? default : cache.BeginRead(in key);
        var queryToken = queryRead ? cache.BeginRead("STRLEN", in request) : default;
        var store = queryRead ? queryToken.Store : read.Store;
        var gate = (Lock)typeof(ClientSideCacheCoordinator.CacheStore)
            .GetField(gateName, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = Task.Factory.StartNew(() =>
        {
            lock (gate)
            {
                held.SetResult();
                release.Task.GetAwaiter().GetResult();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task invalidating = Task.CompletedTask;
        try
        {
            await held.Task.WaitAsync(TimeSpan.FromSeconds(10));
            invalidating = Task.Run(() => cache.Invalidate(in key));
            await invalidating.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.TrySetResult();
            await holder.WaitAsync(TimeSpan.FromSeconds(10));
            await invalidating.WaitAsync(TimeSpan.FromSeconds(10));
            using var response = queryRead ? RespValue.Integer(3) : RespValue.BulkString("old"u8.ToArray());
            if (queryRead) cache.CompleteRead(in queryToken, in response, allowInsert: true);
            else cache.CompleteRead(in read, in response, allowInsert: true);
        }
        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(cache.InspectForTests().PendingQueryDependencyCount).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FirstPublicationAndRepopulationRemainInvalidatable(bool queryRead)
    {
        var cache = new ClientSideCacheCoordinator(new());
        RespireKey key = "key";
        var query = new ClientCacheCommandKey("STRLEN", key.AsValue());
        var request = new ClientSideCacheCoordinator.QueryRequest(query, key);
        for (var iteration = 0; iteration < 3; iteration++)
        {
            // An empty-store invalidation must not disable invalidation after first publication.
            cache.Invalidate(in key);
            using var response = queryRead ? RespValue.Integer(3) : RespValue.BulkString("new"u8.ToArray());
            if (queryRead)
            {
                var token = cache.BeginRead("STRLEN", in request);
                cache.CompleteRead(in token, in response, allowInsert: true);
            }
            else
            {
                var token = cache.BeginRead(in key);
                cache.CompleteRead(in token, in response, allowInsert: true);
            }
            await Assert.That(cache.Count).IsEqualTo(1);
            cache.Invalidate(in key, RespireClientCacheInvalidationReason.ServerInvalidation);
            await Assert.That(cache.Count).IsEqualTo(0);
            await Assert.That(cache.SizeBytes).IsEqualTo(0);
            if (iteration == 1) cache.FlushForContinuityLoss();
        }
    }
}
