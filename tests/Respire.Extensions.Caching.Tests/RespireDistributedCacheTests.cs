using System.Text;
using Microsoft.Extensions.Caching.Distributed;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Extensions.Caching.Tests;

[ClassDataSource<RedisTestFixture>(Shared = SharedType.PerClass)]
[NotInParallel("redis-integration")]
public class RespireDistributedCacheTests(RedisTestFixture fixture)
{
    private RespireClient? _client;
    private RespireDistributedCache? _cache;

    private RespireClient Client => _client!;
    private RespireDistributedCache Cache => _cache!;

    [Before(Test)]
    public async Task InitializeAsync()
    {
        _client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        (await Client.ExecuteAsync("FLUSHDB")).Dispose();
        _cache = new RespireDistributedCache(_client);
    }

    [After(Test)]
    public async ValueTask DisposeAsync()
    {
        if (_cache is not null)
        {
            await Cache.DisposeAsync();
        }

        if (_client is not null)
        {
            await Client.DisposeAsync();
        }
    }

    private async Task<long> PttlAsync(string key)
    {
        using var result = await Client.ExecuteAsync("PTTL", key);
        return result.AsInteger();
    }

    [Test]
    public async Task SetAndGet_RoundTripsBytes()
    {
        var value = Encoding.UTF8.GetBytes("hello distributed cache");

        await Cache.SetAsync("roundtrip", value, new DistributedCacheEntryOptions());
        var fetched = await Cache.GetAsync("roundtrip");

        await Assert.That(fetched).IsNotNull();
        await Assert.That(fetched!.SequenceEqual(value)).IsTrue();
    }

    [Test]
    public async Task Get_MissingKey_ReturnsNull()
    {
        var fetched = await Cache.GetAsync("does-not-exist");

        await Assert.That(fetched).IsNull();
    }

    [Test]
    public async Task Remove_DeletesEntry()
    {
        await Cache.SetAsync("removable", [1, 2, 3], new DistributedCacheEntryOptions());

        await Cache.RemoveAsync("removable");

        await Assert.That(await Cache.GetAsync("removable")).IsNull();
    }

    [Test]
    public async Task Set_WithoutExpiration_HasNoTtl()
    {
        await Cache.SetAsync("no-ttl", [42], new DistributedCacheEntryOptions());

        await Assert.That(await PttlAsync("no-ttl")).IsEqualTo(-1);
    }

