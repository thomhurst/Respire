using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Coordination.Tests;

public class RateLimitDenyListCacheTests
{
    [Test]
    public async Task CacheHasFixedExpiryAndUpdatesInvalidateBothTransitions()
    {
        var time = new ManualTime();
        var denied = false;
        var reads = 0;
        var list = new RespireRateLimitDenyList((_, _) => { reads++; return ValueTask.FromResult(denied); },
            (_, value, _) => { denied = value; return ValueTask.CompletedTask; }, time, TimeSpan.FromSeconds(1), 2);
        await Assert.That(await list.IsDeniedAsync("alice")).IsFalse();
        denied = true; // Another process changes Redis; cache hits must not extend the lifetime.
        time.Advance(TimeSpan.FromMilliseconds(999));
        await Assert.That(await list.IsDeniedAsync("alice")).IsFalse();
        time.Advance(TimeSpan.FromMilliseconds(1));
        await Assert.That(await list.IsDeniedAsync("alice")).IsTrue();
        await Assert.That(reads).IsEqualTo(2);
        await list.SetDeniedAsync("alice", false);
        await Assert.That(await list.IsDeniedAsync("alice")).IsFalse();
        await list.SetDeniedAsync("alice", true);
        await Assert.That(await list.IsDeniedAsync("alice")).IsTrue();
        denied = false;
        list.Invalidate();
        await Assert.That(await list.IsDeniedAsync("alice")).IsFalse();
    }

    [Test]
    public async Task CacheCapacityAndDisabledCacheBoundRetainedEntries()
    {
        var reads = 0;
        var list = new RespireRateLimitDenyList((_, _) => { reads++; return ValueTask.FromResult(false); },
            (_, _, _) => ValueTask.CompletedTask, new ManualTime(), TimeSpan.FromSeconds(1), 2);
        foreach (var identity in new[] { "a", "b", "c", "a" }) await list.IsDeniedAsync(identity);
        await Assert.That(reads).IsEqualTo(4);
        var uncached = new RespireRateLimitDenyList((_, _) => { reads++; return ValueTask.FromResult(false); },
            (_, _, _) => ValueTask.CompletedTask, new ManualTime(), TimeSpan.Zero, 2);
        await uncached.IsDeniedAsync("a");
        await uncached.IsDeniedAsync("a");
        await Assert.That(reads).IsEqualTo(6);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InvalidationAndSlowReadsCannotPublishStaleCacheEntries(bool invalidate)
    {
        var time = new ManualTime();
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var list = new RespireRateLimitDenyList((_, _) => ++reads == 1
                ? new ValueTask<bool>(pending.Task) : ValueTask.FromResult(true),
            (_, _, _) => ValueTask.CompletedTask, time, TimeSpan.FromSeconds(1), 2);
        var oldRead = list.IsDeniedAsync("a").AsTask();
        if (invalidate) list.Invalidate();
        else time.Advance(TimeSpan.FromSeconds(1));
        pending.SetResult(false);
        await Assert.That(await oldRead).IsFalse();
        await Assert.That(await list.IsDeniedAsync("a")).IsTrue();
        await Assert.That(reads).IsEqualTo(2);
    }

    [Test]
    public async Task FailedUpdatesInvalidateAndReadFailuresAreNotCached()
    {
        var failRead = false;
        var list = new RespireRateLimitDenyList((_, _) => failRead
                ? ValueTask.FromException<bool>(new IOException("offline")) : ValueTask.FromResult(false),
            (_, _, _) => ValueTask.FromException(new IOException("uncertain write")),
            new ManualTime(), TimeSpan.FromSeconds(1), 2);
        await list.IsDeniedAsync("a");
        failRead = true;
        await Assert.That(async () => await list.SetDeniedAsync("a", true)).Throws<IOException>();
        await Assert.That(async () => await list.IsDeniedAsync("a")).Throws<IOException>();
        failRead = false;
        await Assert.That(await list.IsDeniedAsync("a")).IsFalse();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.That(async () => await list.IsDeniedAsync("a", cancelled.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task UpdateCompletionRemovesDecisionsReadWhileWriteWasPending()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var denied = false;
        var list = new RespireRateLimitDenyList((_, _) => ValueTask.FromResult(denied),
            async (_, value, _) => { await pending.Task; denied = value; },
            new ManualTime(), TimeSpan.FromSeconds(1), 2);
        var update = list.SetDeniedAsync("a", true).AsTask();
        await Assert.That(await list.IsDeniedAsync("a")).IsFalse();
        pending.SetResult();
        await update;
        await Assert.That(await list.IsDeniedAsync("a")).IsTrue();
    }

    [Test]
    public async Task PublicArgumentsRejectInvalidConfigurationAndIdentity()
    {
        await using var client = RespireClient.Create(new RespireOptions { Endpoints = [new("127.0.0.1", 6379)] });
        await Assert.That(() => new RespireRateLimitDenyList(null!, "list")).Throws<ArgumentNullException>();
        await Assert.That(() => new RespireRateLimitDenyList(client, default)).Throws<ArgumentException>();
        await Assert.That(() => new RespireRateLimitDenyList(client, "list", TimeSpan.FromTicks(-1)))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new RespireRateLimitDenyList(client, "list", TimeSpan.FromMinutes(1) + TimeSpan.FromTicks(1)))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new RespireRateLimitDenyList(client, "list", cacheCapacity: 0))
            .Throws<ArgumentOutOfRangeException>();
        var list = new RespireRateLimitDenyList(client, "list");
        await Assert.That(async () => await list.IsDeniedAsync("")).Throws<ArgumentException>();
        await Assert.That(async () => await list.SetDeniedAsync("", true)).Throws<ArgumentException>();
        await Assert.That(() => client.Coordination.RateLimiters.WithDenyList(null!, "a")).Throws<ArgumentNullException>();
        await Assert.That(() => client.Coordination.RateLimiters.WithDenyList(list, "")).Throws<ArgumentException>();
    }

    [Test]
    public async Task ConcurrentReadsAndInvalidationsRemainUsable()
    {
        var list = new RespireRateLimitDenyList((_, _) => ValueTask.FromResult(true),
            (_, _, _) => ValueTask.CompletedTask, TimeProvider.System, TimeSpan.FromSeconds(1), 4);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 100).Select(index => Task.Run(async () =>
        {
            if (index % 3 == 0) list.Invalidate();
            return await list.IsDeniedAsync(index.ToString());
        })));
        await Assert.That(outcomes.All(value => value)).IsTrue();
        await Assert.That(await list.IsDeniedAsync("a")).IsTrue();
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }
}
