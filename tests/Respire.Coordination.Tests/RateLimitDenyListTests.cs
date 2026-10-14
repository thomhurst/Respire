using System.Threading.RateLimiting;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Coordination.Tests;

public class RateLimitDenyListTests
{
    [ClassDataSource<SharedRedis74>(Shared = SharedType.PerTestSession)]
    public required SharedRedis74 Redis { get; init; }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    public async Task DeniedIdentitiesNeverConsumePermitsAndUpdatesRestoreAdmission(int kind)
    {
        await using var client = await RespireClient.ConnectAsync(Redis.CreateOptions());
        var prefixed = client.WithKeyPrefix(SharedRespireContainer.Prefix("deny-list"));
        var list = new RespireRateLimitDenyList(prefixed, new byte[] { 0xff, 0, 0x42 });
        var remote = new RespireRateLimitDenyList(prefixed, new byte[] { 0xff, 0, 0x42 }, TimeSpan.Zero);
        var factory = prefixed.Coordination.RateLimiters.WithDenyList(list, "alice\0é");
        await using var limiter = kind switch
        {
            0 => factory.FixedWindow("limit", 1, TimeSpan.FromMinutes(1)),
            1 => factory.SlidingWindow("limit", 1, TimeSpan.FromMinutes(1), 4),
            2 => factory.TokenBucket("limit", 1, 1, TimeSpan.FromMinutes(1)),
            3 => factory.Gcra("limit", 1, TimeSpan.FromMinutes(1)),
            _ => factory.Composite("limit", [RespireRateLimit.FixedWindow(1, TimeSpan.FromMinutes(1))]),
        };
        await list.SetDeniedAsync("alice\0é", true);
        await Assert.That(await remote.IsDeniedAsync("alice\0é")).IsTrue();
        using var denied = await limiter.AcquireAsync();
        using var deniedProbe = await limiter.AcquireAsync(0);
        await Assert.That(denied.IsAcquired).IsFalse();
        await Assert.That(deniedProbe.IsAcquired).IsFalse();
        await Assert.That(denied.TryGetMetadata(MetadataName.ReasonPhrase, out string? reason)).IsTrue();
        await Assert.That(reason).IsEqualTo("deny_list");
        await Assert.That(denied.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan _)).IsFalse();
        using (var exists = await prefixed.ExecuteAsync("EXISTS", ["limit"]))
            await Assert.That(exists.AsInteger()).IsEqualTo(0);
        await list.SetDeniedAsync("alice\0é", false);
        await Assert.That(await remote.IsDeniedAsync("alice\0é")).IsFalse();
        using var allowed = await limiter.AcquireAsync();
        using var full = await limiter.AcquireAsync();
        await Assert.That(allowed.IsAcquired).IsTrue();
        await Assert.That(full.IsAcquired).IsFalse();
        await Assert.That(full.TryGetMetadata(MetadataName.ReasonPhrase, out string? _)).IsFalse();
        await Assert.That(limiter.GetStatistics()!.TotalFailedLeases).IsEqualTo(3);
        await Assert.That(limiter.GetStatistics()!.TotalSuccessfulLeases).IsEqualTo(1);
    }

    [Test]
    public async Task RemoteUpdatesRespectLocalCacheUntilExplicitInvalidation()
    {
        await using var client = await RespireClient.ConnectAsync(Redis.CreateOptions());
        var key = SharedRespireContainer.Key("deny-remote");
        var first = new RespireRateLimitDenyList(client, key, TimeSpan.FromMinutes(1));
        var second = new RespireRateLimitDenyList(client, key);
        await Assert.That(await first.IsDeniedAsync("a")).IsFalse();
        await second.SetDeniedAsync("a", true);
        await Assert.That(await first.IsDeniedAsync("a")).IsFalse();
        first.Invalidate();
        await Assert.That(await first.IsDeniedAsync("a")).IsTrue();
    }
}
