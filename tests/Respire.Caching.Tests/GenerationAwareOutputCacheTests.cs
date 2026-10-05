using System.Buffers;
using System.IO.Pipelines;
using System.Reflection;
using Respire.IntegrationTests;
using Respire.OutputCaching;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Caching.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class GenerationAwareOutputCacheTests(RedisTestContainer fixture)
{
    [Test]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task CorruptTagMembersAreRemovedWithoutBlockingValidEviction(int protocol, bool mixedPage)
    {
        await using var client = await ConnectAsync(protocol);
        var options = Options();
        var store = new RespireOutputCacheStore(client, options);
        var tagKey = options.InstanceName + "__RPOCT2_corrupt";
        await store.SetAsync("retained", [9], ["other"], TimeSpan.FromMinutes(1));
        using (var added = await client.ExecuteAsync("ZADD", [tagKey, "1", "bad"])) { }
        if (mixedPage)
        {
            await store.SetAsync("evicted", [1], ["corrupt"], TimeSpan.FromMinutes(1));
            await store.SetAsync("replacement", [2], ["corrupt"], TimeSpan.FromMinutes(1));
            await store.SetAsync("replacement", [3], ["new"], TimeSpan.FromMinutes(1));
            using var added = await client.ExecuteAsync("ZADD", [tagKey, "1",
                new string('x', 32) + ":retained", "1", new string('0', 32) + "/retained"]);
        }
        await store.EvictByTagAsync("corrupt");
        using (var count = await client.ExecuteAsync("ZCARD", [tagKey]))
            await Assert.That(count.AsInteger()).IsEqualTo(0);
        await AssertPayload(store, "retained", 9);
        if (mixedPage)
        {
            await Assert.That(await store.GetAsync("evicted")).IsNull();
            await AssertPayload(store, "replacement", 3);
            await store.EvictByTagAsync("new");
            await Assert.That(await store.GetAsync("replacement")).IsNull();
        }
        await store.EvictByTagAsync("corrupt");
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ObsoleteTagCannotDeleteReplacement(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var store = new RespireOutputCacheStore(client, Options());
        await store.SetAsync("key:雪", [1], ["old"], TimeSpan.FromMinutes(1));
        await store.SetAsync("key:雪", [2], ["new"], TimeSpan.FromMinutes(1));
        await store.EvictByTagAsync("old");
        await AssertPayload(store, "key:雪", 2);
        await store.EvictByTagAsync("new");
        await Assert.That(await store.GetAsync("key:雪")).IsNull();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ExpiredKeyReuseIsSafeBeforeTagCleanup(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var store = new RespireOutputCacheStore(client, Options());
        await store.SetAsync("key", [1], ["old"], TimeSpan.FromSeconds(1));
        await AssertPayload(store, "key", 1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await store.GetAsync("key", timeout.Token) is not null)
            await Task.Delay(10, timeout.Token);
        await store.SetAsync("key", [2], ["new"], TimeSpan.FromMinutes(1));
        await store.EvictByTagAsync("old");
        await AssertPayload(store, "key", 2);
        await store.EvictByTagAsync("new");
        await Assert.That(await store.GetAsync("key")).IsNull();
    }

    [Test]
    [Arguments(2, false)]
    [Arguments(2, true)]
    [Arguments(3, false)]
    [Arguments(3, true)]
    public async Task ConcurrentPublicationAndEvictionPreserveLaterGeneration(int protocol, bool pausePublication)
    {
        await using var client = await ConnectAsync(protocol);
        var options = Options();
        var store = new RespireOutputCacheStore(client, options);
        await store.SetAsync("key", [1], ["shared"], TimeSpan.FromMinutes(1));
        var (wrapped, barrier) = WrapScripts(client);
        barrier.PausePublication = pausePublication;
        var paused = new RespireOutputCacheStore(wrapped, options);
        var operation = pausePublication
            ? paused.SetAsync("key", [2], ["shared"], TimeSpan.FromMinutes(1)).AsTask()
            : paused.EvictByTagAsync("shared").AsTask();
        try
        {
            await barrier.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (pausePublication) await store.EvictByTagAsync("shared");
            else await store.SetAsync("key", [2], ["shared"], TimeSpan.FromMinutes(1));
        }
        finally
        {
            barrier.Release.TrySetResult();
            await operation.WaitAsync(TimeSpan.FromSeconds(10));
        }
        await AssertPayload(store, "key", 2);
        // A second eviction must still find the surviving generation's membership.
        await store.EvictByTagAsync("shared");
        await Assert.That(await store.GetAsync("key")).IsNull();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task PartialIndexFailureLeavesOldPayloadAndOrphansCannotDeleteReplacement(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var options = Options();
        var store = new RespireOutputCacheStore(client, options);
        await store.SetAsync("key", [1], ["old"], TimeSpan.FromMinutes(1));
        var username = $"output-{Guid.NewGuid():N}";
        var password = Guid.NewGuid().ToString("N");
        var allowedIndexes = $"(+zadd ~{options.InstanceName}__RPOCT2 ~{options.InstanceName}__RPOCT2_orphan)";
        using (var reply = await client.ExecuteAsync("ACL", ["SETUSER", username, "on", ">" + password, "~*", "+@all", "-zadd", allowedIndexes])) { }
        try
        {
            await using var restricted = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with
            { Protocol = (RespProtocol)protocol, Username = username, Password = password });
            var failing = new RespireOutputCacheStore(restricted, options);
            await Assert.That(async () => await failing.SetAsync("key", [2], ["orphan", "denied"], TimeSpan.FromMinutes(1)))
                .Throws<RespireServerException>();
            await AssertPayload(store, "key", 1);
            using (var count = await client.ExecuteAsync("ZCARD", [options.InstanceName + "__RPOCT2_orphan"]))
                await Assert.That(count.AsInteger()).IsEqualTo(1);
            await store.SetAsync("key", [3], ["new"], TimeSpan.FromMinutes(1));
            await store.EvictByTagAsync("orphan");
            await store.EvictByTagAsync("old");
            await AssertPayload(store, "key", 3);
            await store.EvictByTagAsync("new");
            await Assert.That(await store.GetAsync("key")).IsNull();
        }
        finally { using var reply = await client.ExecuteAsync("ACL", ["DELUSER", username]); }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task MissingExpiryPermissionCannotPublishUnboundedValue(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var options = Options();
        var store = new RespireOutputCacheStore(client, options);
        await store.SetAsync("key", [1], ["old"], TimeSpan.FromMinutes(1));
        var username = $"output-{Guid.NewGuid():N}";
        var password = Guid.NewGuid().ToString("N");
        using (var reply = await client.ExecuteAsync("ACL", ["SETUSER", username, "on", ">" + password, "~*", "+@all", "-pexpireat"])) { }
        try
        {
            await using var restricted = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with
            { Protocol = (RespProtocol)protocol, Username = username, Password = password });
            await Assert.That(async () => await new RespireOutputCacheStore(restricted, options)
                .SetAsync("key", [2], ["new"], TimeSpan.FromMinutes(1))).Throws<RespireServerException>();
            await AssertPayload(store, "key", 1);
            using var membership = await client.ExecuteAsync("EXISTS", [options.InstanceName + "__RPOCT2_new"]);
            await Assert.That(membership.AsInteger()).IsEqualTo(0);
        }
        finally { using var reply = await client.ExecuteAsync("ACL", ["DELUSER", username]); }
    }

    [Test]
    [Arguments(2, false)]
    [Arguments(2, true)]
    [Arguments(3, false)]
    [Arguments(3, true)]
    public async Task WrongTypePreflightDoesNotPublishOrRegisterOtherTags(int protocol, bool corruptMaster)
    {
        await using var client = await ConnectAsync(protocol);
        var options = Options();
        var store = new RespireOutputCacheStore(client, options);
        await store.SetAsync("key", [1], ["old"], TimeSpan.FromMinutes(1));
        await client.SetAsync(options.InstanceName + (corruptMaster ? "__RPOCT2" : "__RPOCT2_bad"), "wrong type");
        await Assert.That(async () => await store.SetAsync("key", [2], ["first", "bad"], TimeSpan.FromMinutes(1)))
            .Throws<RespireServerException>();
        await AssertPayload(store, "key", 1);
        await Assert.That(await client.ExistsAsync(options.InstanceName + "__RPOCT2_first")).IsFalse();
    }

    [Test]
    public async Task CleanupRemovesObsoleteGenerationsButPreservesLiveMembership()
    {
        await using var client = await ConnectAsync(3);
        var options = Options();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        options.TimeProvider = clock;
        var store = new RespireOutputCacheStore(client, options);
        await store.SetAsync("key", [1], ["shared"], TimeSpan.FromMinutes(5));
        await store.SetAsync("key", [2], ["shared"], TimeSpan.FromHours(1));
        using (var count = await client.ExecuteAsync("ZCARD", [options.InstanceName + "__RPOCT2_shared"]))
            await Assert.That(count.AsInteger()).IsEqualTo(2);
        clock.Now += TimeSpan.FromMinutes(10);
        await store.CollectExpiredTagsAsync();
        using (var count = await client.ExecuteAsync("ZCARD", [options.InstanceName + "__RPOCT2_shared"]))
            await Assert.That(count.AsInteger()).IsEqualTo(1);
        await AssertPayload(store, "key", 2);
        await store.EvictByTagAsync("shared");
        await Assert.That(await store.GetAsync("key")).IsNull();
    }

    [Test]
    public async Task BulkEvictionPreservesReplacementAcrossPageBoundary()
    {
        await using var client = await ConnectAsync(3);
        var store = new RespireOutputCacheStore(client, Options());
        for (var index = 0; index < 251; index++)
            await store.SetAsync(index.ToString(), [1], ["old"], TimeSpan.FromMinutes(1));
        await store.SetAsync("250", [2], ["new"], TimeSpan.FromMinutes(1));
        await store.EvictByTagAsync("old");
        for (var index = 0; index < 250; index++)
            await Assert.That(await store.GetAsync(index.ToString())).IsNull();
        await AssertPayload(store, "250", 2);
        await store.EvictByTagAsync("new");
        await Assert.That(await store.GetAsync("250")).IsNull();
    }

    [Test]
    public async Task BufferAndSegmentedPayloadsAreUnchangedAndModesAreIsolated()
    {
        await using var client = await ConnectAsync(3);
        var options = Options();
        var store = new RespireOutputCacheStore(client.WithKeyPrefix("tenant:"), options);
        var compatibility = new RespireOutputCacheStore(client.WithKeyPrefix("tenant:"), new() { InstanceName = options.InstanceName });
        await compatibility.SetAsync("", [9], [""], TimeSpan.FromMinutes(1));
        await Assert.That(await store.GetAsync("")).IsNull();
        var head = new Segment([0, 255]);
        var tail = head.Append([1, 2]);
        var sequence = new ReadOnlySequence<byte>(head, 0, tail, tail.Memory.Length);
        await store.SetAsync("", sequence, new[] { "" }.AsMemory(), TimeSpan.FromMinutes(1));
        await AssertPayload(compatibility, "", 9);
        using var output = new MemoryStream();
        var writer = PipeWriter.Create(output, new(leaveOpen: true));
        try
        {
            await Assert.That(await store.TryGetAsync("missing", writer)).IsFalse();
            await Assert.That(await store.TryGetAsync("", writer)).IsTrue();
            await Assert.That(output.ToArray().SequenceEqual(new byte[] { 0, 255, 1, 2 })).IsTrue();
        }
        finally { await writer.CompleteAsync(); }
        await store.EvictByTagAsync("");
        await Assert.That(await store.GetAsync("")).IsNull();
        await AssertPayload(compatibility, "", 9);
        await store.SetAsync("empty", [], null, TimeSpan.FromMinutes(1));
        await Assert.That((await store.GetAsync("empty"))!.Length).IsEqualTo(0);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task SharedHashTagMakesGenerationScriptsClusterSafe(int protocol)
    {
        await using var cluster = await RedisClusterTestContainer.StartAsync();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        { UseCluster = true, Protocol = (RespProtocol)protocol, Connections = 1, Endpoints = [new(cluster.Host, cluster.Port(0))] });
        var store = new RespireOutputCacheStore(client.WithKeyPrefix("tenant:"), Options());
        await store.SetAsync("key:{other-slot}", [1], ["old:{different-slot}"], TimeSpan.FromMinutes(1));
        await store.SetAsync("key:{other-slot}", [2], ["new:{third-slot}"], TimeSpan.FromMinutes(1));
        await store.EvictByTagAsync("old:{different-slot}");
        await AssertPayload(store, "key:{other-slot}", 2);
        await store.CollectExpiredTagsAsync();
        await store.EvictByTagAsync("new:{third-slot}");
        await Assert.That(await store.GetAsync("key:{other-slot}")).IsNull();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task LostPublicationReplyIsNotReplayedAndOldTagRemainsHarmless(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var options = Options();
        var store = new RespireOutputCacheStore(client, options);
        await store.SetAsync("key", [1], ["old"], TimeSpan.FromMinutes(1));
        var (wrapped, barrier) = WrapScripts(client);
        barrier.LosePublicationReply = true;
        var uncertain = new RespireOutputCacheStore(wrapped, options);
        await Assert.That(async () => await uncertain.SetAsync("key", [2], ["new"], TimeSpan.FromMinutes(1)))
            .ThrowsExactly<TimeoutException>();
        await Assert.That(barrier.PublicationCount).IsEqualTo(1);
        await store.EvictByTagAsync("old");
        await AssertPayload(store, "key", 2);
        await store.EvictByTagAsync("new");
        await Assert.That(await store.GetAsync("key")).IsNull();
    }

    private static async Task AssertPayload(RespireOutputCacheStore store, string key, byte expected)
    {
        var bytes = await store.GetAsync(key);
        await Assert.That(bytes).IsNotNull();
        await Assert.That(bytes!.Single()).IsEqualTo(expected);
    }

    private static (IRespireClient Client, ScriptBarrier Barrier) WrapScripts(IRespireClient client)
    {
        var scripts = DispatchProxy.Create<IScriptCommands, ScriptBarrier>();
        var barrier = (ScriptBarrier)scripts;
        barrier.Inner = client.Scripts;
        var wrapped = DispatchProxy.Create<IRespireClient, ClientProxy>();
        ((ClientProxy)wrapped).Inner = client;
        ((ClientProxy)wrapped).Scripts = scripts;
        return (wrapped, barrier);
    }

    public class ClientProxy : DispatchProxy
    {
        public IRespireClient Inner { get; set; } = null!;
        public IScriptCommands Scripts { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? arguments) =>
            method!.Name == "get_Scripts" ? Scripts : method.Invoke(Inner, arguments);
    }

    public class ScriptBarrier : DispatchProxy
    {
        private int _paused;
        public IScriptCommands Inner { get; set; } = null!;
        public bool PausePublication { get; set; }
        public bool LosePublicationReply { get; set; }
        public int PublicationCount { get; private set; }
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            if (method!.Name == nameof(IScriptCommands.ExecuteIntegerAsync) && LosePublicationReply
                && ((RespireScript)arguments![0]!).Source.Contains("'HSET'", StringComparison.Ordinal))
                return ExecuteThenLoseReplyAsync(method, arguments);
            if (method!.Name == nameof(IScriptCommands.ExecuteIntegerAsync)
                && ((RespireScript)arguments![0]!).Source.Contains(PausePublication ? "'HSET'" : "'HGET'", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _paused, 1) == 0)
                return ExecuteAfterReleaseAsync(method, arguments);
            return method.Invoke(Inner, arguments);
        }
        private async ValueTask<long> ExecuteAfterReleaseAsync(MethodInfo method, object?[]? arguments)
        {
            Reached.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return await (ValueTask<long>)method.Invoke(Inner, arguments)!;
        }
        private async ValueTask<long> ExecuteThenLoseReplyAsync(MethodInfo method, object?[]? arguments)
        {
            PublicationCount++;
            _ = await (ValueTask<long>)method.Invoke(Inner, arguments)!;
            throw new TimeoutException("Publication completed but its reply was lost.");
        }
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(byte[] bytes) => Memory = bytes;
        public Segment Append(byte[] bytes)
        {
            var next = new Segment(bytes) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private ValueTask<RespireClient> ConnectAsync(int protocol) => RespireClient.ConnectAsync(
        RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });

    private static RespireOutputCacheOptions Options() => new()
    {
        InstanceName = $"{{generation:{Guid.NewGuid():N}}}:",
        TaggingMode = RespireOutputCacheTaggingMode.GenerationAware,
    };
}
