using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

/// <summary>
/// MSETEX behaviour against a real server: <c>SetManyExpireAsync(RespireExpiry, SetWhen, …)</c>
/// overload has to map each expiry form onto a shared, live TTL and keep the NX/XX gating.
/// Runs on Redis 8 — MSETEX does not exist in the Redis 7 fixture the rest of the suite uses.
/// </summary>
[ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.PerTestSession)]
public class SetManyExpiryIntegrationTests(ModernRedisTestContainer fixture)
{
    [Test]
    public async Task SetMany_WithoutExpiry_WritesEveryPairWithoutATtl()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);

        await client.Strings.SetManyAsync(("mset:a", "1"), ("mset:b", "2"));

        (await client.GetStringAsync("mset:a")).Should().Be("1");
        (await client.GetStringAsync("mset:b")).Should().Be("2");
        (await client.Keys.ExpiryAsync("mset:a")).HasExpiry.Should().BeFalse();
    }

    [Test]
    public async Task SetMany_WithRelativeTtl_SharesThatTtlAcrossKeys()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);

        (await client.Strings.SetManyExpireAsync(
            TimeSpan.FromSeconds(30), pairs: [("msetex:a", "1"), ("msetex:b", "2")])).Should().BeTrue();

        foreach (var key in (string[])["msetex:a", "msetex:b"])
        {
            var expiry = await client.Keys.ExpiryAsync(key);
            expiry.HasExpiry.Should().BeTrue();
            expiry.TimeToLive!.Value.Should().BeGreaterThan(TimeSpan.FromSeconds(25))
                .And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(30));
        }
    }

    [Test]
    public async Task SetMany_WithAbsoluteTtl_EndsAtThatInstant()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var instant = DateTimeOffset.UtcNow.AddSeconds(30);

        (await client.Strings.SetManyExpireAsync(RespireExpiry.At(instant), pairs: ("msetexat:a", "1"))).Should().BeTrue();

        var expiry = await client.Keys.ExpiryAsync("msetexat:a");
        expiry.HasExpiry.Should().BeTrue();
        (DateTimeOffset.UtcNow + expiry.TimeToLive!.Value).Should().BeCloseTo(instant, TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task SetMany_WithKeep_RetainsTheExistingTtl()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);

        await client.SetAsync("msetkeep:a", "first", TimeSpan.FromSeconds(60));

        (await client.Strings.SetManyExpireAsync(RespireExpiry.Keep, pairs: ("msetkeep:a", "second"))).Should().BeTrue();

        (await client.GetStringAsync("msetkeep:a")).Should().Be("second");
        (await client.Keys.ExpiryAsync("msetkeep:a")).TimeToLive!.Value
            .Should().BeGreaterThan(TimeSpan.FromSeconds(50));
    }

    [Test]
    public async Task SetMany_WithConditions_GatesTheWrite()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);

        (await client.Strings.SetManyExpireAsync(
            TimeSpan.FromSeconds(30), SetWhen.NotExists, ("msetnx:a", "first"))).Should().BeTrue();
        (await client.Strings.SetManyExpireAsync(
            TimeSpan.FromSeconds(30), SetWhen.NotExists, ("msetnx:a", "second"))).Should().BeFalse();
        (await client.GetStringAsync("msetnx:a")).Should().Be("first");

        (await client.Strings.SetManyExpireAsync(
            RespireExpiry.Keep, SetWhen.Exists, ("msetnx:a", "third"))).Should().BeTrue();
        (await client.GetStringAsync("msetnx:a")).Should().Be("third");
        (await client.Keys.ExpiryAsync("msetnx:a")).HasExpiry.Should().BeTrue();

        (await client.Strings.SetManyExpireAsync(
            RespireExpiry.Keep, SetWhen.Exists, ("msetnx:missing", "value"))).Should().BeFalse();
        (await client.ExistsAsync("msetnx:missing")).Should().BeFalse();
    }
}