    [Test]
    public async Task Overwrite_WithoutExpiration_ClearsPreviousTtl()
    {
        var options = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) };
        await Cache.SetAsync("clear-ttl", [1], options);
        await Assert.That(await PttlAsync("clear-ttl")).IsGreaterThan(0);

        await Cache.SetAsync("clear-ttl", [2], new DistributedCacheEntryOptions());

        await Assert.That(await PttlAsync("clear-ttl")).IsEqualTo(-1);
    }

    [Test]
    public async Task AbsoluteExpirationRelativeToNow_ExpiresEntry()
    {
        var options = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(1) };
        await Cache.SetAsync("short-lived", [1], options);

        await Assert.That(await Cache.GetAsync("short-lived")).IsNotNull();

        await Task.Delay(TimeSpan.FromSeconds(2));
        await Assert.That(await Cache.GetAsync("short-lived")).IsNull();
    }

    [Test]
    public async Task AbsoluteExpiration_InThePast_Throws()
    {
        var options = new DistributedCacheEntryOptions { AbsoluteExpiration = DateTimeOffset.UtcNow.AddMinutes(-1) };

        var threw = false;
        try
        {
            await Cache.SetAsync("past", [1], options);
        }
        catch (ArgumentOutOfRangeException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task StaleAbsoluteExpiration_IsIgnoredWhenRelativeTakesPrecedence()
    {
        // A reused options instance can carry a passed AbsoluteExpiration alongside a later,
        // valid AbsoluteExpirationRelativeToNow; the relative value wins, so the dead absolute
        // must not be validated.
        var options = new DistributedCacheEntryOptions
        {
            AbsoluteExpiration = DateTimeOffset.UtcNow.AddMinutes(-1),
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5),
        };

        await Cache.SetAsync("stale-absolute", [1], options);

        await Assert.That(await Cache.GetAsync("stale-absolute")).IsNotNull();
        var pttl = await PttlAsync("stale-absolute");
        await Assert.That(pttl).IsGreaterThan(0);
        await Assert.That(pttl).IsLessThanOrEqualTo((long)TimeSpan.FromMinutes(5).TotalMilliseconds);
    }

    [Test]
    public async Task SlidingExpiration_GetExtendsLife()
    {
        var options = new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromSeconds(2) };
        await Cache.SetAsync("sliding", [7], options);

        // Total elapsed exceeds the sliding window, but each read re-arms it.
        for (var i = 0; i < 3; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            await Assert.That(await Cache.GetAsync("sliding")).IsNotNull();
        }

        // Untouched past the window, the entry dies.
        await Task.Delay(TimeSpan.FromSeconds(3));
        await Assert.That(await Cache.GetAsync("sliding")).IsNull();
    }

    [Test]
    public async Task Refresh_ExtendsSlidingEntry()
    {
        var options = new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromSeconds(2) };
        await Cache.SetAsync("refreshable", [9], options);

        await Task.Delay(TimeSpan.FromSeconds(1.2));
        await Cache.RefreshAsync("refreshable");
        await Task.Delay(TimeSpan.FromSeconds(1.2));

        await Assert.That(await Cache.GetAsync("refreshable")).IsNotNull();
    }

    [Test]
    public async Task SlidingExpiration_AbsoluteCapWins()
    {
        var options = new DistributedCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromSeconds(2),
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(3),
        };
        await Cache.SetAsync("capped", [5], options);

        // Keep touching well within the sliding window; the absolute cap must still kill it.
        for (var i = 0; i < 7; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(600));
            await Cache.GetAsync("capped");
        }

        await Assert.That(await Cache.GetAsync("capped")).IsNull();
    }

    [Test]
    public async Task InstanceName_IsolatesCachesAndPrefixesKeys()
    {
        var cacheA = new RespireDistributedCache(Client, new RespireCacheOptions { InstanceName = "a:" });
        var cacheB = new RespireDistributedCache(Client, new RespireCacheOptions { InstanceName = "b:" });

        await cacheA.SetAsync("shared-key", [1], new DistributedCacheEntryOptions());

        await Assert.That(await cacheA.GetAsync("shared-key")).IsNotNull();
        await Assert.That(await cacheB.GetAsync("shared-key")).IsNull();
        await Assert.That(await Client.ExistsAsync("a:shared-key")).IsTrue();
    }

    [Test]
    public async Task SyncApis_RoundTrip()
    {
        var value = new byte[] { 10, 20, 30 };
        var options = new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(1) };

        await Task.Run(() =>
        {
            Cache.Set("sync-key", value, options);
            var fetched = Cache.Get("sync-key");
            if (fetched is null || !fetched.SequenceEqual(value))
            {
                throw new InvalidOperationException("Sync Get returned a different value than Set stored.");
            }

            Cache.Refresh("sync-key");
            Cache.Remove("sync-key");
        });

        await Assert.That(await Cache.GetAsync("sync-key")).IsNull();
    }

    [Test]
    public async Task RemoveAsync_PreCanceledToken_ThrowsAndLeavesEntry()
    {
        await Cache.SetAsync("cancel-remove", [1], new DistributedCacheEntryOptions());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var threw = false;
        try
        {
            await Cache.RemoveAsync("cancel-remove", cts.Token);
        }
        catch (OperationCanceledException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
        await Assert.That(await Cache.GetAsync("cancel-remove")).IsNotNull();
    }

    // The next tests exercise the correction a delayed set triggers (a send that reached Redis
    // only after a stall — lazy first connect, reconnect — arms a TTL computed before the delay).
    // The delay itself cannot be reproduced through the public API, so they execute the
    // production scripts directly: first the set script with the stale TTL a delayed sender
    // would arm, then the correction with the remainder it would re-derive after the reply.

    private async Task RunDelayedSetAsync(string key, long absexp, long staleTtlMs, long sldexp = -1L)
        => (await Client.Scripts.ExecuteAsync(
            RespireDistributedCache.SetScript, [key],
            [absexp, sldexp, staleTtlMs, new byte[] { 1 }])).Dispose();

    private async Task RunTtlCorrectionAsync(string key, long absexp, long remainingMs, long sldexp = -1L)
        => (await Client.Scripts.ExecuteAsync(
            RespireDistributedCache.CapTtlScript, [key], [absexp, sldexp, remainingMs])).Dispose();

    private async Task RunRefreshCorrectionAsync(string key, long nowTicks)
        => (await Client.Scripts.ExecuteAsync(
            RespireDistributedCache.CapRefreshedTtlScript, [key], [nowTicks])).Dispose();

    [Test]
    public async Task DelayedSet_DeadlinePassedInTransit_CorrectionRemovesEntry()
    {
        var absexp = DateTimeOffset.UtcNow.AddSeconds(30).UtcTicks;
        await RunDelayedSetAsync("dead-on-arrival", absexp, staleTtlMs: 30_000);

        // After the reply the sender found the deadline already behind it.
        await RunTtlCorrectionAsync("dead-on-arrival", absexp, remainingMs: -100);

        await Assert.That(await Cache.GetAsync("dead-on-arrival")).IsNull();
    }

    [Test]
    public async Task DelayedSet_StaleTtlIsShrunkToTheRemainder()
    {
        // The sender computed a 5-minute TTL before the stall; only ~5s of deadline remained
        // once the reply arrived.
        var absexp = DateTimeOffset.UtcNow.AddMinutes(5).UtcTicks;
        await RunDelayedSetAsync("stale-ttl", absexp, staleTtlMs: 300_000);

        await RunTtlCorrectionAsync("stale-ttl", absexp, remainingMs: 5_000);

        var pttl = await PttlAsync("stale-ttl");
        await Assert.That(pttl).IsGreaterThan(0);
        await Assert.That(pttl).IsLessThanOrEqualTo(5_000);
    }

    [Test]
    public async Task DelayedSet_CorrectionNeverExtendsTheTtl()
    {
        var absexp = DateTimeOffset.UtcNow.AddSeconds(5).UtcTicks;
        await RunDelayedSetAsync("shrink-only", absexp, staleTtlMs: 5_000);

        // A correction computed from an even staler clock must not push the TTL out.
        await RunTtlCorrectionAsync("shrink-only", absexp, remainingMs: 300_000);

        await Assert.That(await PttlAsync("shrink-only")).IsLessThanOrEqualTo(5_000);
    }

    [Test]
    public async Task DelayedSet_CorrectionSkipsAConcurrentlyOverwrittenEntry()
    {
        // Another writer overwrote the key (different absexp) between the delayed set and its
        // correction; the correction must leave the newer entry untouched.
        var staleAbsexp = DateTimeOffset.UtcNow.AddSeconds(30).UtcTicks;
        var newerAbsexp = DateTimeOffset.UtcNow.AddMinutes(5).UtcTicks;
        await RunDelayedSetAsync("overwritten", newerAbsexp, staleTtlMs: 300_000);

        await RunTtlCorrectionAsync("overwritten", staleAbsexp, remainingMs: -100);

        await Assert.That(await Cache.GetAsync("overwritten")).IsNotNull();
        await Assert.That(await PttlAsync("overwritten")).IsGreaterThan(250_000);
    }

    [Test]
    public async Task DelayedSet_CorrectionSkipsAnEntryWithADifferentSlidingWindow()
    {
        // Both writers chose the same explicit absolute deadline, but the newer entry slides
        // over a minute while the delayed one slid over 5s; matching on absexp alone would let
        // the old correction shrink the newer entry to its smaller window.
        var absexp = DateTimeOffset.UtcNow.AddMinutes(5).UtcTicks;
        var newerSliding = TimeSpan.FromMinutes(1).Ticks;
        var oldSliding = TimeSpan.FromSeconds(5).Ticks;
        await RunDelayedSetAsync("resliding", absexp, staleTtlMs: 60_000, sldexp: newerSliding);

        await RunTtlCorrectionAsync("resliding", absexp, remainingMs: 5_000, sldexp: oldSliding);

        await Assert.That(await PttlAsync("resliding")).IsGreaterThan(55_000);
    }

    // Same simulation approach for the read path: a sliding read whose send stalled re-arms
    // the TTL from a "now" that was sampled before the stall. The tests run the production
    // get script with that stale timestamp, then the correction with a fresh one.

    private async Task RunDelayedRefreshAsync(string key, long staleNowTicks)
        => (await Client.Scripts.ExecuteAsync(
            RespireDistributedCache.GetAndRefreshScript, [key], ["0", staleNowTicks])).Dispose();

    [Test]
    public async Task GetScript_FlagsOnlyAbsoluteCappedSlidingReArms()
    {
        // Only a sliding re-arm with an absolute deadline in play can be over-extended by a
        // stale "now", so only that shape may trigger the correction round trip.
        var now = DateTimeOffset.UtcNow;
        var sliding = TimeSpan.FromSeconds(30).Ticks;
        await RunDelayedSetAsync("both", now.AddMinutes(5).UtcTicks, staleTtlMs: 30_000, sldexp: sliding);
        await RunDelayedSetAsync("sliding-only", -1L, staleTtlMs: 30_000, sldexp: sliding);
        await RunDelayedSetAsync("absolute-only", now.AddMinutes(5).UtcTicks, staleTtlMs: 300_000);

        using var both = await Client.Scripts.ExecuteAsync(
            RespireDistributedCache.GetAndRefreshScript, ["both"], ["1", now.UtcTicks]);
        using var slidingOnly = await Client.Scripts.ExecuteAsync(
            RespireDistributedCache.GetAndRefreshScript, ["sliding-only"], ["1", now.UtcTicks]);
        using var absoluteOnly = await Client.Scripts.ExecuteAsync(
            RespireDistributedCache.GetAndRefreshScript, ["absolute-only"], ["1", now.UtcTicks]);

        await Assert.That(both[0].AsInteger()).IsEqualTo(1);
        await Assert.That(slidingOnly[0].AsInteger()).IsEqualTo(0);
        await Assert.That(absoluteOnly[0].AsInteger()).IsEqualTo(0);
        await Assert.That(both[1].AsBytes()).IsEquivalentTo(new byte[] { 1 });
    }

    [Test]
    public async Task DelayedRefresh_StaleReArmIsShrunkToTheRemainder()
    {
        // 10s of deadline left, 30s sliding window. A refresh queued a minute "ago" re-arms
        // the full window, stretching the entry ~20s past its deadline.
        var now = DateTimeOffset.UtcNow;
        var absexp = now.AddSeconds(10).UtcTicks;
        var sliding = TimeSpan.FromSeconds(30).Ticks;
        await RunDelayedSetAsync("stale-refresh", absexp, staleTtlMs: 10_000, sldexp: sliding);
        await RunDelayedRefreshAsync("stale-refresh", now.AddMinutes(-1).UtcTicks);
        await Assert.That(await PttlAsync("stale-refresh")).IsGreaterThan(10_000);

        await RunRefreshCorrectionAsync("stale-refresh", DateTimeOffset.UtcNow.UtcTicks);

        var pttl = await PttlAsync("stale-refresh");
        await Assert.That(pttl).IsGreaterThan(0);
        await Assert.That(pttl).IsLessThanOrEqualTo(10_000);
    }

    [Test]
    public async Task DelayedRefresh_CorrectionNeverExtendsTheTtl()
    {
        // A correction computed from an even staler clock sees a large remainder; the PTTL
        // guard must keep it from pushing the TTL out.
        var now = DateTimeOffset.UtcNow;
        var absexp = now.AddSeconds(5).UtcTicks;
        var sliding = TimeSpan.FromSeconds(30).Ticks;
        await RunDelayedSetAsync("no-extend", absexp, staleTtlMs: 5_000, sldexp: sliding);

        await RunRefreshCorrectionAsync("no-extend", now.AddMinutes(-1).UtcTicks);

        await Assert.That(await PttlAsync("no-extend")).IsLessThanOrEqualTo(5_000);
    }

    [Test]
    public async Task DelayedRefresh_CorrectionNeverDeletesASkewedEntry()
    {
        // A Microsoft writer's negative clock offset makes absexp read as already past on a
        // live key. The correction must skip — not unlink — and let the write-time TTL govern.
        var absexp = DateTimeOffset.UtcNow.AddHours(-1).UtcTicks;
        var sliding = TimeSpan.FromSeconds(30).Ticks;
        await RunDelayedSetAsync("skewed", absexp, staleTtlMs: 10_000, sldexp: sliding);

        await RunRefreshCorrectionAsync("skewed", DateTimeOffset.UtcNow.UtcTicks);

        await Assert.That(await Cache.GetAsync("skewed")).IsNotNull();
        var pttl = await PttlAsync("skewed");
        await Assert.That(pttl).IsGreaterThan(0);
        await Assert.That(pttl).IsLessThanOrEqualTo(10_000);
    }

    [Test]
    public async Task NullKey_Throws()
    {
        var threw = false;
        try
        {
            await Cache.GetAsync(null!);
        }
        catch (ArgumentNullException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }

    // The next tests pin down how the cache behaves when RespireOptions.CommandTimeout abandons
    // a wait whose command is still queued. The server is stalled deterministically with a Lua
    // busy-loop (ARGV[1] seconds), so a 50ms command timeout always fires first while the queued
    // command still executes once the stall ends.

    private const string StallScriptSource = """
        local t = redis.call('TIME')
        local deadline = tonumber(t[1]) + (tonumber(t[2]) / 1000000) + tonumber(ARGV[1])
        repeat
          t = redis.call('TIME')
        until tonumber(t[1]) + (tonumber(t[2]) / 1000000) >= deadline
        return 1
        """;

    private async Task<RespireClient> ConnectTimeoutClientAsync()
        => await RespireClient.ConnectAsync(
            RespireOptions.Parse(fixture.ConnectionString) with { CommandTimeout = TimeSpan.FromMilliseconds(50) });

    private Task StallServerAsync(string seconds)
        => Task.Run(async () => (await Client.ExecuteAsync("EVAL", StallScriptSource, "0", seconds)).Dispose());

    [Test]
    public async Task Remove_TimedOutWait_FailsBounded_AndTheLatentUnlinkCannotDeleteAReplacement()
    {
        await Cache.SetAsync("timeout-remove", [1], new DistributedCacheEntryOptions());
        await using var timeoutClient = await ConnectTimeoutClientAsync();
        await using var timeoutCache = new RespireDistributedCache(timeoutClient);

        // Warm the dedicated pool while the server is responsive, so the stalled removal below
        // fails in the reply wait (the fenced UNLINK already on the wire), not during
        // connection setup.
        await timeoutCache.RemoveAsync("warmup");

        var stallObserved = StallServerAsync("0.5");
        await Task.Delay(100);

        var threw = false;
        try
        {
            await timeoutCache.RemoveAsync("timeout-remove");
        }
        catch (RespireTimeoutException)
        {
            threw = true;
        }

        // The wait is bounded — a wedged server cannot hang removal forever — and the failure
        // surfaced only after the fence write round-tripped, so the flushed removal script is
        // ordered behind the fence from the caller's viewpoint: whenever it drains, it either
        // already ran (before the failure was reported, deleting only the old value) or sees
        // the fence and refuses to delete. A replacement written after the observed failure
        // must therefore survive the stall clearing.
        await Assert.That(threw).IsTrue();
        await Cache.SetAsync("timeout-remove", [2], new DistributedCacheEntryOptions());

        await stallObserved;
        await Task.Delay(200);

        await timeoutCache.RemoveAsync("proves-the-pool-recovered");
        var survivor = await Cache.GetAsync("timeout-remove");
        await Assert.That(survivor).IsNotNull();
        await Assert.That(survivor!.SequenceEqual(new byte[] { 2 })).IsTrue();
    }

    [Test]
    public async Task Remove_FencedScript_DeletesOnlyWhileUnfenced()
    {
        // The production removal script executed both ways: with its fence present the delete
        // must no-op (the latent-copy case), without it the delete must run.
        await Cache.SetAsync("fenced", [1], new DistributedCacheEntryOptions());
        (await Client.ExecuteAsync("SET", "fence-key", "1")).Dispose();

        (await Client.Scripts.ExecuteAsync(
            RespireClient.FencedUnlinkScript, ["fenced", "fence-key"])).Dispose();
        await Assert.That(await Cache.GetAsync("fenced")).IsNotNull();

        (await Client.Scripts.ExecuteAsync(
            RespireClient.FencedUnlinkScript, ["fenced", "absent-fence"])).Dispose();
        await Assert.That(await Cache.GetAsync("fenced")).IsNull();
    }

    [Test]
    public async Task Set_TimedOutWait_StillStoresTheQueuedEntry()
    {
        // Preload so the timed-out EVALSHA cannot fall into an unobserved NOSCRIPT.
        await Client.Scripts.LoadAsync(RespireDistributedCache.SetScript);
        await using var timeoutClient = await ConnectTimeoutClientAsync();
        await using var timeoutCache = new RespireDistributedCache(timeoutClient);

        var stallObserved = StallServerAsync("0.5");
        await Task.Delay(100);

        var threw = false;
        try
        {
            await timeoutCache.SetAsync("timeout-set", [1],
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });
        }
        catch (RespireTimeoutException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
        await stallObserved;

        // The abandoned wait did not abandon the queued set; the correction the catch sent is
        // FIFO-ordered after it and leaves the TTL no larger than the true remainder.
        await Assert.That(await Cache.GetAsync("timeout-set")).IsNotNull();
        var pttl = await PttlAsync("timeout-set");
        await Assert.That(pttl).IsGreaterThan(0);
        await Assert.That(pttl).IsLessThanOrEqualTo((long)TimeSpan.FromMinutes(5).TotalMilliseconds);
    }

    [Test]
    public async Task Get_TimedOutWait_CorrectsWithoutExtendingTheTtl()
    {
        await Cache.SetAsync("timeout-get", [1], new DistributedCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromSeconds(30),
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5),
        });
        await Client.Scripts.LoadAsync(RespireDistributedCache.GetAndRefreshScript);
        await using var timeoutClient = await ConnectTimeoutClientAsync();
        await using var timeoutCache = new RespireDistributedCache(timeoutClient);

        var stallObserved = StallServerAsync("0.5");
        await Task.Delay(100);

        var threw = false;
        try
        {
            await timeoutCache.GetAsync("timeout-get");
        }
        catch (RespireTimeoutException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
        await stallObserved;

        // The queued read re-armed the sliding window; the correction the catch sent must have
        // left the TTL within it.
        await Assert.That(await Cache.GetAsync("timeout-get")).IsNotNull();
        var pttl = await PttlAsync("timeout-get");
        await Assert.That(pttl).IsGreaterThan(0);
        await Assert.That(pttl).IsLessThanOrEqualTo((long)TimeSpan.FromSeconds(30).TotalMilliseconds);
    }

    [Test]
    public async Task Set_TimedOutWait_CorrectionChasesTheStall_UntilTheRemainderIsFresh()
    {
        await Client.Scripts.LoadAsync(RespireDistributedCache.SetScript);
        await using var timeoutClient = await ConnectTimeoutClientAsync();
        await using var timeoutCache = new RespireDistributedCache(timeoutClient);

        // A stall longer than SendDelayTolerance: the first correction pass chases the queued
        // set through it, so its remainder is stale by more than the tolerance and a second
        // pass with a freshly derived remainder must run.
        var stallObserved = StallServerAsync("1.5");
        await Task.Delay(100);

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        var threw = false;
        try
        {
            await timeoutCache.SetAsync("chased-set", [1],
                new DistributedCacheEntryOptions { AbsoluteExpiration = deadline });
        }
        catch (RespireTimeoutException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
        await stallObserved;

        // A single stale pass would have left the TTL near the full 10s, letting the entry
        // outlive its deadline by the stall; the re-derived pass pins it to the true remainder.
        await Assert.That(await Cache.GetAsync("chased-set")).IsNotNull();
        var pttl = await PttlAsync("chased-set");
        var remaining = (long)(deadline - DateTimeOffset.UtcNow).TotalMilliseconds;
        await Assert.That(pttl).IsGreaterThan(0);
        await Assert.That(pttl).IsLessThanOrEqualTo(remaining + 500);
    }

    [Test]
    public async Task Correction_BroadcastReachesEveryConnection_AndShrinksTheTtl()
    {
        // Round-robin makes the connection that carried a delayed set unknowable, so the
        // correction is sent on all of them; on a multi-connection client the one sharing the
        // set's connection must still shrink the stale TTL.
        await using var multiClient = await RespireClient.ConnectAsync(
            RespireOptions.Parse(fixture.ConnectionString) with { Connections = 3 });

        var absexp = DateTimeOffset.UtcNow.AddSeconds(30).UtcTicks;
        await RunDelayedSetAsync("broadcast-cap", absexp, staleTtlMs: 120_000);

        await multiClient.ExecuteOnAllConnectionsAsync(
            RespireDistributedCache.CapTtlScript, ["broadcast-cap"], [absexp, -1L, 30_000L]);

        var pttl = await PttlAsync("broadcast-cap");
        await Assert.That(pttl).IsGreaterThan(0);
        await Assert.That(pttl).IsLessThanOrEqualTo(30_000);
    }

    [Test]
    public async Task Set_SustainedLatency_CorrectionStopsAfterBoundedPasses()
    {
        // Latency that never clears: every script round trip exceeds SendDelayTolerance, so a
        // "retry until one pass is fresh" loop would never return. A pass that fails to improve
        // on the previous one proves the floor is the latency itself, and the correction must
        // stop there instead of livelocking the set.
        var slowClient = new ScriptInterceptingClient(Client, async (_, send) =>
        {
            var result = await send();
            await Task.Delay(TimeSpan.FromMilliseconds(1200));
            return result;
        });
        await using var slowCache = new RespireDistributedCache(slowClient);

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        await slowCache.SetAsync("sustained", [1],
                new DistributedCacheEntryOptions { AbsoluteExpiration = deadline })
            .WaitAsync(TimeSpan.FromSeconds(15));

        // One set, then exactly two correction passes: the first is allowed to chase, the
        // second round-trips no better and ends the loop.
        await Assert.That(slowClient.ScriptCalls).IsEqualTo(3);

        // Stopping early must not abandon the correction's job: the TTL is pinned to the
        // remainder, stale by at most one round trip.
        var pttl = await PttlAsync("sustained");
        var remaining = (long)(deadline - DateTimeOffset.UtcNow).TotalMilliseconds;
        await Assert.That(pttl).IsGreaterThan(0);
        await Assert.That(pttl).IsLessThanOrEqualTo(remaining + 2_000);
    }

    [Test]
    public async Task Set_CorrectionReplyNeverArrives_WaitIsAbandonedAndSetCompletes()
    {
        // A blackholed correction: the set's own reply is slow enough to trigger the
        // correction, whose pass then never completes. The pass wait must be bounded — the
        // queued shrink-only pass can land in the background, but the caller must not hang.
        var blackholeClient = new ScriptInterceptingClient(Client, async (call, send) =>
        {
            if (call > 1)
            {
                await new TaskCompletionSource().Task;
            }

            var result = await send();
            await Task.Delay(TimeSpan.FromMilliseconds(1200));
            return result;
        });
        await using var blackholeCache = new RespireDistributedCache(blackholeClient)
        {
            CorrectionWaitBound = TimeSpan.FromSeconds(2),
        };

        await blackholeCache.SetAsync("blackholed", [1],
                new DistributedCacheEntryOptions { AbsoluteExpiration = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30) })
            .WaitAsync(TimeSpan.FromSeconds(10));

        // The set, then a single correction pass whose wait was abandoned — no retries pile up
        // behind the same wedge. The entry itself was stored before the correction hung.
        await Assert.That(blackholeClient.ScriptCalls).IsEqualTo(2);
        await Assert.That(await Cache.GetAsync("blackholed")).IsNotNull();
    }

    /// <summary>
    /// Delegates to a real client but routes every script call through the test's interceptor
    /// (called with the 1-based call number and the real send). Deliberately not a
    /// RespireClient, so the cache degrades to the single-send correction path.
    /// </summary>
    private sealed class ScriptInterceptingClient(
        RespireClient inner,
        Func<int, Func<ValueTask<RespireResult>>, ValueTask<RespireResult>> onScript) : IRespireClient
    {
        private int _scriptCalls;

        public int ScriptCalls => _scriptCalls;

        public IScriptCommands Scripts => new InterceptedScripts(this, inner.Scripts);

        private sealed class InterceptedScripts(ScriptInterceptingClient owner, IScriptCommands inner) : IScriptCommands
        {
            public ValueTask<RespireResult> ExecuteAsync(
                RespireScript script, RespireKey[]? keys = null, RespireValue[]? args = null,
                CancellationToken cancellationToken = default)
            {
                var call = Interlocked.Increment(ref owner._scriptCalls);
                return owner._onScript(call, () => inner.ExecuteAsync(script, keys, args, cancellationToken));
            }

            public ValueTask<string> LoadAsync(RespireScript script, CancellationToken cancellationToken = default)
                => inner.LoadAsync(script, cancellationToken);
        }

        private readonly Func<int, Func<ValueTask<RespireResult>>, ValueTask<RespireResult>> _onScript = onScript;

        public RespireEndpoint Endpoint => inner.Endpoint;
        public bool IsConnected => inner.IsConnected;

        public event Action<RespireConnectionState>? ConnectionStateChanged
        {
            add => inner.ConnectionStateChanged += value;
            remove => inner.ConnectionStateChanged -= value;
        }

        public IStringCommands Strings => inner.Strings;
        public IKeyCommands Keys => inner.Keys;
        public IHashCommands Hashes => inner.Hashes;
        public IListCommands Lists => inner.Lists;
        public ISetCommands Sets => inner.Sets;
        public ISortedSetCommands SortedSets => inner.SortedSets;
        public IStreamCommands Streams => inner.Streams;
        public IServerCommands Server => inner.Server;

        public ValueTask<string?> GetStringAsync(RespireKey key, CancellationToken cancellationToken = default)
            => inner.GetStringAsync(key, cancellationToken);

        public ValueTask<T?> GetAsync<T>(RespireKey key, CancellationToken cancellationToken = default)
            => inner.GetAsync<T>(key, cancellationToken);

        public ValueTask<byte[]?> GetBytesAsync(RespireKey key, CancellationToken cancellationToken = default)
            => inner.GetBytesAsync(key, cancellationToken);

        public ValueTask<bool> SetAsync(
            RespireKey key, RespireValue value, TimeSpan? expiry = null, SetWhen when = SetWhen.Always,
            bool keepTtl = false, CancellationToken cancellationToken = default)
            => inner.SetAsync(key, value, expiry, when, keepTtl, cancellationToken);

        public ValueTask<bool> SetAsync<T>(
            RespireKey key, T value, TimeSpan? expiry = null, SetWhen when = SetWhen.Always,
            bool keepTtl = false, CancellationToken cancellationToken = default)
            => inner.SetAsync(key, value, expiry, when, keepTtl, cancellationToken);

        public ValueTask<long> DeleteAsync(params ReadOnlySpan<RespireKey> keys) => inner.DeleteAsync(keys);

        public ValueTask<bool> ExistsAsync(RespireKey key, CancellationToken cancellationToken = default)
            => inner.ExistsAsync(key, cancellationToken);

        public ValueTask<long> IncrementAsync(RespireKey key, long by = 1, CancellationToken cancellationToken = default)
            => inner.IncrementAsync(key, by, cancellationToken);

        public ValueTask<long> DecrementAsync(RespireKey key, long by = 1, CancellationToken cancellationToken = default)
            => inner.DecrementAsync(key, by, cancellationToken);

        public ValueTask<bool> ExpireAsync(RespireKey key, TimeSpan expiry, CancellationToken cancellationToken = default)
            => inner.ExpireAsync(key, expiry, cancellationToken);

        public ValueTask<TimeSpan> PingAsync(CancellationToken cancellationToken = default)
            => inner.PingAsync(cancellationToken);

        public ValueTask<long> PublishAsync(string channel, RespireValue message, CancellationToken cancellationToken = default)
            => inner.PublishAsync(channel, message, cancellationToken);

        public ValueTask<long> PublishShardedAsync(string channel, RespireValue message, CancellationToken cancellationToken = default)
            => inner.PublishShardedAsync(channel, message, cancellationToken);

        public RespireSubscription Subscribe(params string[] channels) => inner.Subscribe(channels);
        public RespireSubscription SubscribePattern(params string[] patterns) => inner.SubscribePattern(patterns);
        public RespireSubscription SubscribeSharded(params string[] channels) => inner.SubscribeSharded(channels);

        public RespireBatch CreateBatch() => inner.CreateBatch();
        public RespireTransaction CreateTransaction() => inner.CreateTransaction();

        public ValueTask<RespireTransaction> CreateTransactionAsync(RespireKey[] watchKeys, CancellationToken cancellationToken = default)
            => inner.CreateTransactionAsync(watchKeys, cancellationToken);

        public ValueTask<RespireResult> ExecuteAsync(string command, params RespireValue[] args)
            => inner.ExecuteAsync(command, args);

        public ValueTask<RespireResult> ExecuteAsync(
            RespireCommandInterpolatedStringHandler command, CancellationToken cancellationToken = default)
            => inner.ExecuteAsync(command, cancellationToken);

        public IRespireClient WithKeyPrefix(string prefix) => inner.WithKeyPrefix(prefix);

        // The test owns the wrapped client's lifetime.
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
