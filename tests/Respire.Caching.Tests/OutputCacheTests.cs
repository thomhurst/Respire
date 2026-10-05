using System.Buffers;
using System.IO.Pipelines;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Respire.OutputCaching;
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
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var prefix = NewPrefix();
        var store = new RespireOutputCacheStore(client, new() { InstanceName = prefix });
        string[] tags = withTags ? ["one", "two"] : [];
        if (segmented)
            await store.SetAsync("key", Sequence(), tags, TimeSpan.FromMinutes(5));
        else
            await store.SetAsync("key", new byte[] { 1, 2, 3, 4 }, tags, TimeSpan.FromMinutes(5));

        await Assert.That((await client.GetBytesAsync(prefix + "__MSOCV_key"))!.SequenceEqual(new byte[] { 1, 2, 3, 4 })).IsTrue();
        using var ttl = await client.ExecuteAsync("PTTL", [prefix + "__MSOCV_key"]);
        await Assert.That(ttl.AsInteger() > 0 && ttl.AsInteger() <= 300_000).IsTrue();
        foreach (var tag in tags)
        {
            using var members = await client.ExecuteAsync("ZRANGE", [prefix + "__MSOCT_" + tag, 0, -1]);
            await Assert.That(members[0].AsString()).IsEqualTo("key");
        }
    }

    [Test]
    public async Task MissingEmptyAndSegmentedValuesUseBufferContract()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
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
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var prefix = NewPrefix();
        var store = new RespireOutputCacheStore(client, new() { InstanceName = prefix });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddStackExchangeRedisOutputCache(options =>
        {
            options.Configuration = fixture.StackExchangeConnectionString;
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
    public async Task MasterScoreNeverShrinksAndEvictionLeavesUnrelatedTags()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
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
    public async Task CleanupHonorsMicrosoftLockAndRemovesOnlyExpiredReferences()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var prefix = NewPrefix();
        var store = new RespireOutputCacheStore(client, new() { InstanceName = prefix });
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await client.SortedSets.AddAsync(prefix + "__MSOCT", (RespireValue)"expired", now - 1);
        await client.SortedSets.AddAsync(prefix + "__MSOCT_expired", (RespireValue)"old", now - 1);
        await store.SetAsync("live", new byte[] { 1 }, ["live"], TimeSpan.FromHours(1));
        await client.SetAsync(prefix + "__MSOCTGC", (RespireValue)"Microsoft GC", TimeSpan.FromMinutes(5));
        await store.CollectExpiredTagsAsync();
        await Assert.That(await client.ExistsAsync(prefix + "__MSOCT_expired")).IsTrue();
        await client.DeleteAsync([prefix + "__MSOCTGC"]);
        await store.CollectExpiredTagsAsync();
        await Assert.That(await client.ExistsAsync(prefix + "__MSOCT_expired")).IsFalse();
        await Assert.That(await client.SortedSets.CountAsync(prefix + "__MSOCT")).IsEqualTo(1L);
        await Assert.That(await store.GetAsync("live")).IsNotNull();
        await Assert.That(await client.ExistsAsync(prefix + "__MSOCTGC")).IsFalse();
    }

    [Test]
    public async Task RegistrationUsesBufferStoreAndOneCleanupServiceWithoutOwningClient()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
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
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var store = new RespireOutputCacheStore(client, new() { InstanceName = NewPrefix() });
        await Assert.That(async () => await store.SetAsync("key", new byte[] { 1 }, null,
            TimeSpan.FromMinutes(1), new CancellationToken(true))).Throws<OperationCanceledException>();
        await Assert.That(async () => await store.SetAsync("key", new byte[] { 1 }, null, TimeSpan.Zero))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(await store.GetAsync("key")).IsNull();
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
