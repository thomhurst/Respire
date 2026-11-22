using System.Buffers;
using System.IO.Pipelines;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Respire.OutputCaching;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Caching.Tests;

/// <summary>Ported output-store conformance scenarios plus bidirectional Microsoft-store interoperability.</summary>
[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class OutputCacheTests(RedisTestContainer fixture)
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task StoresRawValuesWithPrefixTagsAndTimeout(bool segmented, bool withTags)
    {
        await using var client = await RespireClient.ConnectAsync(fixture.IsolatedConnectionString);
        var prefix = NewPrefix();
        var store = new RespireOutputCacheStore(client, new() { InstanceName = prefix });
        string[] tags = withTags ? ["one", "two"] : [];
        TimeSpan lifetime = TimeSpan.FromMinutes(5);
        long earliestDeadline = (DateTimeOffset.UtcNow + lifetime).ToUnixTimeMilliseconds();

        if (segmented)
        {
            await store.SetAsync("key", Sequence(), tags, lifetime);
        }
        else
        {
            await store.SetAsync("key", new byte[] { 1, 2, 3, 4 }, tags, lifetime);
        }

        long latestDeadline = (DateTimeOffset.UtcNow + lifetime).ToUnixTimeMilliseconds();
        await Assert.That((await client.GetBytesAsync(prefix + "__MSOCV_key"))!.SequenceEqual(new byte[] { 1, 2, 3, 4 })).IsTrue();
        using RespireResult ttl = await client.ExecuteAsync("PTTL", [prefix + "__MSOCV_key"]);
        long remaining = ttl.AsInteger();
        await Assert.That(remaining).IsGreaterThan(0L);

        if (!withTags)
        {
            await Assert.That(remaining).IsLessThanOrEqualTo((long)lifetime.TotalMilliseconds);
            return;
        }

        // The client calculates tagged deadlines. Redis measures PTTL against its own clock.
        using RespireResult expiry = await client.ExecuteAsync("PEXPIRETIME", [prefix + "__MSOCV_key"]);
        long deadline = expiry.AsInteger();
        await Assert.That(deadline).IsGreaterThanOrEqualTo(earliestDeadline);
        await Assert.That(deadline).IsLessThanOrEqualTo(latestDeadline);

        foreach (string tag in tags)
        {
            using var members = await client.ExecuteAsync("ZRANGE", [prefix + "__MSOCT_" + tag, 0, -1]);
            await Assert.That(members[0].AsString()).IsEqualTo("key");
            using var score = await client.ExecuteAsync("ZSCORE", [prefix + "__MSOCT_" + tag, "key"]);
            await Assert.That(score.AsDouble()).IsEqualTo((double)deadline);
        }
    }

    [Test]
    public async Task MissingEmptyAndSegmentedValuesUseBufferContract()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.IsolatedConnectionString);
        var store = new RespireOutputCacheStore(client, new() { InstanceName = NewPrefix() });
        await Assert.That(await store.GetAsync("missing")).IsNull();
        using var output = new MemoryStream();
        var destination = PipeWriter.Create(output, new(leaveOpen: true));

        try
        {
            await Assert.That(await store.TryGetAsync("missing", destination)).IsFalse();
            await Assert.That(output.Length).IsEqualTo(0L);
            await store.SetAsync("empty", Array.Empty<byte>(), null, TimeSpan.FromMinutes(1));
            await Assert.That(await store.TryGetAsync("empty", destination)).IsTrue();
            await Assert.That((await store.GetAsync("empty"))!.Length).IsEqualTo(0);
            await store.SetAsync("segments", Sequence(), ReadOnlyMemory<string>.Empty, TimeSpan.FromMinutes(1));
            await Assert.That(await store.TryGetAsync("segments", destination)).IsTrue();
            await Assert.That(output.ToArray().SequenceEqual(new byte[] { 1, 2, 3, 4 })).IsTrue();
        }
        finally { await destination.CompleteAsync(); }
    }

    [Test]
    public async Task MicrosoftAndRespireReadAndEvictEachOthersEntries()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.IsolatedConnectionString);
        var prefix = NewPrefix();
        var store = new RespireOutputCacheStore(client, new() { InstanceName = prefix });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddStackExchangeRedisOutputCache(options =>
        {
            options.Configuration = fixture.IsolatedStackExchangeConnectionString;
            options.InstanceName = prefix;
        });
        await using var provider = services.BuildServiceProvider();
        var microsoft = provider.GetRequiredService<IOutputCacheStore>();
        await microsoft.SetAsync("microsoft", [10, 20], ["shared"], TimeSpan.FromMinutes(5), default);
        // Microsoft sends tag updates fire-and-forget. A read on that connection is a protocol barrier.
        await Assert.That(await microsoft.GetAsync("microsoft", default)).IsNotNull();
        await Assert.That((await store.GetAsync("microsoft"))!.SequenceEqual(new byte[] { 10, 20 })).IsTrue();
        await store.EvictByTagAsync("shared");
        await Assert.That(await microsoft.GetAsync("microsoft", default)).IsNull();
        await store.SetAsync("respire", new byte[] { 30, 40 }, ["shared"], TimeSpan.FromMinutes(5));
        await Assert.That((await microsoft.GetAsync("respire", default))!.SequenceEqual(new byte[] { 30, 40 })).IsTrue();
        await microsoft.EvictByTagAsync("shared", default);
        await Assert.That(await microsoft.GetAsync("respire", default)).IsNull();
        await Assert.That(await store.GetAsync("respire")).IsNull();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task OverwriteRetainsOldTagMembershipLikeMicrosoft(bool microsoftWrites, bool microsoftEvicts)
    {
        await using var client = await RespireClient.ConnectAsync(fixture.IsolatedConnectionString);
        var prefix = NewPrefix();
        var respire = new RespireOutputCacheStore(client, new() { InstanceName = prefix });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddStackExchangeRedisOutputCache(options =>
        {
            options.Configuration = fixture.IsolatedStackExchangeConnectionString;
            options.InstanceName = prefix;
        });
        await using var provider = services.BuildServiceProvider();
        var microsoft = provider.GetRequiredService<IOutputCacheStore>();
        IOutputCacheStore writer = microsoftWrites ? microsoft : respire;
        IOutputCacheStore evictor = microsoftEvicts ? microsoft : respire;
        await writer.SetAsync("key", [1], ["old"], TimeSpan.FromMinutes(5), default);
        await writer.SetAsync("key", [2], ["new"], TimeSpan.FromMinutes(5), default);
        // This awaited read orders Microsoft's fire-and-forget tag writes before cross-client eviction.
        await Assert.That((await writer.GetAsync("key", default))!.Single()).IsEqualTo((byte)2);
        await evictor.EvictByTagAsync("old", default);
        await Assert.That(await evictor.GetAsync("key", default)).IsNull();
        await Assert.That(await writer.GetAsync("key", default)).IsNull();
    }

    [Test]
    public async Task MasterScoreNeverShrinksAndEvictionLeavesUnrelatedTags()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.IsolatedConnectionString);
        var prefix = NewPrefix();
        var store = new RespireOutputCacheStore(client, new() { InstanceName = prefix });
        await store.SetAsync("long", new byte[] { 1 }, ["shared"], TimeSpan.FromHours(1));
        using var original = await client.ExecuteAsync("ZSCORE", [prefix + "__MSOCT", "shared"]);
        await store.SetAsync("short", new byte[] { 2 }, ["shared"], TimeSpan.FromMinutes(1));
        using var current = await client.ExecuteAsync("ZSCORE", [prefix + "__MSOCT", "shared"]);
        await Assert.That(current.AsDouble()).IsEqualTo(original.AsDouble());
        await store.SetAsync("other", new byte[] { 3 }, ["unrelated"], TimeSpan.FromMinutes(1));
        await store.EvictByTagAsync("shared");
        await Assert.That(await store.GetAsync("long")).IsNull();
        await Assert.That(await store.GetAsync("short")).IsNull();
        await Assert.That(await store.GetAsync("other")).IsNotNull();
        await store.EvictByTagAsync("absent");
    }

    [Test]
    public async Task EvictionSpansSeveralChunksWithoutLosingTheRemainder()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.IsolatedConnectionString);
        var prefix = NewPrefix();
        var store = new RespireOutputCacheStore(client, new() { InstanceName = prefix });
        var keys = new RespireValue[601];

        using (var batch = client.CreateBatch())
        {
            for (var index = 0; index < keys.Length; index++)
            {
                var member = $"key:{index}";
                keys[index] = prefix + "__MSOCV_" + member;
                _ = batch.Set(prefix + "__MSOCV_" + member, "value", TimeSpan.FromMinutes(5));
                _ = batch.SortedSets.Add(prefix + "__MSOCT_shared", (RespireValue)member,
                    DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds());
            }

            await batch.ExecuteAsync();
        }

        await store.SetAsync("unrelated", new byte[] { 1 }, ["other"], TimeSpan.FromMinutes(5));
        await store.EvictByTagAsync("shared");
        using var remaining = await client.ExecuteAsync("EXISTS", keys);
        await Assert.That(remaining.AsInteger()).IsEqualTo(0L);
        await Assert.That(await client.SortedSets.CountAsync(prefix + "__MSOCT_shared")).IsEqualTo(0L);
        await Assert.That(await store.GetAsync("unrelated")).IsNotNull();
    }

    [Test]
    [Arguments(RespireOutputCacheTaggingMode.MicrosoftCompatible)]
    [Arguments(RespireOutputCacheTaggingMode.GenerationAware)]
    public async Task EvictionFindsTagMembersMissingOnReplica(RespireOutputCacheTaggingMode taggingMode)
    {
        await using FakeRespServer replica = StaleTagReplica();
        await using RespireClient client = await ConnectWithReplicaAsync(replica);
        var options = new RespireOutputCacheOptions { InstanceName = $"{{{NewPrefix()}}}:", TaggingMode = taggingMode };
        var store = new RespireOutputCacheStore(client, options);
        var primaryStore = new RespireOutputCacheStore(client.WithReadFrom(RespireReadFrom.Primary), options);

        await WarmReplicaScanAsync(client, replica);
        await store.SetAsync("entry", [1], ["tag"], TimeSpan.FromMinutes(1));
        await Assert.That(await primaryStore.GetAsync("entry")).IsNotNull();

        await store.EvictByTagAsync("tag");

        await Assert.That(await primaryStore.GetAsync("entry")).IsNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CleanupHonorsMicrosoftLockAndRemovesOnlyExpiredReferences(bool readFromReplica)
    {
        await using FakeRespServer? replica = readFromReplica ? StaleTagReplica() : null;
        await using RespireClient client = replica is null
            ? await RespireClient.ConnectAsync(fixture.IsolatedConnectionString)
            : await ConnectWithReplicaAsync(replica);
        IRespireClient primary = client.WithReadFrom(RespireReadFrom.Primary);
        var prefix = NewPrefix();
        var store = new RespireOutputCacheStore(client, new() { InstanceName = prefix });
        var primaryStore = new RespireOutputCacheStore(primary, new() { InstanceName = prefix });
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (replica is not null)
        {
            await WarmReplicaScanAsync(client, replica);
        }

        using (var batch = client.CreateBatch())
        {
            _ = batch.SortedSets.Add(prefix + "__MSOCT", (RespireValue)"expired", now - 1);
            _ = batch.SortedSets.Add(prefix + "__MSOCT_expired", (RespireValue)"old", now - 1);
            // Cross the renewal threshold with a cancellable token, exercising CLIENT permissions.
            for (var index = 0; index < 250; index++)
            {
                _ = batch.SortedSets.Add(prefix + "__MSOCT", (RespireValue)$"expired:{index}", now - 1);
                _ = batch.SortedSets.Add(prefix + $"__MSOCT_expired:{index}", (RespireValue)"old", now - 1);
            }

            await batch.ExecuteAsync();
        }

        await store.SetAsync("live", new byte[] { 1 }, ["live"], TimeSpan.FromHours(1));
        await client.SetAsync(prefix + "__MSOCTGC", (RespireValue)"Microsoft GC", TimeSpan.FromMinutes(5));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await store.CollectExpiredTagsAsync(cancellation.Token);
        await Assert.That(await primary.ExistsAsync(prefix + "__MSOCT_expired")).IsTrue();
        await client.DeleteAsync([prefix + "__MSOCTGC"]);
        await store.CollectExpiredTagsAsync(cancellation.Token);
        await Assert.That(await primary.ExistsAsync(prefix + "__MSOCT_expired")).IsFalse();
        await Assert.That(await primary.SortedSets.CountAsync(prefix + "__MSOCT")).IsEqualTo(1L);
        await Assert.That(await primaryStore.GetAsync("live")).IsNotNull();
        await Assert.That(await primary.ExistsAsync(prefix + "__MSOCTGC")).IsFalse();
    }

    [Test]
    public async Task RegistrationUsesBufferStoreAndOneCleanupServiceWithoutOwningClient()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.IsolatedConnectionString);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IRespireClient>(client);
        services.AddRespireOutputCache(options => options.InstanceName = NewPrefix());
        services.AddRespireOutputCache();

        await using (var provider = services.BuildServiceProvider())
        {
            await Assert.That(provider.GetRequiredService<IOutputCacheStore>() is IOutputCacheBufferStore).IsTrue();
            await Assert.That(provider.GetServices<IHostedService>().Count()).IsEqualTo(1);
        }

        await client.SetAsync(NewPrefix(), (RespireValue)"still connected");
    }

    [Test]
    public async Task PreCanceledAndInvalidWritesDoNotStoreValues()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.IsolatedConnectionString);
        var store = new RespireOutputCacheStore(client, new() { InstanceName = NewPrefix() });
        await Assert.That(async () => await store.SetAsync("key", new byte[] { 1 }, null,
            TimeSpan.FromMinutes(1), new CancellationToken(true))).Throws<OperationCanceledException>();
        await Assert.That(async () => await store.SetAsync("key", new byte[] { 1 }, null, TimeSpan.Zero))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(await store.GetAsync("key")).IsNull();
    }

    private ValueTask<RespireClient> ConnectWithReplicaAsync(FakeRespServer replica)
    {
        RespireOptions options = RespireOptions.Parse(fixture.IsolatedConnectionString) with
        {
            Connections = 1,
            Protocol = RespireProtocol.Resp2,
            ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
            ReadFrom = RespireReadFrom.ReplicaPreferred,
        };
        return RespireClient.ConnectAsync(options);
    }

    private static FakeRespServer StaleTagReplica()
    {
        return new(8)
        {
            ReplyOverride = (_, command) => command switch
            {
                "ROLE" => "*5\r\n$5\r\nslave\r\n$9\r\n127.0.0.1\r\n:6379\r\n$9\r\nconnected\r\n:0\r\n"u8.ToArray(),
                _ when command.StartsWith("ZSCAN ", StringComparison.Ordinal) => "*2\r\n$1\r\n0\r\n*0\r\n"u8.ToArray(),
                _ => FakeRespServer.OkReply,
            },
        };
    }

    private static async Task WarmReplicaScanAsync(RespireClient client, FakeRespServer replica)
    {
        await using IAsyncEnumerator<RespireSortedSetEntry> scan = client.SortedSets.ScanAsync("warm").GetAsyncEnumerator();
        await Assert.That(await scan.MoveNextAsync()).IsFalse();
        await Assert.That(replica.ReceivedCommands.Any(command => command.StartsWith("ZSCAN warm ", StringComparison.Ordinal)))
            .IsTrue();
    }

    private static string NewPrefix() => $"output:{Guid.NewGuid():N}:";

    private static ReadOnlySequence<byte> Sequence()
    {
        var first = new Segment(new byte[] { 1, 2 });
        var last = first.Append(new byte[] { 3, 4 });
        return new(first, 0, last, last.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
