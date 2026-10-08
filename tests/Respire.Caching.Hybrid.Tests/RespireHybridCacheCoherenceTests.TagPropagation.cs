using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using System.Buffers.Binary;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Caching.Hybrid.Tests;

public partial class RespireHybridCacheCoherenceTests
{
    [Test]
    public async Task RegistrationSnapshotsTheValidatedChannelAndBounds()
    {
        var channel = "tags:" + NewKey();
        RespireHybridCacheCoherenceOptions? configured = null;
        await using var source = RespireClient.Create(fixture.ConnectionString);
        await using var provider = BuildProvider(true, source, configure: options =>
        {
            TagOptions(channel)(options);
            options.MaxTagsPerEntry = 1;
            configured = options;
        });
        configured!.TagInvalidationChannel = "changed:" + channel;
        configured.MaxTagsPerEntry = 2;
        var cache = provider.GetRequiredService<HybridCache>();
        await cache.GetOrCreateAsync(NewKey(), _ => ValueTask.FromResult("local"),
            new HybridCacheEntryOptions { Flags = HybridCacheEntryFlags.DisableDistributedCacheWrite }, ["one"]);
        await Assert.That((await source.Server.PubSubSubscriberCountsAsync([channel]))[0].Subscribers).IsEqualTo(1);
        await Assert.That(async () => await cache.SetAsync(NewKey(), "unwritten", LongLived, ["one", "two"]))
            .Throws<ArgumentException>();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task AnotherInstanceInvalidatesTagsWithoutDeletingValuesOrUnrelatedL1(bool multiple, bool wildcard)
    {
        var channel = "tags:" + NewKey();
        var tag = "tag:" + NewKey();
        var affected = NewKey();
        var unrelated = NewKey();
        var codec = new CountingCodec();
        var writerCodec = new CountingCodec();
        void Configure(RespireHybridCacheCoherenceOptions options)
        {
            options.TagInvalidationChannel = channel;
            options.TagInvalidationNamespace = InstanceName;
        }
        await using var writer = BuildProvider(true, configure: Configure, valueCodec: writerCodec);
        await using var reader = BuildProvider(true, configure: Configure, valueCodec: codec);
        var write = writer.GetRequiredService<HybridCache>();
        var read = reader.GetRequiredService<HybridCache>();
        await write.SetAsync(affected, "old", LongLived, wildcard ? null : [tag]);
        await write.SetAsync(unrelated, "unrelated", LongLived, ["unrelated:" + tag]);
        // HybridCache requires consistent tags on reads and writes; stored L2 tags validate
        // L2 payloads but do not replace the caller's tag set on the resulting L1 entry.
        await ReadTaggedAsync(read, affected, wildcard ? [] : [tag]);
        await ReadTaggedAsync(read, unrelated, ["unrelated:" + tag]);
        await UntilAsync(async () =>
        {
            await ReadTaggedAsync(read, affected, wildcard ? [] : [tag]);
            await ReadTaggedAsync(read, unrelated, ["unrelated:" + tag]);
            return Coherent(reader).ObservationCount == 2;
        });
        var backend = reader.GetRequiredService<IDistributedCache>();
        var payload = await backend.GetAsync(affected);
        var writes = writerCodec.Encodes;

        if (multiple) await write.RemoveByTagAsync(["absent:" + tag, tag]);
        else await write.RemoveByTagAsync(wildcard ? "*" : tag);

        var factoryCalls = 0;
        var localOnly = new HybridCacheEntryOptions
        {
            Expiration = LongLived.Expiration,
            LocalCacheExpiration = LongLived.LocalCacheExpiration,
            Flags = HybridCacheEntryFlags.DisableDistributedCacheWrite,
        };
        await UntilAsync(async () => await read.GetOrCreateAsync(affected, _ =>
        {
            Interlocked.Increment(ref factoryCalls);
            return ValueTask.FromResult("fresh");
        }, localOnly, wildcard ? null : [tag]) == "fresh");

        await Assert.That(factoryCalls).IsEqualTo(1);
        var decodes = codec.Decodes;
        if (!wildcard)
        {
            await Assert.That(await ReadTaggedAsync(read, unrelated, ["unrelated:" + tag])).IsEqualTo("unrelated");
            await Assert.That(codec.Decodes).IsEqualTo(decodes);
        }
        else
        {
            await Assert.That(await read.GetOrCreateAsync(unrelated, _ => ValueTask.FromResult("wildcard fresh"), localOnly))
                .IsEqualTo("wildcard fresh");
        }
        var after = await backend.GetAsync(affected);
        await Assert.That(after!.AsSpan().SequenceEqual(payload)).IsTrue();
        await Assert.That(codec.Encodes).IsEqualTo(0);
        await Assert.That(writerCodec.Encodes - writes).IsEqualTo(multiple ? 2 : 1);
    }

    private static ValueTask<string> ReadTaggedAsync(HybridCache cache, string key, string[] tags)
        => cache.GetOrCreateAsync<string>(key, _ => throw new InvalidOperationException("Existing L2 must supply the value."),
            LongLived, tags);

    private static ValueTask<string> FreshTaggedAsync(HybridCache cache, string key, string tag, string value)
        => cache.GetOrCreateAsync(key, _ => ValueTask.FromResult(value), new HybridCacheEntryOptions
        {
            LocalCacheExpiration = TimeSpan.FromHours(1),
            Expiration = TimeSpan.FromHours(1),
            Flags = HybridCacheEntryFlags.DisableDistributedCacheWrite,
        }, [tag]);

    private static Action<RespireHybridCacheCoherenceOptions> TagOptions(string channel)
        => options =>
        {
            options.TagInvalidationChannel = channel;
            options.TagInvalidationNamespace = InstanceName;
        };

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TagInvalidationRetiresParkedFactoriesBeforeTheirLateCompletion(bool wildcard)
    {
        var channel = "tags:" + NewKey();
        var tag = NewKey();
        var key = NewKey();
        var probe = NewKey();
        await using var writer = BuildProvider(true, configure: TagOptions(channel));
        await using var reader = BuildProvider(true, configure: TagOptions(channel));
        var write = writer.GetRequiredService<HybridCache>();
        var read = reader.GetRequiredService<HybridCache>();
        await write.SetAsync(probe, "old", LongLived, [tag]);
        await ReadTaggedAsync(read, probe, [tag]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fill = read.GetOrCreateAsync(key, async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            return "parked";
        }, new HybridCacheEntryOptions
        {
            LocalCacheExpiration = TimeSpan.FromHours(1),
            Flags = HybridCacheEntryFlags.DisableDistributedCacheWrite,
        }, [tag]).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await write.RemoveByTagAsync(wildcard ? "*" : tag);
            // The probe proves that this receiver processed the message before the second
            // request tests stampede retirement. The old producer is still parked.
            await UntilAsync(async () => await FreshTaggedAsync(read, probe, tag, "probe fresh") == "probe fresh");
            await Assert.That(await FreshTaggedAsync(read, key, tag, "new generation").AsTask().WaitAsync(TimeSpan.FromSeconds(10)))
                .IsEqualTo("new generation");
            release.TrySetResult();
            await Assert.That(await fill.WaitAsync(TimeSpan.FromSeconds(10))).IsEqualTo("parked");
            await Assert.That(await FreshTaggedAsync(read, key, tag, "unexpected factory")).IsEqualTo("new generation");
            await Assert.That(await reader.GetRequiredService<IDistributedCache>().GetAsync(key)).IsNull();
        }
        finally { release.TrySetResult(); await fill.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Test]
    public async Task ReceivedTagsRefreshIndependentlyCachedMetadataInCapacityFallbacks()
    {
        var channel = "tags:" + NewKey();
        var tag = NewKey();
        var held = NewKey();
        var overflow = NewKey();
        await using var writer = BuildProvider(true, configure: TagOptions(channel));
        await using var reader = BuildProvider(true, configure: options =>
        {
            TagOptions(channel)(options);
            options.MaxObservedKeys = 1;
        });
        var write = writer.GetRequiredService<HybridCache>();
        var read = reader.GetRequiredService<HybridCache>();
        await write.SetAsync(held, "old", LongLived, [tag]);
        await write.SetAsync(overflow, "old", LongLived, [tag]);
        await ReadTaggedAsync(read, held, [tag]);
        await ReadTaggedAsync(read, overflow, [tag]);
        await Assert.That(Coherent(reader).ObservationCount).IsEqualTo(1);
        await write.RemoveByTagAsync(tag);
        await UntilAsync(async () => await FreshTaggedAsync(read, held, tag, "held fresh") == "held fresh");
        await Assert.That(await FreshTaggedAsync(read, overflow, tag, "overflow fresh")).IsEqualTo("overflow fresh");
        await Assert.That(Coherent(reader).ObservationCount).IsEqualTo(1);
    }

    [Test]
    public async Task ReceivedTagsPreserveTheExactSharedMarkerAndDoNotEcho()
    {
        var channel = "tags:" + NewKey();
        var tag = NewKey();
        var key = NewKey();
        var codec = new CountingCodec();
        await using var monitor = RespireClient.Create(fixture.ConnectionString);
        await using var subscription = await monitor.SubscribeAsync(channel);
        await using var messages = subscription.GetAsyncEnumerator();
        await using var writer = BuildProvider(true, configure: TagOptions(channel));
        await using var reader = BuildProvider(true, configure: TagOptions(channel), valueCodec: codec);
        var write = writer.GetRequiredService<HybridCache>();
        var read = reader.GetRequiredService<HybridCache>();
        await write.SetAsync(key, "old", LongLived, [tag]);
        await ReadTaggedAsync(read, key, [tag]);
        await write.RemoveByTagAsync(tag);
        var backend = writer.GetRequiredService<IDistributedCache>();
        // Inspect the pinned HybridCache 10.10.0 marker contract only in this integration
        // control; production code neither parses nor manufactures HybridCache markers.
        var original = await backend.GetAsync("__MSFT_HCT__" + tag);
        await UntilAsync(async () => await FreshTaggedAsync(read, key, tag, "fresh") == "fresh");
        await Assert.That(await messages.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10))).IsTrue();
        var protocol = new TagInvalidationMessage(InstanceName, 4096);
        await Assert.That(protocol.TryDecode(messages.Current.Payload.Span, out _, out var receivedTag, out var timestamp)).IsTrue();
        await Assert.That(receivedTag).IsEqualTo(tag);
        await Assert.That(timestamp.UtcTicks).IsEqualTo(BinaryPrimitives.ReadInt64LittleEndian(original));
        var after = await backend.GetAsync("__MSFT_HCT__" + tag);
        await Assert.That(after!.AsSpan().SequenceEqual(original)).IsTrue();
        await Assert.That(codec.Encodes).IsEqualTo(0);
        await monitor.PublishAsync(channel, "end of original message");
        await Assert.That(await messages.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10))).IsTrue();
        await Assert.That(messages.Current.Text).IsEqualTo("end of original message");
    }

    [Test]
    public async Task ForeignNamespacesAndOlderMessagesCannotInvalidateOrRegressLocalTags()
    {
        var channel = "tags:" + NewKey();
        var tag = NewKey();
        var probeTag = NewKey();
        var key = NewKey();
        var probe = NewKey();
        await using var writer = BuildProvider(false);
        await using var reader = BuildProvider(true, configure: TagOptions(channel));
        await using var publisher = RespireClient.Create(fixture.ConnectionString);
        var write = writer.GetRequiredService<HybridCache>();
        var read = reader.GetRequiredService<HybridCache>();
        await write.SetAsync(key, "old", LongLived, [tag]);
        await write.SetAsync(probe, "probe", LongLived, [probeTag]);
        await ReadTaggedAsync(read, key, [tag]);
        await ReadTaggedAsync(read, probe, [probeTag]);
        var protocol = new TagInvalidationMessage(InstanceName, 4096);
        var sender = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await publisher.PublishAsync(channel, new TagInvalidationMessage("foreign", 4096).Encode(sender, tag, now));
        await publisher.PublishAsync(channel, protocol.Encode(sender, probeTag, now));
        await UntilAsync(async () => await FreshTaggedAsync(read, probe, probeTag, "probe fresh") == "probe fresh");
        await Assert.That(await ReadTaggedAsync(read, key, [tag])).IsEqualTo("old");
        await publisher.PublishAsync(channel, protocol.Encode(sender, tag, DateTimeOffset.UtcNow));
        await publisher.PublishAsync(channel, protocol.Encode(sender, tag, now.AddDays(-1)));
        // A wildcard source-cache clear retires the observation so its next context must
        // replay remembered history, rather than relying only on the previous context.
        await UntilAsync(async () => await FreshTaggedAsync(read, key, tag, "fresh") == "fresh");
        await publisher.PublishAsync(channel, protocol.Encode(sender, probeTag, DateTimeOffset.UtcNow));
        await UntilAsync(async () => await FreshTaggedAsync(read, probe, probeTag, "second probe") == "second probe");
        Coherent(reader).TrackingClient.ClientSideCache!.Clear();
        await UntilAsync(() => Coherent(reader).ObservationCount == 0);
        await Assert.That(await FreshTaggedAsync(read, key, tag, "fresh after retirement")).IsEqualTo("fresh after retirement");
        await Assert.That(await reader.GetRequiredService<IDistributedCache>().GetAsync("__MSFT_HCT__" + tag)).IsNull();
    }

    [Test]
    public async Task OversizedMessagesRetireL1AndReloadAuthoritativeTagMetadata()
    {
        var channel = "tags:" + NewKey();
        var tag = NewKey();
        var key = NewKey();
        await using var writer = BuildProvider(false);
        await using var reader = BuildProvider(true, configure: options =>
        {
            TagOptions(channel)(options);
            options.MaxTagInvalidationMessageBytes = 128;
        });
        await using var publisher = RespireClient.Create(fixture.ConnectionString);
        var write = writer.GetRequiredService<HybridCache>();
        var read = reader.GetRequiredService<HybridCache>();
        await write.SetAsync(key, "old", LongLived, [tag]);
        await ReadTaggedAsync(read, key, [tag]);
        // This L2-only writer deliberately omits publication, modelling an unreplayable gap.
        await write.RemoveByTagAsync(tag);
        await Assert.That(await ReadTaggedAsync(read, key, [tag])).IsEqualTo("old");
        await publisher.PublishAsync(channel, new byte[129]);
        await UntilAsync(() => Coherent(reader).ObservationCount == 0);
        await Assert.That(await FreshTaggedAsync(read, key, tag, "fresh after gap")).IsEqualTo("fresh after gap");
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task ProviderDisposalUnsubscribesWithoutDisposingTheRegisteredClient(bool synchronous, bool prefixedView)
    {
        var channel = "tags:" + NewKey();
        await using var root = RespireClient.Create(RespireOptions.Parse(fixture.ConnectionString) with { PubSubPrefix = "source-only:" });
        var source = prefixedView ? (RespireClient)root.WithPubSubPrefix("view-only:") : root;
        var provider = BuildProvider(true, source, configure: TagOptions(channel));
        try
        {
            await provider.GetRequiredService<HybridCache>().GetOrCreateAsync(NewKey(), _ => ValueTask.FromResult("value"),
                new HybridCacheEntryOptions { Flags = HybridCacheEntryFlags.DisableDistributedCacheWrite });
            await Assert.That((await source.Server.PubSubSubscriberCountsAsync([channel]))[0].Subscribers).IsEqualTo(1);
            if (synchronous) provider.Dispose();
            else await provider.DisposeAsync();
            await Assert.That((await source.Server.PubSubSubscriberCountsAsync([channel]))[0].Subscribers).IsEqualTo(0);
            using var pong = await source.ExecuteAsync("PING");
            await Assert.That(pong.AsString()).IsEqualTo("PONG");
        }
        finally { await provider.DisposeAsync(); }
    }

    [Test]
    public async Task SubscriberReconnectDropsCachedTagMetadataAcrossAMissedInvalidation()
    {
        var channel = "tags:" + NewKey();
        var tag = NewKey();
        var key = NewKey();
        var username = "tag-subscriber-" + NewKey();
        const string password = "ephemeral-tag-test-password";
        await using var admin = RespireClient.Create(fixture.ConnectionString);
        using (var created = await admin.ExecuteAsync("ACL", "SETUSER", username, "on", ">" + password, "~*", "&*", "+@all")) { }
        try
        {
            await using var source = RespireClient.Create(RespireOptions.Parse(fixture.ConnectionString) with
            {
                Username = username,
                Password = password,
                ClientName = username,
                ReconnectPolicy = new() { InitialDelay = TimeSpan.FromMilliseconds(10), MaxDelay = TimeSpan.FromMilliseconds(10), JitterRatio = 0 },
            });
            await using var writer = BuildProvider(false);
            await using var reader = BuildProvider(true, source, configure: TagOptions(channel));
            var write = writer.GetRequiredService<HybridCache>();
            var read = reader.GetRequiredService<HybridCache>();
            await write.SetAsync(key, "old", LongLived, [tag]);
            await ReadTaggedAsync(read, key, [tag]);
            var subscription = (await admin.Server.ClientsAsync()).Single(client => client.Name == username && client.Flags.Contains('P'));
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Coherent(reader).TrackingClient.ConnectionStateChanged += change =>
            {
                if (change.ReconnectSource != RespireReconnectSource.PubSub) return;
                if ((change.SourceState ?? change.State) == RespireConnectionState.Connected) connected.TrySetResult();
                else disconnected.TrySetResult();
            };
            // Revoke only this owned user's SUBSCRIBE permission so reconnect cannot close
            // the publication gap before the shared marker is changed.
            using (var denied = await admin.ExecuteAsync("ACL", "SETUSER", username, "-subscribe")) { }
            using (var killed = await admin.ExecuteAsync("CLIENT", "KILL", "ID", subscription.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))) { }
            await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await write.RemoveByTagAsync(tag);
            await Assert.That((await admin.Server.PubSubSubscriberCountsAsync([channel]))[0].Subscribers).IsEqualTo(0);
            await Assert.That(await FreshTaggedAsync(read, key, tag, "fresh during gap")).IsEqualTo("fresh during gap");
            await Assert.That(Coherent(reader).ObservationCount).IsEqualTo(0);
            using (var restored = await admin.ExecuteAsync("ACL", "SETUSER", username, "+subscribe")) { }
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(await FreshTaggedAsync(read, key, tag, "fresh after reconnect")).IsEqualTo("fresh after reconnect");
            await Assert.That(Coherent(reader).ObservationCount).IsEqualTo(1);
            using var pong = await source.ExecuteAsync("PING");
            await Assert.That(pong.AsString()).IsEqualTo("PONG");
        }
        finally { using var removed = await admin.ExecuteAsync("ACL", "DELUSER", username); }
    }

    [Test]
    public async Task ExhaustedRemoteTagHistoryKeepsFallbackMetadataFresh()
    {
        var channel = "tags:" + NewKey();
        var tag = NewKey();
        var key = NewKey();
        await using var writer = BuildProvider(true, configure: TagOptions(channel));
        await using var reader = BuildProvider(true, configure: options =>
        {
            TagOptions(channel)(options);
            options.MaxRememberedTagInvalidations = 1;
        });
        var write = writer.GetRequiredService<HybridCache>();
        var read = reader.GetRequiredService<HybridCache>();
        await write.SetAsync(key, "old", LongLived, [tag]);
        await ReadTaggedAsync(read, key, [tag]);
        await write.RemoveByTagAsync("first:" + tag);
        await write.RemoveByTagAsync("second:" + tag);
        await UntilAsync(() => Coherent(reader).ObservationCount == 0);
        await Assert.That(await ReadTaggedAsync(read, key, [tag])).IsEqualTo("old");
        await write.RemoveByTagAsync(tag);
        await Assert.That(await FreshTaggedAsync(read, key, tag, "fresh after exhaustion")).IsEqualTo("fresh after exhaustion");
        await Assert.That(Coherent(reader).ObservationCount).IsEqualTo(0);
    }

    [Test]
    public async Task EntryAndHistoryBoundsRejectOversizedTagsBeforeWriting()
    {
        var channel = "tags:" + NewKey();
        var key = NewKey();
        var codec = new CountingCodec();
        await using var reader = BuildProvider(true, configure: options =>
        {
            TagOptions(channel)(options);
            options.MaxTagInvalidationMessageBytes = 128;
            options.MaxTagsPerEntry = 2;
        }, valueCodec: codec);
        var cache = reader.GetRequiredService<HybridCache>();
        await Assert.That(async () => await cache.SetAsync(key, "unwritten", LongLived, ["one", "two", "three"]))
            .Throws<ArgumentException>();
        await Assert.That(async () => await cache.RemoveByTagAsync(new string('x', 128))).Throws<ArgumentException>();
        await Assert.That(codec.Encodes).IsEqualTo(0);
        var calls = 0;
        async ValueTask<string> Fill(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            await Task.Yield();
            return "local";
        }
        var localOnly = new HybridCacheEntryOptions { Flags = HybridCacheEntryFlags.DisableDistributedCacheWrite };
        await cache.GetOrCreateAsync(key, Fill, localOnly, ["one"]);
        await cache.GetOrCreateAsync(key, Fill, localOnly, ["two"]);
        await Assert.That(calls).IsEqualTo(1);
        await cache.GetOrCreateAsync(key, Fill, localOnly, ["three"]);
        await Assert.That(calls).IsEqualTo(2);
        await Assert.That(Coherent(reader).ObservationCount).IsEqualTo(1);
    }

    [Test]
    public async Task BufferOverflowFencesL1BeforeTheBlockedConsumerCanResume()
    {
        var channel = "tags:" + NewKey();
        var tag = NewKey();
        var key = NewKey();
        using var memory = new ProbingMemoryCache();
        await using var writer = BuildProvider(false);
        await using var reader = BuildProvider(true, configure: options =>
        {
            TagOptions(channel)(options);
            options.TagInvalidationBufferSize = 1;
        }, memory: memory);
        await using var publisher = RespireClient.Create(fixture.ConnectionString);
        var write = writer.GetRequiredService<HybridCache>();
        var read = reader.GetRequiredService<HybridCache>();
        await write.SetAsync(key, "old", LongLived, [tag]);
        await ReadTaggedAsync(read, key, [tag]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var removes = 0;
        memory.OnRemove = () =>
        {
            if (Interlocked.Increment(ref removes) != 1) return;
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        };
        var protocol = new TagInvalidationMessage(InstanceName, 4096);
        var sender = Guid.NewGuid();
        try
        {
            await publisher.PublishAsync(channel, protocol.Encode(sender, "*", DateTimeOffset.UtcNow.AddDays(-1)));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // Refill while the first retirement's cleanup blocks the consumer. The old
            // wildcard timestamp does not expire the stored value.
            await Assert.That(await ReadTaggedAsync(read, key, [tag])).IsEqualTo("old");
            await write.RemoveByTagAsync(tag); // authoritative marker changes without a publication
            for (var index = 0; index < 4; index++)
                await publisher.PublishAsync(channel, protocol.Encode(sender, "buffer:" + index, DateTimeOffset.UtcNow));
            // The overflow callback must fence L1 on the receive path. The consumer cannot
            // yet see a stream gap or process any queued tag message.
            await UntilAsync(async () => await FreshTaggedAsync(read, key, tag, "fresh before resume") == "fresh before resume");
            await Assert.That(release.Task.IsCompleted).IsFalse();
        }
        finally { release.TrySetResult(); memory.OnRemove = null; }
    }

    [Test]
    public async Task DeniedSubscriberStartupUsesFreshL2MetadataAndDisposesCleanly()
    {
        var channel = "tags:" + NewKey();
        var tag = NewKey();
        var key = NewKey();
        var username = "tag-denied-" + NewKey();
        const string password = "ephemeral-tag-test-password";
        await using var admin = RespireClient.Create(fixture.ConnectionString);
        using (var created = await admin.ExecuteAsync("ACL", "SETUSER", username, "on", ">" + password,
            "~*", "&*", "+@all", "-subscribe")) { }
        try
        {
            await using var source = RespireClient.Create(RespireOptions.Parse(fixture.ConnectionString) with
            { Username = username, Password = password });
            await using var writer = BuildProvider(false);
            await using var reader = BuildProvider(true, source, configure: TagOptions(channel));
            var write = writer.GetRequiredService<HybridCache>();
            var read = reader.GetRequiredService<HybridCache>();
            await write.SetAsync(key, "old", LongLived, [tag]);
            await Assert.That(await ReadTaggedAsync(read, key, [tag])).IsEqualTo("old");
            await write.RemoveByTagAsync(tag);
            await Assert.That(await FreshTaggedAsync(read, key, tag, "fresh after failed startup")).IsEqualTo("fresh after failed startup");
            await Assert.That(Coherent(reader).ObservationCount).IsEqualTo(0);
            await reader.DisposeAsync();
            using var pong = await source.ExecuteAsync("PING");
            await Assert.That(pong.AsString()).IsEqualTo("PONG");
        }
        finally { using var removed = await admin.ExecuteAsync("ACL", "DELUSER", username); }
    }
}
