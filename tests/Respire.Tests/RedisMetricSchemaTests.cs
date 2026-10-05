using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Respire.Internal;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

[NotInParallel]
public class RedisMetricSchemaTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task QueryEvictionCallbackDoesNotBlockOtherQueryPublication(bool capacity)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ClientSideCacheCoordinator.CacheStore(new() { MaxEntries = 1 }, _ =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        });
        var key = new RespireKey("query-callback");
        var query = new ClientCacheCommandKey("HGET", "query-callback", "first");
        using var response = RespValue.BulkString("value"u8.ToArray());
        store.Set(in query, new(response.ToOwned(), [key], 100, capacity ? 0 : 1));
        if (capacity)
        {
            var other = new ClientCacheCommandKey("HGET", "query-callback", "second");
            store.Set(in other, new(response.ToOwned(), [key], 100, 0));
        }
        var removal = Task.Run(() =>
        {
            if (capacity) store.Trim();
            else store.TryGet(in query, out _);
        });
        Task<bool>? publication = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(store.SizeBytes).IsEqualTo(capacity ? 100L : 0L);
            publication = Task.Run(() =>
            {
                var next = new ClientCacheCommandKey("HGET", "query-callback", "next");
                return store.Set(in next, new(response.ToOwned(), [key], 100, 0));
            });
            await Assert.That(await publication.WaitAsync(TimeSpan.FromSeconds(10))).IsTrue();
        }
        finally
        {
            release.TrySetResult();
            await removal.WaitAsync(TimeSpan.FromSeconds(10));
            if (publication is not null) await publication.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>Forces a flush between scalar and dependent-query removal for one invalidation.</summary>
    [Test]
    public async Task FlushDuringInvalidationCountsEachResponseOnce()
    {
        using var capture = new Capture();
        var cache = new ClientSideCacheCoordinator(new());
        var key = new RespireKey("racing-hash");
        Fill(cache, key);
        var request = new ClientSideCacheCoordinator.QueryRequest(new ClientCacheCommandKey("HGET", "racing-hash", "field"), key);
        var token = cache.BeginRead("HGET", in request);
        using var response = RespValue.BulkString("value"u8.ToArray());
        cache.CompleteRead(in token, in response, allowInsert: true);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var store = (ClientSideCacheCoordinator.CacheStore)typeof(ClientSideCacheCoordinator).GetField("_store", flags)!.GetValue(cache)!;
        var dependencies = (Lock)typeof(ClientSideCacheCoordinator.CacheStore).GetField("_dependencyLock", flags)!.GetValue(store)!;
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = Task.Run(() =>
        {
            lock (dependencies)
            {
                held.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }
        });
        Task invalidating = Task.CompletedTask;
        try
        {
            await held.Task.WaitAsync(TimeSpan.FromSeconds(10));
            invalidating = Task.Run(() => cache.Invalidate(in key, RespireClientCacheInvalidationReason.ServerInvalidation));
            // The scalar is removed, but dependency removal is blocked on the held gate.
            await Assert.That(SpinWait.SpinUntil(() => store.Count == 1, TimeSpan.FromSeconds(10))).IsTrue();
            using var flush = RespValue.Array(RespValue.SimpleString("invalidate"u8.ToArray()), RespValue.Null);
            cache.HandlePush(in flush);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(holder, invalidating).WaitAsync(TimeSpan.FromSeconds(10));
        }
        await Assert.That(capture.Items.Where(item => item.Name == "redis.client.csc.evictions").Sum(item => item.Value)).IsEqualTo(2);
        await Assert.That(cache.Count).IsEqualTo(0);
    }

    /// <summary>Checks a stale lookup cannot report an expiration already counted by a flush.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExpirationInRetiredStoreDoesNotCountAgain(bool queryEntry)
    {
        using var capture = new Capture();
        var cache = new ClientSideCacheCoordinator(new() { LocalExpiration = TimeSpan.Zero });
        var key = new RespireKey("retired-expiration");
        var query = new ClientCacheCommandKey("HGET", "retired-expiration", "field");
        if (queryEntry)
        {
            var request = new ClientSideCacheCoordinator.QueryRequest(query, key);
            var token = cache.BeginRead("HGET", in request);
            using var response = RespValue.BulkString("value"u8.ToArray());
            cache.CompleteRead(in token, in response, allowInsert: true);
        }
        else Fill(cache, key);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var store = (ClientSideCacheCoordinator.CacheStore)typeof(ClientSideCacheCoordinator).GetField("_store", flags)!.GetValue(cache)!;
        using var flush = RespValue.Array(RespValue.SimpleString("invalidate"u8.ToArray()), RespValue.Null);
        cache.HandlePush(in flush);
        var found = queryEntry ? store.TryGet(in query, out _) : store.TryGet(in key, out _);
        await Assert.That(found).IsFalse();
        await Assert.That(capture.Items.Where(item => item.Name == "redis.client.csc.evictions").Sum(item => item.Value)).IsEqualTo(1);
    }

    [Test]
    public async Task ExpirationUsesTtlReason()
    {
        using var capture = new Capture();
        var cache = new ClientSideCacheCoordinator(new() { LocalExpiration = TimeSpan.Zero });
        var key = new RespireKey("expired");
        Fill(cache, key);
        await Assert.That(cache.TryGet(in key, out _)).IsFalse();
        var expired = capture.Items.Single(item => item.Name == "redis.client.csc.evictions");
        await Assert.That(expired.Tags["redis.client.csc.reason"]).IsEqualTo("ttl");
    }

    [Test]
    [Arguments("clear")]
    [Arguments("mutation")]
    [Arguments("unknown")]
    [Arguments("continuity")]
    [Arguments("deferred-continuity")]
    [Arguments("moving")]
    public async Task LocalRemovalsDoNotCountAsStandardEvictions(string operation)
    {
        using var capture = new Capture();
        var cache = new ClientSideCacheCoordinator(new());
        var key = new RespireKey("local-removal");
        Fill(cache, key);
        switch (operation)
        {
            case "clear": cache.Clear(); break;
            case "mutation": cache.Invalidate(in key); break;
            case "unknown": cache.FlushForUnknownCommand(); break;
            case "continuity": cache.FlushForContinuityLoss(); break;
            case "deferred-continuity":
                ClientSideCacheCoordinator.PublishContinuityFlushMetrics(cache.FlushForContinuityLossWithoutMetrics());
                break;
            case "moving": cache.FlushForMovingRetirementFence(); break;
        }
        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(capture.Items.Any(item => item.Name == "redis.client.csc.evictions")).IsFalse();
    }

    [Test]
    public async Task OneServerInvalidationCountsEachDependentQueryResponse()
    {
        using var capture = new Capture();
        var cache = new ClientSideCacheCoordinator(new());
        var key = new RespireKey("hash");
        foreach (var field in new[] { "first", "second" })
        {
            var request = new ClientSideCacheCoordinator.QueryRequest(new ClientCacheCommandKey("HGET", "hash", field), key);
            var token = cache.BeginRead("HGET", in request);
            var response = RespValue.BulkString("value"u8.ToArray());
            cache.CompleteRead(in token, in response, allowInsert: true);
        }
        await Assert.That(cache.Count).IsEqualTo(2);
        cache.Invalidate(in key, RespireClientCacheInvalidationReason.ServerInvalidation);
        cache.Invalidate(in key, RespireClientCacheInvalidationReason.ServerInvalidation);
        var removed = capture.Items.Where(item => item.Name == "redis.client.csc.evictions").ToArray();
        await Assert.That(removed.Sum(item => item.Value)).IsEqualTo(2);
        await Assert.That(removed.All(item => Equals(item.Tags["redis.client.csc.reason"], "invalidation"))).IsTrue();
        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task MaintenanceUsesStandardIdentityAndNotificationAttributes()
    {
        using var capture = new Capture();
        RespireTelemetry.RecordMaintenanceNotification("maintenance.example", 6380, "MOVING");
        var item = capture.Items.Single(item => item.Name == "redis.client.maintenance.notifications");
        await CheckIdentity(item);
        await Assert.That(item.Unit).IsEqualTo("{notification}");
        await Assert.That(item.Value).IsEqualTo(1);
        await Assert.That(item.Tags["server.address"]).IsEqualTo("maintenance.example");
        await Assert.That(item.Tags["server.port"]).IsEqualTo(6380);
        await Assert.That(item.Tags["redis.client.connection.notification"]).IsEqualTo("MOVING");
        await Assert.That(item.Tags.ContainsKey("respire.maintenance.kind")).IsFalse();
    }

    [Test]
    public async Task ThrowingStandardListenersDoNotChangeCacheOrSuppressSelectionMetrics()
    {
        using var capture = new Capture();
        using var throwing = new MeterListener();
        throwing.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name.StartsWith("redis.client.", StringComparison.Ordinal))
                listener.EnableMeasurementEvents(instrument);
        };
        throwing.SetMeasurementEventCallback<long>((_, _, _, _) => throw new InvalidOperationException("Listener failure."));
        throwing.Start();
        var cache = new ClientSideCacheCoordinator(new());
        var key = new RespireKey("listener-isolation");
        await Assert.That(cache.TryGet(in key, out _)).IsFalse();
        Fill(cache, key);
        await Assert.That(cache.TryGet(in key, out _)).IsTrue();
        cache.Invalidate(in key, RespireClientCacheInvalidationReason.ServerInvalidation);
        await Assert.That(cache.Count).IsEqualTo(0);
        RespireTelemetry.RecordFailoverSwitch(new("first"), new("second"), RespireFailoverSwitchReasons.ActiveEndpointUnhealthy);
        await Assert.That(capture.Items.Count(item => item.Name == "respire.failover.endpoint.switches")).IsEqualTo(1);
    }

    [Test]
    public async Task DisabledMappedCountersAllocateNothing()
    {
        for (var index = 0; index < 20; index++) MeasureMappedCounters();
        MeasurePositiveControl();
        var measurements = AllocationMeasurement.WithoutConcurrentGc(() => (MeasureMappedCounters(), MeasurePositiveControl()));
        await Assert.That(measurements.Item1).IsEqualTo(0);
        await Assert.That(measurements.Item2 > 0).IsTrue();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureMappedCounters()
    {
        var first = new RespireEndpoint("first");
        var second = new RespireEndpoint("second");
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++)
        {
            RespireTelemetry.RecordCacheRequest(hit: true);
            RespireTelemetry.RecordCacheRequest(hit: false);
            RespireTelemetry.RecordCacheEvictions(1, "full");
            RespireTelemetry.RecordMaintenanceNotification("maintenance", 6379, "MOVING");
            RespireTelemetry.RecordFailoverSwitch(first, second, RespireFailoverSwitchReasons.ActiveEndpointUnhealthy);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasurePositiveControl()
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        GC.KeepAlive(new byte[64]);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Test]
    public async Task CacheRequestsShareInstrumentWithHitAndMissLabels()
    {
        using var capture = new Capture();
        var cache = new ClientSideCacheCoordinator(new());
        var key = new RespireKey("schema-request");
        cache.TryGet(in key, out _);
        Fill(cache, key);
        cache.TryGet(in key, out _);

        var requests = capture.Items.Where(item => item.Name == "redis.client.csc.requests").ToArray();
        await Assert.That(requests.Length).IsEqualTo(2);
        await Assert.That(requests.Select(item => item.Tags["redis.client.csc.result"])).IsEquivalentTo(new object?[] { "miss", "hit" });
        foreach (var item in requests)
        {
            await Assert.That(item.Unit).IsEqualTo("{request}");
            await Assert.That(item.Value).IsEqualTo(1);
            await CheckIdentity(item);
        }
        await Assert.That(capture.Items.Any(item => item.Name is "respire.client_cache.hits" or "respire.client_cache.misses")).IsFalse();
    }

    [Test]
    public async Task EvictionsCountRemovedEntriesRatherThanInvalidatedKeys()
    {
        using var capture = new Capture();
        var cache = new ClientSideCacheCoordinator(new() { MaxEntries = 1 });
        Fill(cache, new("first"));
        Fill(cache, new("second"));
        var remaining = new RespireKey(cache.TryPeek(new RespireKey("first"), out _) ? "first" : "second");
        cache.Invalidate(in remaining, RespireClientCacheInvalidationReason.ServerInvalidation);
        var absent = new RespireKey("absent");
        cache.Invalidate(in absent, RespireClientCacheInvalidationReason.ServerInvalidation);

        var evictions = capture.Items.Where(item => item.Name == "redis.client.csc.evictions").ToArray();
        await Assert.That(evictions.Length).IsEqualTo(2);
        await Assert.That(evictions.Select(item => item.Tags["redis.client.csc.reason"])).IsEquivalentTo(new object?[] { "full", "invalidation" });
        foreach (var item in evictions)
        {
            await Assert.That(item.Unit).IsEqualTo("{eviction}");
            await Assert.That(item.Value).IsEqualTo(1);
            await CheckIdentity(item);
        }
        // The existing statistics continue to count capacity/expiry and flush removals.
        await Assert.That(cache.GetStatistics().Evictions).IsEqualTo(1);
        await Assert.That(capture.Items.Count(item => item.Name == "respire.client_cache.invalidations")).IsEqualTo(2);
    }

    [Test]
    public async Task GeographicFailoversExcludeInitialSelectionAndUnavailableStates()
    {
        using var capture = new Capture();
        var first = new RespireEndpoint("first.example", 6379);
        var second = new RespireEndpoint("second.example", 6380);
        RespireTelemetry.RecordFailoverSwitch(null, first, RespireFailoverSwitchReasons.FirstHealthy);
        RespireTelemetry.RecordFailoverSwitch(first, null, RespireFailoverSwitchReasons.NoHealthyEndpoint);
        RespireTelemetry.RecordFailoverSwitch(null, second, RespireFailoverSwitchReasons.RecoveredFromNoHealthyEndpoint);
        RespireTelemetry.RecordFailoverSwitch(first, first, RespireFailoverSwitchReasons.ActiveEndpointUnhealthy);
        RespireTelemetry.RecordFailoverSwitch(first, second, RespireFailoverSwitchReasons.ActiveEndpointUnhealthy);

        var item = capture.Items.Single(item => item.Name == "redis.client.geofailover.failovers");
        await Assert.That(item.Unit).IsEqualTo("{failover}");
        await Assert.That(item.Value).IsEqualTo(1);
        await Assert.That(item.Tags["db.client.geofailover.reason"]).IsEqualTo("automatic");
        await Assert.That(item.Tags["db.client.geofailover.fail_from"]).IsEqualTo(first.ToString());
        await Assert.That(item.Tags["db.client.geofailover.fail_to"]).IsEqualTo(second.ToString());
        await CheckIdentity(item);
        await Assert.That(capture.Items.Count(item => item.Name == "respire.failover.endpoint.switches")).IsEqualTo(5);
    }

    private static void Fill(ClientSideCacheCoordinator cache, RespireKey key)
    {
        var token = cache.BeginRead(in key);
        var response = RespValue.BulkString("value"u8.ToArray());
        cache.CompleteRead(in token, in response, allowInsert: true);
    }

    private static async Task CheckIdentity(Item item)
    {
        await Assert.That(item.IsCounter).IsTrue();
        await Assert.That(item.Tags["db.system.name"]).IsEqualTo("redis");
        await Assert.That(item.Tags["redis.client.library"]?.ToString()).StartsWith("Respire:");
    }

    private sealed record Item(string Name, string? Unit, long Value, Dictionary<string, object?> Tags, bool IsCounter);

    private sealed class Capture : IDisposable
    {
        private readonly MetricConfigurationScope _metrics = new();
        private readonly MeterListener _listener = new();
        internal ConcurrentQueue<Item> Items { get; } = new();

        internal Capture()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Respire") listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                Items.Enqueue(new(instrument.Name, instrument.Unit, value, tags.ToArray().ToDictionary(pair => pair.Key, pair => pair.Value),
                    instrument is Counter<long>)));
            _listener.Start();
        }

        public void Dispose()
        {
            _listener.Dispose();
            _metrics.Dispose();
        }
    }
}
