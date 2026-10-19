using System.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.Keyed, Key = TestConstraints.ClientCacheServer)]
[NotInParallel(TestConstraints.ClientCacheHits)]
public class CacheStampedeIntegrationTests(RedisTestContainer fixture)
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);
    private static readonly RespireCacheAsideOptions Options = new() { WaitTimeout = TimeSpan.FromSeconds(10) };

    [Test]
    public async Task ManyIndependentClientsRecomputeOncePerExpiry()
    {
        var clients = new List<RespireClient>();
        var key = NewKey();
        var calls = 0;
        try
        {
            for (var index = 0; index < 16; index++) clients.Add(await ConnectAsync());
            for (var generation = 1; generation <= 3; generation++)
            {
                // Expire immediately on Redis, including after each populated generation.
                await clients[0].ExpireAsync(key, DateTimeOffset.UnixEpoch);
                var entered = Signal();
                var release = Signal();
                async ValueTask<string?> Factory(CancellationToken token)
                {
                    Interlocked.Increment(ref calls);
                    entered.TrySetResult();
                    await release.Task.WaitAsync(token);
                    return $"generation-{generation}";
                }
                var reads = clients.SelectMany(client => Enumerable.Range(0, 4)
                    .Select(_ => client.GetOrSetAsync(key, Options, Factory, Ttl).AsTask())).ToArray();
                try { await entered.Task.WaitAsync(Limit); }
                finally { release.TrySetResult(); }
                var results = await Task.WhenAll(reads).WaitAsync(Limit);
                await Assert.That(calls).IsEqualTo(generation);
                await Assert.That(results.All(value => value == $"generation-{generation}")).IsTrue();
            }
        }
        finally { foreach (var client in clients) await client.DisposeAsync(); }
    }

    [Test]
    public async Task CrashedHolderExpiresAndAnotherClientPopulates()
    {
        await using var client = await ConnectAsync();
        var key = NewKey();
        var lockKey = RespireClient.CreateCacheAsideLockKey(key);
        using var marker = await client.ExecuteAsync("HSET", key, "version", "crashed-process");
        await client.ExpireAsync(key, TimeSpan.FromMilliseconds(150));
        await client.SetAsync(lockKey, "crashed-process", TimeSpan.FromMilliseconds(150));
        var started = Stopwatch.GetTimestamp();
        var calls = 0;
        var result = await client.GetOrSetAsync<string>(key, Options, _ =>
        {
            calls++;
            return ValueTask.FromResult<string?>("recovered");
        }, Ttl).AsTask().WaitAsync(Limit);
        await Assert.That(result).IsEqualTo("recovered");
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5)).IsTrue();
        await Assert.That(await client.ExistsAsync(lockKey)).IsFalse();
    }

    [Test]
    public async Task WaitBudgetDoesNotStartAnUnprotectedFactory()
    {
        await using var client = await ConnectAsync();
        var key = NewKey();
        var lockKey = RespireClient.CreateCacheAsideLockKey(key);
        await client.SetAsync(lockKey, "owner", Ttl);
        var calls = 0;
        await Assert.That(async () => await client.GetOrSetAsync<string>(key, Options with { WaitTimeout = TimeSpan.FromMilliseconds(60) }, _ =>
        {
            calls++;
            return ValueTask.FromResult<string?>("unexpected");
        }, Ttl))
            .ThrowsExactly<TimeoutException>();
        await Assert.That(calls).IsEqualTo(0);
        await Assert.That(await client.GetStringAsync(lockKey)).IsEqualTo("owner");
    }

    [Test]
    public async Task ContendersServeStaleWhileElectedCallerAwaitsRefresh()
    {
        await using var first = await ConnectAsync();
        await using var second = await ConnectAsync();
        var key = NewKey();
        var options = Options with { StaleWhileRevalidate = TimeSpan.FromMinutes(1) };
        await first.GetOrSetAsync<string>(key, options, _ => ValueTask.FromResult<string?>("old"), Ttl);
        // Move the logical freshness boundary into the past while retaining the stale window.
        await first.ExpireAsync(key, TimeSpan.FromSeconds(30));
        var entered = Signal();
        var release = Signal();
        var refresh = first.GetOrSetAsync<string>(key, options, async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return "new";
        }, Ttl).AsTask();
        try
        {
            await entered.Task.WaitAsync(Limit);
            var stale = await second.GetOrSetAsync<string>(key, options, _ => throw new Exception("contender factory"), Ttl).AsTask().WaitAsync(Limit);
            await Assert.That(stale).IsEqualTo("old");
            await Assert.That(refresh.IsCompleted).IsFalse();
        }
        finally { release.TrySetResult(); }
        await Assert.That(await refresh.WaitAsync(Limit)).IsEqualTo("new");
        await Assert.That(await second.GetOrSetAsync<string>(key, options, _ => throw new Exception("hit factory"), Ttl))
            .IsEqualTo("new");
    }

    [Test]
    public async Task EarlyRefreshUsesSharedRecomputeDurationAcrossClients()
    {
        await using var first = await ConnectAsync();
        await using var second = await ConnectAsync();
        var key = NewKey();
        await first.GetOrSetAsync<string>(key, Options, _ => ValueTask.FromResult<string?>("old"), Ttl);
        await second.GetOrSetAsync<string>(key, Options, _ => throw new Exception("beta zero"), Ttl);
        // Force the election for any Random.NextDouble sample in [0, 1).
        var refresh = Options with { EarlyRefreshBeta = double.MaxValue };
        await Assert.That(await second.GetOrSetAsync<string>(key, refresh, _ => ValueTask.FromResult<string?>("early"), Ttl))
            .IsEqualTo("early");
        using var duration = await first.ExecuteAsync("HGET", key, "duration");
        await Assert.That(duration.AsDouble() >= 1).IsTrue();
    }

    [Test]
    public async Task ObsoleteOwnerCannotPublishOrReleaseSuccessorLease()
    {
        await using var first = await ConnectAsync();
        await using var second = await ConnectAsync();
        var key = NewKey();
        var lockKey = RespireClient.CreateCacheAsideLockKey(key);
        var entered = Signal();
        var release = Signal();
        var pending = first.GetOrSetAsync<string>(key, Options, async _ =>
        {
            entered.TrySetResult();
            await release.Task; // Deliberately ignore cancellation to exercise server fencing.
            return "obsolete";
        }, Ttl).AsTask();
        try
        {
            await entered.Task.WaitAsync(Limit);
            await second.SetAsync(lockKey, "successor", Ttl);
        }
        finally { release.TrySetResult(); }
        await Assert.That(await pending.WaitAsync(Limit)).IsEqualTo("obsolete");
        await Assert.That(await second.GetStringAsync(lockKey)).IsEqualTo("successor");
        await Assert.That(await second.ExistsAsync(key)).IsFalse();
    }

    [Test]
    public async Task FactoryDeadlineSafelyCancelsThrowingCallbacksAndAllowsRecovery()
    {
        await using var client = await ConnectAsync();
        var key = NewKey();
        await Assert.That(async () => await client.GetOrSetAsync<string>(key, Options with { LockTimeout = TimeSpan.FromMilliseconds(100) }, async token =>
        {
            using var registration = token.Register(static () => throw new InvalidOperationException("callback"));
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "never";
        }, Ttl))
            .ThrowsExactly<TimeoutException>();
        await Assert.That(await client.ExistsAsync(RespireClient.CreateCacheAsideLockKey(key))).IsFalse();
        await Assert.That(await client.GetOrSetAsync<string>(key, Options, _ => ValueTask.FromResult<string?>("recovered"), Ttl))
            .IsEqualTo("recovered");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FactoryFailureNullAndCancellationReleaseTheLease(bool singleFlight)
    {
        await using var client = await ConnectAsync();
        var key = NewKey();
        var options = Options with { DistributedSingleFlight = singleFlight };
        var lockKey = RespireClient.CreateCacheAsideLockKey(key);
        await Assert.That(async () => await client.GetOrSetAsync<string>(key, options, _ => throw new InvalidOperationException("factory"), Ttl)).ThrowsExactly<InvalidOperationException>();
        await Assert.That(await client.ExistsAsync(key)).IsFalse();
        await Assert.That(await client.ExistsAsync(lockKey)).IsFalse();
        await Assert.That(await client.GetOrSetAsync<string>(key, options, _ => ValueTask.FromResult<string?>(null), Ttl)).IsNull();
        await Assert.That(await client.ExistsAsync(key)).IsFalse();
        await Assert.That(await client.ExistsAsync(lockKey)).IsFalse();
        using var canceled = new CancellationTokenSource();
        var entered = Signal();
        var pending = client.GetOrSetAsync<string>(key, options, async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "never";
        }, Ttl, canceled.Token).AsTask();
        await entered.Task.WaitAsync(Limit);
        canceled.Cancel();
        await Assert.That(async () => await pending.WaitAsync(Limit)).Throws<OperationCanceledException>();
        await Assert.That(await client.ExistsAsync(key)).IsFalse();
        await Assert.That(await client.ExistsAsync(lockKey)).IsFalse();
    }

    [Test]
    public async Task FactoryFailurePreservesStringReplacementAndReleasesLease()
    {
        await using var client = await ConnectAsync();
        var key = NewKey();
        await Assert.That(async () => await client.GetOrSetAsync<string>(key, Options, async token =>
        {
            await client.SetAsync(key, "replacement", cancellationToken: token);
            throw new InvalidOperationException("factory");
        }, Ttl)).ThrowsExactly<InvalidOperationException>();
        await Assert.That(await client.GetStringAsync(key)).IsEqualTo("replacement");
        await Assert.That(await client.ExistsAsync(RespireClient.CreateCacheAsideLockKey(key))).IsFalse();
    }

    [Test]
    public async Task CancelingAWaiterDoesNotCancelTheHolder()
    {
        await using var client = await ConnectAsync();
        var key = NewKey();
        var entered = Signal();
        var release = Signal();
        var holder = client.GetOrSetAsync<string>(key, Options, async token =>
        {
            entered.TrySetResult(); await release.Task.WaitAsync(token); return "holder";
        }, Ttl).AsTask();
        try
        {
            await entered.Task.WaitAsync(Limit);
            using var canceled = new CancellationTokenSource();
            var waiter = client.GetOrSetAsync<string>(key, Options, _ => throw new Exception("waiter factory"), Ttl, canceled.Token).AsTask();
            canceled.Cancel();
            await Assert.That(async () => await waiter.WaitAsync(Limit)).Throws<OperationCanceledException>();
            await Assert.That(holder.IsCompleted).IsFalse();
        }
        finally { release.TrySetResult(); }
        await Assert.That(await holder.WaitAsync(Limit)).IsEqualTo("holder");
    }

    [Test]
    public async Task InternalScriptsBorrowOneHookScopeAndFactoryCommandsRemainIndependent()
    {
        var hook = new RecordingHook();
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with
        {
            Connections = 1, CommandHook = hook,
        });
        var key = NewKey();
        await client.GetOrSetAsync<string>(key, Options, async token =>
        {
            await client.PingAsync(token);
            return "value";
        }, Ttl);
        await Assert.That(string.Join(",", hook.Commands)).IsEqualTo("GETORSET,PING");
        await Assert.That(hook.Completed).IsEqualTo(2);
        hook.Commands.Clear();
        await client.GetOrSetAsync<string>(key, Options, _ => throw new Exception("hit"), Ttl);
        await Assert.That(string.Join(",", hook.Commands)).IsEqualTo("GETORSET");
        await Assert.That(hook.Completed).IsEqualTo(3);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PrefixViewsAndCacheBypassUseSharedPhysicalEntry(bool bypass)
    {
        await using var client = await ConnectAsync();
        var prefix = NewKey();
        IRespireClient view = client.WithKeyPrefix(prefix);
        if (bypass) view = view.WithoutClientCache();
        var created = new Payload { Value = "original" };
        var first = await view.GetOrSetAsync<Payload>("key", Options, _ => ValueTask.FromResult<Payload?>(created), Ttl);
        created.Value = "factory mutation";
        first!.Value = "caller mutation";
        var second = await client.GetOrSetAsync<Payload>(prefix + "key", Options, _ => throw new Exception("hit"), Ttl);
        await Assert.That(second!.Value).IsEqualTo("original");
        await Assert.That(await client.ExistsAsync(RespireClient.CreateCacheAsideLockKey(prefix + "key"))).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EarlyRefreshCanRunWithoutSingleFlightAndAbsoluteExpiryIsPreserved(bool past)
    {
        await using var client = await ConnectAsync();
        var key = NewKey();
        var deadline = past ? DateTimeOffset.UnixEpoch.AddSeconds(1) : DateTimeOffset.UtcNow.AddMinutes(5);
        var options = Options with { DistributedSingleFlight = false, EarlyRefreshBeta = 1 };
        await Assert.That(await client.GetOrSetAsync<int>(key, options, _ => ValueTask.FromResult(42), deadline)).IsEqualTo(42);
        if (past) await Assert.That(await client.ExistsAsync(key)).IsFalse();
        else await Assert.That((await client.Keys.ExpiryTimeAsync(key)).UnixTimeMilliseconds).IsEqualTo(deadline.ToUnixTimeMilliseconds());
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RefreshRetainsGenerationBeyondPayloadExpiryWithoutServingExpiredData(bool stale)
    {
        await using var first = await ConnectAsync();
        await using var second = await ConnectAsync();
        var key = NewKey();
        var options = Options with { StaleWhileRevalidate = stale ? TimeSpan.FromMilliseconds(200) : TimeSpan.Zero };
        await first.GetOrSetAsync<string>(key, options, _ => ValueTask.FromResult<string?>("old"), TimeSpan.FromSeconds(1));
        var entered = Signal();
        var release = Signal();
        var refresh = first.GetOrSetAsync<string>(key, options with { EarlyRefreshBeta = double.MaxValue }, async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return "new";
        }, Ttl).AsTask();
        try
        {
            await entered.Task.WaitAsync(Limit);
            await Task.Delay(1500); // Cross both the original freshness and stale deadlines.
            await Assert.That(await second.ExistsAsync(key)).IsTrue();
            await Assert.That(async () => await second.GetOrSetAsync<string>(key,
                options with { WaitTimeout = TimeSpan.Zero }, _ => throw new Exception("contender factory"), Ttl))
                .ThrowsExactly<TimeoutException>();
        }
        finally { release.TrySetResult(); }
        await Assert.That(await refresh.WaitAsync(Limit)).IsEqualTo("new");
        await Assert.That(await second.GetOrSetAsync<string>(key, Options, _ => throw new Exception("hit"), Ttl)).IsEqualTo("new");
    }

    [Test]
    [Arguments(false, false, false)]
    [Arguments(false, false, true)]
    [Arguments(false, true, false)]
    [Arguments(false, true, true)]
    [Arguments(true, false, false)]
    [Arguments(true, false, true)]
    [Arguments(true, true, false)]
    [Arguments(true, true, true)]
    public async Task PopulationCannotOverwriteDeletedOrReplacedGeneration(bool warm, bool replace, bool singleFlight)
    {
        await using var client = await ConnectAsync();
        var key = NewKey();
        if (warm)
            await client.GetOrSetAsync<Payload>(key, Options, _ => ValueTask.FromResult<Payload?>(new() { Value = "old" }), Ttl);
        var entered = Signal();
        var release = Signal();
        var created = new Payload { Value = "computed" };
        var refresh = client.GetOrSetAsync<Payload>(key,
            Options with { EarlyRefreshBeta = double.MaxValue, DistributedSingleFlight = singleFlight }, async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return created;
        }, Ttl).AsTask();
        try
        {
            await entered.Task.WaitAsync(Limit);
            await client.DeleteAsync(key);
            if (replace)
                await client.GetOrSetAsync<Payload>(key, Options with { DistributedSingleFlight = false },
                    _ => ValueTask.FromResult<Payload?>(new() { Value = "winner" }), Ttl);
        }
        finally { release.TrySetResult(); }
        var result = await refresh.WaitAsync(Limit);
        await Assert.That(result!.Value).IsEqualTo(replace ? "winner" : "computed");
        created.Value = "mutated";
        await Assert.That(result.Value).IsEqualTo(replace ? "winner" : "computed");
        await Assert.That(await client.ExistsAsync(key)).IsEqualTo(replace);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FailedColdFillCannotRemoveSuccessorGeneration(bool singleFlight)
    {
        await using var client = await ConnectAsync();
        var key = NewKey();
        var lockKey = RespireClient.CreateCacheAsideLockKey(key);
        var options = Options with { DistributedSingleFlight = singleFlight };
        var entered = Signal();
        var release = Signal();
        var successorEntered = Signal();
        var successorRelease = Signal();
        var original = client.GetOrSetAsync<string>(key, options, async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return null;
        }, Ttl).AsTask();
        Task<string?>? successor = null;
        try
        {
            await entered.Task.WaitAsync(Limit);
            await client.DeleteAsync(key);
            await client.DeleteAsync(lockKey); // Simulate removal of the old lease before its owner completes.
            successor = client.GetOrSetAsync<string>(key, options, async token =>
            {
                successorEntered.TrySetResult();
                await successorRelease.Task.WaitAsync(token);
                return "successor";
            }, Ttl).AsTask();
            await successorEntered.Task.WaitAsync(Limit);
            using var generation = await client.ExecuteAsync("HGET", key, "version");
            await Assert.That(generation.IsNull).IsFalse();
            release.TrySetResult();
            await Assert.That(await original.WaitAsync(Limit)).IsNull();
            using var retained = await client.ExecuteAsync("HGET", key, "version");
            await Assert.That(retained.AsString()).IsEqualTo(generation.AsString());
            await Assert.That(await client.ExistsAsync(lockKey)).IsEqualTo(singleFlight);
        }
        finally
        {
            release.TrySetResult();
            successorRelease.TrySetResult();
            await original.WaitAsync(Limit);
            if (successor is not null) await successor.WaitAsync(Limit);
        }
        await Assert.That(await client.GetOrSetAsync<string>(key, options, _ => throw new Exception("hit"), Ttl))
            .IsEqualTo("successor");
    }

    [Test]
    public async Task LeaseScriptsPreserveUnrelatedClientCacheEntries()
    {
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with
        { Connections = 1, Protocol = RespireProtocol.Resp3, ClientSideCache = new() });
        var unrelated = NewKey();
        await client.SetAsync(unrelated, "cached");
        await client.GetStringAsync(unrelated);
        var hits = client.ClientSideCache!.GetStatistics().Hits;
        await client.GetOrSetAsync<string>(NewKey(), Options, async _ =>
        {
            // The acquisition fence must not evict an unrelated cached value.
            await Assert.That(await client.GetStringAsync(unrelated)).IsEqualTo("cached");
            return "published";
        }, Ttl);
        await client.GetStringAsync(unrelated);
        await client.GetOrSetAsync<string>(NewKey(), Options, _ => ValueTask.FromResult<string?>(null), Ttl);
        await client.GetStringAsync(unrelated);
        await Assert.That(client.ClientSideCache.GetStatistics().Hits).IsEqualTo(hits + 3);
    }

    public sealed class Payload { public string? Value { get; set; } }
    private sealed class RecordingHook : RespireCommandHook
    {
        internal readonly List<string> Commands = [];
        internal int Completed;
        public override object? OnExecuting(RespireCommandContext context)
        {
            Commands.Add(context.Command);
            return null;
        }
        public override void OnCompleted(RespireCommandContext context, object? state, RespireCommandOutcome outcome, Exception? error)
            => Completed++;
    }
    private static string NewKey() => $"stampede:{Guid.NewGuid():N}:";
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ValueTask<RespireClient> ConnectAsync() => RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with { Connections = 1 });
}
