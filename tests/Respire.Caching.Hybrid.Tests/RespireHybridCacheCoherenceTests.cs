using System.Buffers;
using System.Text;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Respire.Compression;
using Respire.Testing.Containers;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Caching.Hybrid.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.Keyed, Key = "HybridCacheCoherence")]
public class RespireHybridCacheCoherenceTests(RedisTestContainer fixture)
{
    private const string InstanceName = "coherent:";
    private static readonly HybridCacheEntryOptions LongLived = new()
    {
        Expiration = TimeSpan.FromHours(1),
        LocalCacheExpiration = TimeSpan.FromHours(1),
    };

    [Test]
    [NotInParallel]
    public async Task ReplicaRoutingOnTheSourceStillTracksPrimaryInvalidations()
    {
        await using var topology = await RespireContainerFixture.StartAsync(new()
        {
            Topology = RespireContainerTopology.Sentinel,
        });
        await using var source = RespireClient.Create(new RespireOptions
        {
            Endpoints = [topology.DataEndpoints[0]],
            ReplicaEndpoints = [topology.DataEndpoints[1]],
            ReadFrom = RespireReadFrom.Replica,
            Connections = 1,
        });
        var key = NewKey();
        await using var writer = BuildProvider(false, source);
        await using var reader = BuildProvider(true, source);
        await writer.GetRequiredService<HybridCache>().SetAsync(key, "old", LongLived);
        await UntilAsync(async () => await source.Hashes.ExistsAsync(InstanceName + key, "data"));
        await PrimeAsync(reader.GetRequiredService<HybridCache>(), key);
        await writer.GetRequiredService<HybridCache>().SetAsync(key, "new", LongLived);
        await UntilAsync(() => Coherent(reader).ObservationCount == 0);
        await Assert.That(await ReadAsync(reader.GetRequiredService<HybridCache>(), key)).IsEqualTo("new");
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task AnotherProviderUpdatesL1OnlyWhenCoherenceIsEnabled(bool enabled, bool registeredClient)
    {
        var key = NewKey();
        await using var root = RespireClient.Create(RespireOptions.Parse(fixture.ConnectionString) with { KeyPrefix = "client:" });
        await using var writer = BuildProvider(false, root);
        await using var reader = BuildProvider(enabled, registeredClient ? root : null,
            clientPrefix: registeredClient ? null : "client:");
        var writeCache = writer.GetRequiredService<HybridCache>();
        var readCache = reader.GetRequiredService<HybridCache>();
        await writeCache.SetAsync(key, "old", LongLived);
        await PrimeAsync(readCache, key);
        await writeCache.SetAsync(key, "new", LongLived);
        var payload = await writer.GetRequiredService<IDistributedCache>().GetAsync(key);
        if (enabled) await UntilAsync(() => Coherent(reader).ObservationCount == 0);

        var value = await ReadAsync(readCache, key);

        await Assert.That(value).IsEqualTo(enabled ? "new" : "old");
        var after = await writer.GetRequiredService<IDistributedCache>().GetAsync(key);
        await Assert.That(after!.AsSpan().SequenceEqual(payload)).IsTrue();
        // Disposing the reader must not dispose an externally registered client.
        await reader.DisposeAsync();
        await Assert.That(await root.ExistsAsync(InstanceName + key)).IsTrue();
    }

    [Test]
    [Arguments(RespireClientTrackingMode.OptIn)]
    [Arguments(RespireClientTrackingMode.Broadcast)]
    public async Task PhysicalTrackingPrefixesCoverHashBackedL2(RespireClientTrackingMode mode)
    {
        var key = NewKey();
        await using var writer = BuildProvider(false, clientPrefix: "physical:");
        await using var reader = BuildProvider(true, clientPrefix: "physical:", configure: options =>
            options.TrackingOptions = new() { TrackingMode = mode, KeyPrefixes = ["physical:" + InstanceName] });
        await writer.GetRequiredService<HybridCache>().SetAsync(key, "old", LongLived);
        await PrimeAsync(reader.GetRequiredService<HybridCache>(), key);
        await writer.GetRequiredService<HybridCache>().SetAsync(key, "new", LongLived);
        await UntilAsync(() => Coherent(reader).ObservationCount == 0);
        await Assert.That(await ReadAsync(reader.GetRequiredService<HybridCache>(), key)).IsEqualTo("new");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CodecAndMutableCopiesSurviveGenerationChanges(bool registeredClient)
    {
        var key = NewKey();
        await using var root = RespireClient.Create(fixture.ConnectionString);
        await using var writer = BuildProvider(false, codec: true);
        await using var reader = BuildProvider(true, registeredClient ? root : null, codec: true);
        var original = Enumerable.Repeat((byte)42, 4096).ToArray();
        await writer.GetRequiredService<HybridCache>().SetAsync(key, original, LongLived);
        var cache = reader.GetRequiredService<HybridCache>();
        var first = await cache.GetOrCreateAsync<byte[]>(key, _ => throw new InvalidOperationException("L2 must supply the payload."));
        first[0] = 0;
        var second = await cache.GetOrCreateAsync<byte[]>(key, _ => throw new InvalidOperationException("L1 or L2 must supply the payload."));
        await Assert.That(second[0]).IsEqualTo((byte)42);
        var replacement = Enumerable.Repeat((byte)99, 4096).ToArray();
        await writer.GetRequiredService<HybridCache>().SetAsync(key, replacement, LongLived);
        await UntilAsync(() => Coherent(reader).ObservationCount == 0);
        var fresh = await cache.GetOrCreateAsync<byte[]>(key, _ => throw new InvalidOperationException("Updated L2 must survive eviction."));
        await Assert.That(fresh.AsSpan().SequenceEqual(replacement)).IsTrue();
        using var raw = await root.ExecuteAsync("HGET", InstanceName + key, "data");
        await Assert.That(raw.AsSpan()[..4].SequenceEqual("RVC\0"u8)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InvalidationRetiresAParkedFillAndItsStampede(bool cancelCaller)
    {
        var key = NewKey();
        await using var writer = BuildProvider(false);
        await using var reader = BuildProvider(true);
        var cache = reader.GetRequiredService<HybridCache>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var fill = cache.GetOrCreateAsync(key, async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            finished.TrySetResult();
            return "old factory result";
        }, new HybridCacheEntryOptions
        {
            Flags = HybridCacheEntryFlags.DisableDistributedCacheWrite,
            LocalCacheExpiration = TimeSpan.FromHours(1),
        }, cancellationToken: cancellation.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(Coherent(reader).ObservationCount).IsEqualTo(1);
            await writer.GetRequiredService<HybridCache>().SetAsync(key, "new", LongLived);
            await UntilAsync(() => Coherent(reader).ObservationCount == 0);
            if (cancelCaller)
            {
                cancellation.Cancel();
                await Assert.That(async () => await fill).Throws<OperationCanceledException>();
            }
            // Read before releasing the old producer: a later request must not join it.
            await Assert.That(await ReadAsync(cache, key).AsTask().WaitAsync(TimeSpan.FromSeconds(10))).IsEqualTo("new");
            release.TrySetResult();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (!cancelCaller) await Assert.That(await fill).IsEqualTo("old factory result");
            await Assert.That(await ReadAsync(cache, key)).IsEqualTo("new");
            await using var fresh = BuildProvider(false);
            await Assert.That(await ReadAsync(fresh.GetRequiredService<HybridCache>(), key)).IsEqualTo("new");
        }
        finally { release.TrySetResult(); }
    }

    [Test]
    public async Task InvalidationDuringSetSerializationCannotPublishIntoTheNextGeneration()
    {
        var key = NewKey();
        using var serializer = new ParkedSerializer();
        using var writerSerializer = new ParkedSerializer();
        await using var writer = BuildProvider(false, serializer: writerSerializer);
        await using var reader = BuildProvider(true, serializer: serializer);
        serializer.Park = true;
        var cache = reader.GetRequiredService<HybridCache>();
        var write = Task.Run(async () => await cache.SetAsync(key, new TestValue("old"), new HybridCacheEntryOptions
        {
            Flags = HybridCacheEntryFlags.DisableDistributedCacheWrite,
            LocalCacheExpiration = TimeSpan.FromHours(1),
        }));
        try
        {
            await serializer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await writer.GetRequiredService<HybridCache>().SetAsync(key, new TestValue("new"), LongLived);
            await UntilAsync(() => Coherent(reader).ObservationCount == 0);
            serializer.Release.Set();
            await write.WaitAsync(TimeSpan.FromSeconds(10));
            var result = await cache.GetOrCreateAsync<TestValue>(key, _ => throw new InvalidOperationException("L2 must survive."));
            await Assert.That(result.Text).IsEqualTo("new");
        }
        finally { serializer.Release.Set(); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RemovalAndServerExpiryInvalidateLongLivedLocalEntries(bool expire)
    {
        var key = NewKey();
        await using var writer = BuildProvider(false);
        await using var reader = BuildProvider(true);
        await writer.GetRequiredService<HybridCache>().SetAsync(key, "old", LongLived);
        await PrimeAsync(reader.GetRequiredService<HybridCache>(), key);
        await using var external = RespireClient.Create(fixture.ConnectionString);
        using var result = expire
            ? await external.ExecuteAsync("PEXPIRE", InstanceName + key, "1")
            : await external.ExecuteAsync("DEL", InstanceName + key);
        await UntilAsync(() => Coherent(reader).ObservationCount == 0);
        var calls = 0;
        var value = await reader.GetRequiredService<HybridCache>().GetOrCreateAsync(key, _ =>
        {
            calls++;
            return ValueTask.FromResult("recreated");
        }, new HybridCacheEntryOptions { Flags = HybridCacheEntryFlags.DisableDistributedCacheWrite });
        await Assert.That(value).IsEqualTo("recreated");
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExplicitClearRetiresLocalStateWithoutDeletingL2(bool clearSource)
    {
        var key = NewKey();
        await using var source = RespireClient.Create(RespireOptions.Parse(fixture.ConnectionString) with { ClientSideCache = new() });
        await using var writer = BuildProvider(false);
        await using var reader = BuildProvider(true, source);
        await writer.GetRequiredService<HybridCache>().SetAsync(key, "value", LongLived);
        await PrimeAsync(reader.GetRequiredService<HybridCache>(), key);
        (clearSource ? source.ClientSideCache! : Coherent(reader).TrackingClient.ClientSideCache!).Clear();
        await UntilAsync(() => Coherent(reader).ObservationCount == 0);
        await Assert.That(await ReadAsync(reader.GetRequiredService<HybridCache>(), key)).IsEqualTo("value");
        await Assert.That(await writer.GetRequiredService<IDistributedCache>().GetAsync(key)).IsNotNull();
    }

    [Test]
    public async Task TrackingDisconnectRetiresLocalStateAndReconnects()
    {
        var key = NewKey();
        await using var writer = BuildProvider(false);
        await using var reader = BuildProvider(true);
        await writer.GetRequiredService<HybridCache>().SetAsync(key, "old", LongLived);
        await PrimeAsync(reader.GetRequiredService<HybridCache>(), key);
        using var identity = await Coherent(reader).TrackingClient.ExecuteAsync("CLIENT", "ID");
        await using var external = RespireClient.Create(fixture.ConnectionString);
        using var killed = await external.ExecuteAsync("CLIENT", "KILL", "ID", identity.AsInteger().ToString(System.Globalization.CultureInfo.InvariantCulture));
        await UntilAsync(() => Coherent(reader).ObservationCount == 0);
        await writer.GetRequiredService<HybridCache>().SetAsync(key, "new", LongLived);
        await PrimeAsync(reader.GetRequiredService<HybridCache>(), key);
        await Assert.That(await ReadAsync(reader.GetRequiredService<HybridCache>(), key)).IsEqualTo("new");
    }

    [Test]
    public async Task CapacityBypassesL1AndExpiredEntriesReleaseObservations()
    {
        var held = NewKey();
        var overflow = NewKey();
        await using var writer = BuildProvider(false);
        await using var reader = BuildProvider(true, configure: options => options.MaxObservedKeys = 1);
        var cache = reader.GetRequiredService<HybridCache>();
        await writer.GetRequiredService<HybridCache>().SetAsync(held, "held", LongLived);
        await writer.GetRequiredService<HybridCache>().SetAsync(overflow, "old", LongLived);
        await cache.GetOrCreateAsync<string>(held, _ => throw new InvalidOperationException(),
            new HybridCacheEntryOptions { LocalCacheExpiration = TimeSpan.FromSeconds(1) });
        await Assert.That(Coherent(reader).ObservationCount).IsEqualTo(1);
        await Assert.That(await ReadAsync(cache, overflow)).IsEqualTo("old");
        await writer.GetRequiredService<HybridCache>().SetAsync(overflow, "new", LongLived);
        await Assert.That(await ReadAsync(cache, overflow)).IsEqualTo("new");
        await Assert.That(Coherent(reader).ObservationCount).IsEqualTo(1);
        await UntilAsync(() => { Coherent(reader).SweepObservations(); return Coherent(reader).ObservationCount == 0; });
        await PrimeAsync(cache, overflow);
        await Assert.That(Coherent(reader).ObservationCount).IsEqualTo(1);
    }

    [Test]
    public async Task MemoryEvictionReleasesObservations()
    {
        var key = NewKey();
        await using var writer = BuildProvider(false);
        await using var reader = BuildProvider(true);
        await writer.GetRequiredService<HybridCache>().SetAsync(key, "value", LongLived);
        await PrimeAsync(reader.GetRequiredService<HybridCache>(), key);
        ((MemoryCache)reader.GetRequiredService<IMemoryCache>()).Compact(1);
        await UntilAsync(() => Coherent(reader).ObservationCount == 0);
        await Assert.That(await ReadAsync(reader.GetRequiredService<HybridCache>(), key)).IsEqualTo("value");
    }

    [Test]
    public async Task UncoveredPrefixesBypassLocalCaching()
    {
        var key = NewKey();
        await using var writer = BuildProvider(false);
        await using var reader = BuildProvider(true, configure: options => options.TrackingOptions = new() { KeyPrefixes = ["elsewhere:"] });
        await writer.GetRequiredService<HybridCache>().SetAsync(key, "old", LongLived);
        await Assert.That(await ReadAsync(reader.GetRequiredService<HybridCache>(), key)).IsEqualTo("old");
        await writer.GetRequiredService<HybridCache>().SetAsync(key, "new", LongLived);
        await Assert.That(await ReadAsync(reader.GetRequiredService<HybridCache>(), key)).IsEqualTo("new");
        await Assert.That(Coherent(reader).ObservationCount).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TrackerShutdownBypassesL1AndProviderDisposalReleasesSubscriptions(bool prime)
    {
        var key = NewKey();
        await using var writer = BuildProvider(false);
        await using var reader = BuildProvider(true);
        var cache = reader.GetRequiredService<HybridCache>();
        var coherent = Coherent(reader);
        await writer.GetRequiredService<HybridCache>().SetAsync(key, "old", LongLived);
        if (prime) await PrimeAsync(cache, key);
        await coherent.TrackingClient.DisposeAsync();
        await UntilAsync(() => coherent.ObservationCount == 0);
        await writer.GetRequiredService<HybridCache>().SetAsync(key, "new", LongLived);
        await Assert.That(await ReadAsync(cache, key)).IsEqualTo("new");
        await Assert.That(coherent.ObservationCount).IsEqualTo(0);
        await reader.DisposeAsync();
        await Assert.That(async () => await ReadAsync(cache, key)).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task NormalFactoryCoalescingTagsAndRemoveRemainAvailable()
    {
        var key = NewKey();
        await using var reader = BuildProvider(true);
        var cache = reader.GetRequiredService<HybridCache>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async ValueTask<string> Factory(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task;
            return "value";
        }
        var first = cache.GetOrCreateAsync(key, Factory, tags: ["tag-" + key]).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var second = cache.GetOrCreateAsync(key, Factory).AsTask();
            release.TrySetResult();
            await Task.WhenAll(first, second);
            await Assert.That(calls).IsEqualTo(1);
            await UntilAsync(async () => await reader.GetRequiredService<IDistributedCache>().GetAsync(key) is not null);
            await cache.RemoveByTagAsync("tag-" + key);
            await Assert.That(await cache.GetOrCreateAsync(key, _ => ValueTask.FromResult("after tag"))).IsEqualTo("after tag");
            await UntilAsync(async () => await reader.GetRequiredService<IDistributedCache>().GetAsync(key) is not null);
            await cache.RemoveAsync(key);
            await Assert.That(await reader.GetRequiredService<IDistributedCache>().GetAsync(key)).IsNull();
            await Assert.That(await cache.GetOrCreateAsync(key, _ => ValueTask.FromResult("after remove"),
                new HybridCacheEntryOptions { Flags = HybridCacheEntryFlags.DisableDistributedCacheWrite })).IsEqualTo("after remove");
        }
        finally { release.TrySetResult(); }
    }

    [Test]
    public async Task LocalTagRemovalSurvivesEvictionAndPreservesNewerTaggedL2Values()
    {
        var key = NewKey();
        var tag = "tag-" + key;
        await using var writer = BuildProvider(false);
        await using var reader = BuildProvider(true);
        var cache = reader.GetRequiredService<HybridCache>();
        await writer.GetRequiredService<HybridCache>().SetAsync(key, "old", LongLived, [tag]);
        await PrimeAsync(cache, key);
        await cache.RemoveByTagAsync(tag);
        ((MemoryCache)reader.GetRequiredService<IMemoryCache>()).Compact(1);
        await UntilAsync(() => Coherent(reader).ObservationCount == 0);
        await Assert.That(await cache.GetOrCreateAsync(key, _ => ValueTask.FromResult("replacement"),
            new HybridCacheEntryOptions { Flags = HybridCacheEntryFlags.DisableDistributedCacheWrite }, [tag])).IsEqualTo("replacement");

        await writer.GetRequiredService<HybridCache>().SetAsync(key, "newer", LongLived, [tag]);
        await UntilAsync(() => Coherent(reader).ObservationCount == 0);
        await Assert.That(await ReadAsync(cache, key)).IsEqualTo("newer");
        ((MemoryCache)reader.GetRequiredService<IMemoryCache>()).Compact(1);
        await UntilAsync(() => Coherent(reader).ObservationCount == 0);
        await Assert.That(await ReadAsync(cache, key)).IsEqualTo("newer");
    }

    [Test]
    public async Task ExhaustedTagHistoryBypassesLocalCaching()
    {
        var key = NewKey();
        await using var writer = BuildProvider(false);
        await using var reader = BuildProvider(true, configure: options => options.MaxRememberedTagInvalidations = 1);
        var cache = reader.GetRequiredService<HybridCache>();
        await writer.GetRequiredService<HybridCache>().SetAsync(key, "old", LongLived);
        await PrimeAsync(cache, key);
        await cache.RemoveByTagAsync("first-" + key);
        await cache.RemoveByTagAsync("second-" + key);
        await Assert.That(Coherent(reader).ObservationCount).IsEqualTo(0);
        await writer.GetRequiredService<HybridCache>().SetAsync(key, "new", LongLived);
        await Assert.That(await ReadAsync(cache, key)).IsEqualTo("new");
        await writer.GetRequiredService<HybridCache>().SetAsync(key, "newer", LongLived);
        await Assert.That(await ReadAsync(cache, key)).IsEqualTo("newer");
        await Assert.That(Coherent(reader).ObservationCount).IsEqualTo(0);
    }

    [Test]
    public async Task FactoryFailureAndDisabledLocalCachingReleaseObservationCapacity()
    {
        var key = NewKey();
        await using var provider = BuildProvider(true, configure: options => options.MaxObservedKeys = 1);
        var cache = provider.GetRequiredService<HybridCache>();
        await Assert.That(async () => await cache.GetOrCreateAsync<string>(key, _ =>
            throw new InvalidOperationException("factory failure"))).Throws<InvalidOperationException>();
        await Assert.That(Coherent(provider).ObservationCount).IsEqualTo(0);
        var calls = 0;
        for (var iteration = 0; iteration < 2; iteration++)
        {
            await cache.GetOrCreateAsync(key, _ => ValueTask.FromResult(++calls), new HybridCacheEntryOptions
            {
                Flags = HybridCacheEntryFlags.DisableLocalCache | HybridCacheEntryFlags.DisableDistributedCache,
            });
        }
        await Assert.That(calls).IsEqualTo(2);
        await Assert.That(Coherent(provider).ObservationCount).IsEqualTo(0);
    }

    [Test]
    public async Task BridgeDisposalRetiresAnActiveFillAndStopsItsOwnedTracker()
    {
        var key = NewKey();
        await using var provider = BuildProvider(true);
        var cache = provider.GetRequiredService<HybridCache>();
        var coherent = Coherent(provider);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fill = cache.GetOrCreateAsync(key, async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            return "result";
        }, new HybridCacheEntryOptions { Flags = HybridCacheEntryFlags.DisableDistributedCacheWrite }).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var trackerSubscription = coherent.TrackingClient.ClientSideCache!.SubscribeInvalidations(InstanceName + key, _ => { });
            await coherent.DisposeAsync();
            await Assert.That(trackerSubscription.Stopped.IsCancellationRequested).IsTrue();
            release.TrySetResult();
            await Assert.That(await fill.WaitAsync(TimeSpan.FromSeconds(10))).IsEqualTo("result");
            await Assert.That(coherent.ObservationCount).IsEqualTo(0);
            await Assert.That(async () => await ReadAsync(cache, key)).Throws<ObjectDisposedException>();
        }
        finally { release.TrySetResult(); }
    }

    [Test]
    public async Task DefaultOptionsAreConfiguredOnceAcrossLocalGenerations()
    {
        var calls = 0;
        var services = new ServiceCollection();
        services.AddRespireHybridCache(fixture.ConnectionString, InstanceName, options =>
        {
            calls++;
            options.DefaultEntryOptions = new HybridCacheEntryOptions
            {
                Flags = HybridCacheEntryFlags.DisableDistributedCacheWrite,
            };
        }).WithRespireClientSideCoherence();
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<HybridCache>();
        for (var iteration = 0; iteration < 3; iteration++)
            await cache.GetOrCreateAsync(NewKey(), _ => ValueTask.FromResult("value"));
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(Coherent(provider).ObservationCount).IsEqualTo(3);
    }

    [Test]
    public async Task RegisteredClientPrefixViewsResolveTheObservedPhysicalKey()
    {
        var key = NewKey();
        await using var root = RespireClient.Create(RespireOptions.Parse(fixture.ConnectionString) with { KeyPrefix = "tenant:" });
        var view = (RespireClient)root.WithKeyPrefix("service:");
        await using var writer = BuildProvider(false, view);
        await using var reader = BuildProvider(true, view);
        await writer.GetRequiredService<HybridCache>().SetAsync(key, "old", LongLived);
        await PrimeAsync(reader.GetRequiredService<HybridCache>(), key);
        await writer.GetRequiredService<HybridCache>().SetAsync(key, "new", LongLived);
        await UntilAsync(() => Coherent(reader).ObservationCount == 0);
        await Assert.That(await ReadAsync(reader.GetRequiredService<HybridCache>(), key)).IsEqualTo("new");
        await Assert.That(await root.ExistsAsync("service:" + InstanceName + key)).IsTrue();
    }

    [Test]
    public async Task DeniedTrackingReadBypassesL1UntilTrackingIsAvailable()
    {
        var key = NewKey();
        var username = "coherence-" + key;
        const string password = "ephemeral-test-password";
        await using var admin = RespireClient.Create(fixture.ConnectionString);
        using (var created = await admin.ExecuteAsync("ACL", "SETUSER", username, "on", ">" + password, "~*", "+@all", "-hexists")) { }
        try
        {
            await using var restricted = RespireClient.Create(RespireOptions.Parse(fixture.ConnectionString) with
            {
                Username = username,
                Password = password,
            });
            await using var writer = BuildProvider(false);
            await using var reader = BuildProvider(true, restricted);
            var cache = reader.GetRequiredService<HybridCache>();
            await writer.GetRequiredService<HybridCache>().SetAsync(key, "old", LongLived);
            await Assert.That(await ReadAsync(cache, key)).IsEqualTo("old");
            await Assert.That(Coherent(reader).ObservationCount).IsEqualTo(0);
            await writer.GetRequiredService<HybridCache>().SetAsync(key, "new", LongLived);
            await Assert.That(await ReadAsync(cache, key)).IsEqualTo("new");
            await Assert.That(Coherent(reader).ObservationCount).IsEqualTo(0);
            using var restored = await admin.ExecuteAsync("ACL", "SETUSER", username, "+hexists");
            await PrimeAsync(cache, key);
            await Assert.That(Coherent(reader).ObservationCount).IsEqualTo(1);
        }
        finally { using var removed = await admin.ExecuteAsync("ACL", "DELUSER", username); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OwnWritesRetireTheInitialFillAndLaterReadsStayLocal(bool factoryMiss)
    {
        var key = NewKey();
        var codec = new CountingCodec();
        await using var provider = BuildProvider(true, valueCodec: codec);
        var cache = provider.GetRequiredService<HybridCache>();
        if (factoryMiss)
            await cache.GetOrCreateAsync(key, _ => ValueTask.FromResult("value"), LongLived);
        else
            await cache.SetAsync(key, "value", LongLived);
        await UntilAsync(() => codec.Encodes == 1 && Coherent(provider).ObservationCount == 0);
        await Assert.That(await ReadAsync(cache, key)).IsEqualTo("value");
        var decodes = codec.Decodes;
        await Assert.That(decodes).IsEqualTo(1);
        await Assert.That(await ReadAsync(cache, key)).IsEqualTo("value");
        await Assert.That(codec.Decodes).IsEqualTo(decodes);
        await Assert.That(Coherent(provider).ObservationCount).IsEqualTo(1);
    }

    [Test]
    public async Task TagReplayUsesOneL2WriteAcrossExistingAndNewContexts()
    {
        var first = NewKey();
        var second = NewKey();
        var tag = "tag-" + first;
        var codec = new CountingCodec();
        await using var writer = BuildProvider(false);
        await using var reader = BuildProvider(true, valueCodec: codec);
        var cache = reader.GetRequiredService<HybridCache>();
        await writer.GetRequiredService<HybridCache>().SetAsync(first, "old", LongLived, [tag]);
        await writer.GetRequiredService<HybridCache>().SetAsync(second, "old", LongLived, [tag]);
        await PrimeAsync(cache, first);
        await PrimeAsync(cache, second);
        await Assert.That(Coherent(reader).ObservationCount).IsEqualTo(2);
        await cache.RemoveByTagAsync(tag);
        await Assert.That(codec.Encodes).IsEqualTo(1);
        ((MemoryCache)reader.GetRequiredService<IMemoryCache>()).Compact(1);
        await UntilAsync(() => Coherent(reader).ObservationCount == 0);
        await Assert.That(await cache.GetOrCreateAsync(first, _ => ValueTask.FromResult("replacement"),
            new HybridCacheEntryOptions { Flags = HybridCacheEntryFlags.DisableDistributedCacheWrite }, [tag]))
            .IsEqualTo("replacement");
        await Assert.That(codec.Encodes).IsEqualTo(1);
        await Assert.That(((MemoryCache)reader.GetRequiredService<IMemoryCache>()).Count).IsEqualTo(1);
    }

    [Test]
    public async Task NarrowSourcePrefixesDoNotPreventIndependentTracking()
    {
        var key = NewKey();
        await using var source = RespireClient.Create(RespireOptions.Parse(fixture.ConnectionString) with
        {
            ClientSideCache = new() { KeyPrefixes = ["elsewhere:"] },
        });
        await using var writer = BuildProvider(false);
        await using var reader = BuildProvider(true, source);
        await writer.GetRequiredService<HybridCache>().SetAsync(key, "old", LongLived);
        await PrimeAsync(reader.GetRequiredService<HybridCache>(), key);
        await writer.GetRequiredService<HybridCache>().SetAsync(key, "new", LongLived);
        await UntilAsync(() => Coherent(reader).ObservationCount == 0);
        await Assert.That(await ReadAsync(reader.GetRequiredService<HybridCache>(), key)).IsEqualTo("new");
    }

    [Test]
    public async Task SynchronousDisposalDoesNotJoinAnActiveObserver()
    {
        var key = NewKey();
        await using var writer = BuildProvider(false);
        await using var reader = BuildProvider(true);
        await writer.GetRequiredService<HybridCache>().SetAsync(key, "value", LongLived);
        await PrimeAsync(reader.GetRequiredService<HybridCache>(), key);
        var coherent = Coherent(reader);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = coherent.TrackingClient.ClientSideCache!.SubscribeInvalidations(InstanceName + key, _ =>
        {
            entered.TrySetResult();
            try
            {
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Observer was not released.");
            }
            finally { exited.TrySetResult(); }
        });
        try
        {
            coherent.TrackingClient.ClientSideCache.Clear();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(coherent.Dispose).WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(subscription.Stopped.IsCancellationRequested).IsTrue();
            await Assert.That(Coherent(reader).ObservationCount).IsEqualTo(0);
        }
        finally
        {
            release.Set();
            if (entered.Task.IsCompleted) await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await Assert.That(subscription.LastObserverException).IsNull();
    }

    private ServiceProvider BuildProvider(bool coherent, RespireClient? client = null, string? clientPrefix = null,
        bool codec = false, Action<RespireHybridCacheCoherenceOptions>? configure = null, ParkedSerializer? serializer = null,
        IRespireValueCodec? valueCodec = null)
    {
        var services = new ServiceCollection();
        if (client is not null) services.AddSingleton<IRespireClient>(client);
        var builder = services.AddRespireHybridCache(options =>
        {
            if (client is null) options.ClientOptions = _ => RespireOptions.Parse(fixture.ConnectionString) with { KeyPrefix = clientPrefix ?? "" };
            options.InstanceName = InstanceName;
            if (codec) options.ValueCodec = new BrotliValueCodec();
            if (valueCodec is not null) options.ValueCodec = valueCodec;
        });
        if (serializer is not null) builder.AddSerializer<TestValue>(serializer);
        if (coherent) builder.WithRespireClientSideCoherence(configure);
        return services.BuildServiceProvider();
    }

    private static string NewKey() => Guid.NewGuid().ToString("N");
    private static RespireCoherentHybridCache Coherent(ServiceProvider provider)
        => (RespireCoherentHybridCache)provider.GetRequiredService<HybridCache>();
    private static ValueTask<string> ReadAsync(HybridCache cache, string key)
        => cache.GetOrCreateAsync<string>(key, _ => throw new InvalidOperationException("Existing L2 must survive local invalidation."), LongLived);

    private static async Task PrimeAsync(HybridCache cache, string key)
    {
        await ReadAsync(cache, key);
        if (cache is RespireCoherentHybridCache coherent)
            await UntilAsync(async () => { await ReadAsync(cache, key); return coherent.ObservationCount > 0; });
    }

    private static Task UntilAsync(Func<bool> condition) => UntilAsync(() => Task.FromResult(condition()));
    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!await condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("The causal Redis/cache condition did not occur.");
            await Task.Delay(10);
        }
    }

    private sealed class TestValue(string text) { public string Text { get; } = text; }

    private sealed class CountingCodec : IRespireValueCodec
    {
        private int _encodes;
        private int _decodes;
        internal int Encodes => Volatile.Read(ref _encodes);
        internal int Decodes => Volatile.Read(ref _decodes);
        public byte[] Encode(ReadOnlySpan<byte> payload)
        {
            Interlocked.Increment(ref _encodes);
            return payload.ToArray();
        }
        public byte[] Decode(ReadOnlySpan<byte> payload)
        {
            Interlocked.Increment(ref _decodes);
            return payload.ToArray();
        }
    }

    private sealed class ParkedSerializer : IHybridCacheSerializer<TestValue>, IDisposable
    {
        internal bool Park;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new(false);
        public TestValue Deserialize(ReadOnlySequence<byte> source) => new(Encoding.UTF8.GetString(source.ToArray()));
        public void Serialize(TestValue value, IBufferWriter<byte> target)
        {
            if (Park)
            {
                Entered.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The parked serializer was not released.");
            }
            target.Write(Encoding.UTF8.GetBytes(value.Text));
        }
        public void Dispose() => Release.Dispose();
    }
}
