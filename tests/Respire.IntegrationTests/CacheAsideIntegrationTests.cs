using StackExchange.Redis;
using StackExchange.Redis.KeyspaceIsolation;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.Keyed, Key = TestConstraints.ClientCacheServer)]
[NotInParallel(TestConstraints.ClientCacheHits)]
[SkipOnGarnet("Garnet 2.1.8 rejects CLIENT TRACKING, which client-side caching requires.")]
public class CacheAsideIntegrationTests(RedisTestContainer fixture)
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);

    [Test]
    public async Task AbsoluteExpiryUsesTheServerDeadlineAndHitsDoNotExtendIt()
    {
        var key = NewKey();
        await using var client = await ConnectAsync();
        var deadline = DateTimeOffset.UtcNow.AddMinutes(5);

        await Assert.That(await client.GetOrSetAsync<string>(key,
            _ => ValueTask.FromResult<string?>("created"), expiry: deadline)).IsEqualTo("created");
        var expiry = await client.Keys.ExpiryTimeAsync(key);
        await Assert.That(expiry.UnixTimeMilliseconds).IsEqualTo(deadline.ToUnixTimeMilliseconds());

        await client.GetOrSetAsync<string>(key, _ => throw new Exception("factory"), expiry: deadline.AddHours(1));
        await client.GetOrSetAsync<string>(key, _ => throw new Exception("factory"), expiry: Ttl);
        await Assert.That((await client.Keys.ExpiryTimeAsync(key)).UnixTimeMilliseconds)
            .IsEqualTo(deadline.ToUnixTimeMilliseconds());
    }

    [Test]
    public async Task PastAbsoluteExpiryReturnsProducedValueWithoutRetainingTheKey()
    {
        var key = NewKey();
        await using var client = await ConnectAsync();

        await Assert.That(await client.GetOrSetAsync<string>(key,
            _ => ValueTask.FromResult<string?>("created"), expiry: DateTimeOffset.UnixEpoch.AddSeconds(1)))
            .IsEqualTo("created");
        await Assert.That(await client.GetStringAsync(key)).IsNull();
    }

    [Test]
    [Arguments(RespireClientTrackingMode.OptIn)]
    [Arguments(RespireClientTrackingMode.Broadcast)]
    public async Task CreationAndExternalWritesInvalidateOtherClients(RespireClientTrackingMode mode)
    {
        var key = NewKey();
        await using var first = await ConnectAsync(mode);
        await using var second = await ConnectAsync(mode);
        await Assert.That(await second.GetStringAsync(key)).IsNull();
        await Assert.That(second.ClientSideCache!.Count).IsEqualTo(1);
        await Assert.That(await first.GetOrSetAsync<string>(key, _ => ValueTask.FromResult<string?>("created"), Ttl)).IsEqualTo("created");
        await WaitUntilAsync(() => second.ClientSideCache!.Count == 0);
        await Assert.That(await second.GetOrSetAsync<string>(key, _ => throw new Exception("factory"), Ttl)).IsEqualTo("created");
        // The SET result is not injected into tracking state; a subsequent GET registers it.
        await first.GetOrSetAsync<string>(key, _ => throw new Exception("factory"), Ttl);
        await Assert.That(first.ClientSideCache!.Count).IsEqualTo(1);
        await second.SetAsync(key, "changed", Ttl);
        await WaitUntilAsync(() => first.ClientSideCache!.Count == 0);
        await Assert.That(await first.GetOrSetAsync<string>(key, _ => throw new Exception("factory"), Ttl)).IsEqualTo("changed");
    }

    [Test]
    public async Task CrossClientFactoriesRaceButBothReturnTheAtomicWinner()
    {
        var key = NewKey();
        await using var first = await ConnectAsync();
        await using var second = await ConnectAsync();
        var entered = NewSignal();
        var release = NewSignal();
        var calls = 0;
        async ValueTask<string?> Factory(string value, CancellationToken token)
        {
            if (Interlocked.Increment(ref calls) == 2) entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return value;
        }
        var left = first.GetOrSetAsync<string>(key, token => Factory("left", token), Ttl).AsTask();
        var right = second.GetOrSetAsync<string>(key, token => Factory("right", token), Ttl).AsTask();
        try { await entered.Task.WaitAsync(Limit); }
        finally { release.TrySetResult(); }
        var results = await Task.WhenAll(left, right).WaitAsync(Limit);
        await Assert.That(calls).IsEqualTo(2);
        await Assert.That(results[0]).IsEqualTo(results[1]);
        await Assert.That(await first.GetStringAsync(key)).IsEqualTo(results[0]);
    }

    [Test]
    public async Task ConcurrentWinnerKeepsItsExistingServerTtl()
    {
        var key = NewKey();
        await using var client = await ConnectAsync();
        await using var other = await ConnectAsync();
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(fixture.StackExchangeConnectionString);
        var entered = NewSignal();
        var release = NewSignal();
        var read = client.GetOrSetAsync<string>(key, async token =>
        {
            entered.TrySetResult(); await release.Task.WaitAsync(token); return "loser";
        }, TimeSpan.FromSeconds(1)).AsTask();
        try
        {
            await entered.Task.WaitAsync(Limit);
            await other.SetAsync(key, "winner", TimeSpan.FromMinutes(20));
        }
        finally { release.TrySetResult(); }
        await Assert.That(await read.WaitAsync(Limit)).IsEqualTo("winner");
        var ttl = await multiplexer.GetDatabase().WithKeyPrefix(fixture.KeyPrefix).KeyTimeToLiveAsync(key);
        await Assert.That(ttl!.Value > TimeSpan.FromMinutes(19)).IsTrue();
    }

    [Test]
    public async Task ServerExpiryInvalidatesTrackedValueAndNextCallRecreatesIt()
    {
        var key = NewKey();
        await using var client = await ConnectAsync();
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(fixture.StackExchangeConnectionString);
        await client.GetOrSetAsync<string>(key, _ => ValueTask.FromResult<string?>("first"), TimeSpan.FromSeconds(1));
        await client.GetOrSetAsync<string>(key, _ => throw new Exception("factory"), Ttl);
        using var timeout = new CancellationTokenSource(Limit);
        while (await multiplexer.GetDatabase().WithKeyPrefix(fixture.KeyPrefix).KeyExistsAsync(key)) await Task.Delay(10, timeout.Token);
        await WaitUntilAsync(() => client.ClientSideCache!.Count == 0);
        await Assert.That(await client.GetOrSetAsync<string>(key, _ => ValueTask.FromResult<string?>("second"), Ttl)).IsEqualTo("second");
    }

    [Test]
    public async Task SameClientSharesFactoryAndReturnsIndependentTypedObjects()
    {
        var key = NewKey();
        await using var client = await ConnectAsync();
        var entered = NewSignal();
        var release = NewSignal();
        var calls = 0;
        var original = new Product { Name = "owned" };
        async ValueTask<Product?> Factory(CancellationToken token)
        {
            Interlocked.Increment(ref calls); entered.TrySetResult();
            await release.Task.WaitAsync(token); return original;
        }
        var first = client.GetOrSetAsync(key, Factory, Ttl).AsTask();
        var second = client.GetOrSetAsync(key, Factory, Ttl).AsTask();
        try { await entered.Task.WaitAsync(Limit); }
        finally { release.TrySetResult(); }
        var values = await Task.WhenAll(first, second).WaitAsync(Limit);
        await Assert.That(calls).IsEqualTo(1);
        original.Name = "factory mutated";
        values[0]!.Name = "caller mutated";
        await Assert.That(values[1]!.Name).IsEqualTo("owned");
        var cached = await client.GetOrSetAsync<Product>(key, _ => throw new Exception("factory"), Ttl);
        cached!.Name = "cache caller mutated";
        await Assert.That((await client.GetOrSetAsync<Product>(key, _ => throw new Exception("factory"), Ttl))!.Name).IsEqualTo("owned");
    }

    [Test]
    public async Task FactoryFailuresAndCancellationDoNotCreateKeys()
    {
        var failedKey = NewKey();
        var canceledKey = NewKey();
        await using var client = await ConnectAsync();
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(fixture.StackExchangeConnectionString);
        await Assert.That(async () => await client.GetOrSetAsync<string>(failedKey, _ => throw new InvalidOperationException("factory"), Ttl))
            .ThrowsExactly<InvalidOperationException>();
        var entered = NewSignal();
        var exited = NewSignal();
        using var canceled = new CancellationTokenSource();
        var pending = client.GetOrSetAsync<string>(canceledKey, async token =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return "never"; }
            finally { exited.TrySetResult(); }
        }, Ttl, canceled.Token).AsTask();
        await entered.Task.WaitAsync(Limit);
        canceled.Cancel();
        await Assert.That(async () => await pending.WaitAsync(Limit)).Throws<OperationCanceledException>();
        await exited.Task.WaitAsync(Limit);
        await Assert.That(await multiplexer.GetDatabase().WithKeyPrefix(fixture.KeyPrefix).KeyExistsAsync(failedKey)).IsFalse();
        await Assert.That(await multiplexer.GetDatabase().WithKeyPrefix(fixture.KeyPrefix).KeyExistsAsync(canceledKey)).IsFalse();
    }

    [Test]
    public async Task ReconnectFlushesPreviousValueAndRestoresTrackedReads()
    {
        var key = NewKey();
        await using var client = await ConnectAsync();
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(fixture.StackExchangeConnectionString);
        var database = multiplexer.GetDatabase().WithKeyPrefix(fixture.KeyPrefix);
        using var idReply = await client.ExecuteAsync("CLIENT", "ID");
        var id = idReply.AsInteger();
        await database.StringSetAsync(key, "before");
        await client.GetOrSetAsync<string>(key, _ => throw new Exception("factory"), Ttl);
        await database.ExecuteAsync("CLIENT", "KILL", "ID", id);
        await WaitUntilAsync(() => client.ClientSideCache!.Count == 0);
        await database.StringSetAsync(key, "after");
        using var timeout = new CancellationTokenSource(Limit);
        while (true)
        {
            try { await client.PingAsync(timeout.Token); break; }
            catch (RespireConnectionException) { await Task.Delay(10, timeout.Token); }
        }
        await Assert.That(await client.GetOrSetAsync<string>(key, _ => throw new Exception("factory"), Ttl)).IsEqualTo("after");
        var hits = client.ClientSideCache!.GetStatistics().Hits;
        await client.GetOrSetAsync<string>(key, _ => throw new Exception("factory"), Ttl);
        await Assert.That(client.ClientSideCache.GetStatistics().Hits).IsEqualTo(hits + 1);
    }

    public sealed class Product { public string? Name { get; set; } }
    private static string NewKey() => $"cache-aside:{Guid.NewGuid():N}";
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ValueTask<RespireClient> ConnectAsync(RespireClientTrackingMode mode = RespireClientTrackingMode.OptIn)
        => RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with
        {
            Connections = 1,
            ClientSideCache = new()
            {
                CoalesceConcurrentMisses = true, TrackingMode = mode,
                // Tracking prefixes match physical keys, which carry the fixture's per-test prefix.
                KeyPrefixes = mode == RespireClientTrackingMode.Broadcast ? [fixture.KeyPrefix + "cache-aside:"] : [],
            },
        });
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Limit);
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
