using System.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.Keyed, Key = TestConstraints.ClientCacheServer)]
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
                    .Select(_ => client.GetOrSetAsync(key, Factory, Ttl, Options).AsTask())).ToArray();
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
        await client.SetAsync(lockKey, "crashed-process", TimeSpan.FromMilliseconds(150));
        var started = Stopwatch.GetTimestamp();
        var calls = 0;
        var result = await client.GetOrSetAsync<string>(key, _ =>
        {
            calls++;
            return ValueTask.FromResult<string?>("recovered");
        }, Ttl, Options).AsTask().WaitAsync(Limit);
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
        await Assert.That(async () => await client.GetOrSetAsync<string>(key, _ =>
        {
            calls++;
            return ValueTask.FromResult<string?>("unexpected");
        }, Ttl, Options with { WaitTimeout = TimeSpan.FromMilliseconds(60) }))
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
        await first.GetOrSetAsync<string>(key, _ => ValueTask.FromResult<string?>("old"), Ttl, options);
        // Move the logical freshness boundary into the past while retaining the stale window.
        await first.ExpireAsync(key, TimeSpan.FromSeconds(30));
        var entered = Signal();
        var release = Signal();
        var refresh = first.GetOrSetAsync<string>(key, async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return "new";
        }, Ttl, options).AsTask();
        try
        {
            await entered.Task.WaitAsync(Limit);
            var stale = await second.GetOrSetAsync<string>(key, _ => throw new Exception("contender factory"),
                Ttl, options).AsTask().WaitAsync(Limit);
            await Assert.That(stale).IsEqualTo("old");
            await Assert.That(refresh.IsCompleted).IsFalse();
        }
        finally { release.TrySetResult(); }
        await Assert.That(await refresh.WaitAsync(Limit)).IsEqualTo("new");
        await Assert.That(await second.GetOrSetAsync<string>(key, _ => throw new Exception("hit factory"), Ttl, options))
            .IsEqualTo("new");
    }

    [Test]
    public async Task EarlyRefreshUsesSharedRecomputeDurationAcrossClients()
    {
        await using var first = await ConnectAsync();
        await using var second = await ConnectAsync();
        var key = NewKey();
        await first.GetOrSetAsync<string>(key, _ => ValueTask.FromResult<string?>("old"), Ttl, Options);
        await second.GetOrSetAsync<string>(key, _ => throw new Exception("beta zero"), Ttl, Options);
        // Force the election for any Random.NextDouble sample in [0, 1).
        var refresh = Options with { EarlyRefreshBeta = double.MaxValue };
        await Assert.That(await second.GetOrSetAsync<string>(key, _ => ValueTask.FromResult<string?>("early"), Ttl, refresh))
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
        var pending = first.GetOrSetAsync<string>(key, async _ =>
        {
            entered.TrySetResult();
            await release.Task; // Deliberately ignore cancellation to exercise server fencing.
            return "obsolete";
        }, Ttl, Options).AsTask();
        try
        {
            await entered.Task.WaitAsync(Limit);
            await second.SetAsync(lockKey, "successor", Ttl);
        }
        finally { release.TrySetResult(); }
        await Assert.That(async () => await pending.WaitAsync(Limit)).ThrowsExactly<TimeoutException>();
        await Assert.That(await second.GetStringAsync(lockKey)).IsEqualTo("successor");
        await Assert.That(await second.ExistsAsync(key)).IsFalse();
    }

    [Test]
    public async Task FactoryDeadlineSafelyCancelsThrowingCallbacksAndAllowsRecovery()
    {
        await using var client = await ConnectAsync();
        var key = NewKey();
        await Assert.That(async () => await client.GetOrSetAsync<string>(key, async token =>
        {
            using var registration = token.Register(static () => throw new InvalidOperationException("callback"));
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "never";
        }, Ttl, Options with { LockTimeout = TimeSpan.FromMilliseconds(100) }))
            .Throws<OperationCanceledException>();
        await Assert.That(await client.ExistsAsync(RespireClient.CreateCacheAsideLockKey(key))).IsFalse();
        await Assert.That(await client.GetOrSetAsync<string>(key, _ => ValueTask.FromResult<string?>("recovered"), Ttl, Options))
            .IsEqualTo("recovered");
    }

    [Test]
    public async Task FactoryFailureNullAndCancellationReleaseTheLease()
    {
        await using var client = await ConnectAsync();
        var key = NewKey();
        var lockKey = RespireClient.CreateCacheAsideLockKey(key);
        await Assert.That(async () => await client.GetOrSetAsync<string>(key,
            _ => throw new InvalidOperationException("factory"), Ttl, Options)).ThrowsExactly<InvalidOperationException>();
        await Assert.That(await client.ExistsAsync(lockKey)).IsFalse();
        await Assert.That(await client.GetOrSetAsync<string>(key, _ => ValueTask.FromResult<string?>(null), Ttl, Options)).IsNull();
        await Assert.That(await client.ExistsAsync(key)).IsFalse();
        await Assert.That(await client.ExistsAsync(lockKey)).IsFalse();
        using var canceled = new CancellationTokenSource();
        var entered = Signal();
        var pending = client.GetOrSetAsync<string>(key, async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "never";
        }, Ttl, Options, canceled.Token).AsTask();
        await entered.Task.WaitAsync(Limit);
        canceled.Cancel();
        await Assert.That(async () => await pending.WaitAsync(Limit)).Throws<OperationCanceledException>();
        await Assert.That(await client.ExistsAsync(lockKey)).IsFalse();
    }

    [Test]
    public async Task CancelingAWaiterDoesNotCancelTheHolder()
    {
        await using var client = await ConnectAsync();
        var key = NewKey();
        var entered = Signal();
        var release = Signal();
        var holder = client.GetOrSetAsync<string>(key, async token =>
        {
            entered.TrySetResult(); await release.Task.WaitAsync(token); return "holder";
        }, Ttl, Options).AsTask();
        try
        {
            await entered.Task.WaitAsync(Limit);
            using var canceled = new CancellationTokenSource();
            var waiter = client.GetOrSetAsync<string>(key, _ => throw new Exception("waiter factory"),
                Ttl, Options, canceled.Token).AsTask();
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
        await client.GetOrSetAsync<string>(key, async token =>
        {
            await client.PingAsync(token);
            return "value";
        }, Ttl, Options);
        await Assert.That(string.Join(",", hook.Commands)).IsEqualTo("GETORSET,PING");
        await Assert.That(hook.Completed).IsEqualTo(2);
        hook.Commands.Clear();
        await client.GetOrSetAsync<string>(key, _ => throw new Exception("hit"), Ttl, Options);
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
        var first = await view.GetOrSetAsync<Payload>("key", _ => ValueTask.FromResult<Payload?>(created), Ttl, Options);
        created.Value = "factory mutation";
        first!.Value = "caller mutation";
        var second = await client.GetOrSetAsync<Payload>(prefix + "key", _ => throw new Exception("hit"), Ttl, Options);
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
        await Assert.That(await client.GetOrSetAsync<int>(key, _ => ValueTask.FromResult(42), deadline, options)).IsEqualTo(42);
        if (past) await Assert.That(await client.ExistsAsync(key)).IsFalse();
        else await Assert.That((await client.Keys.ExpiryTimeAsync(key)).UnixTimeMilliseconds).IsEqualTo(deadline.ToUnixTimeMilliseconds());
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
