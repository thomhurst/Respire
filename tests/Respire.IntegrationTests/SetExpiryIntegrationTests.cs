using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

/// <summary>
/// SET expiry behaviour against a real server: the <see cref="RespireExpiry"/> union has to produce
/// a live TTL for relative, an instant-accurate one for absolute, a preserved one for keep, and a
/// cleared one for none — with the NX/XX conditions still gating the write.
/// </summary>
[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class SetExpiryIntegrationTests(RedisTestContainer fixture)
{
    [Test]
    public async Task Set_WithRelativeTtl_LeavesAPositiveTimeToLive()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        const string key = "ttl:relative";

        (await client.SetAsync(key, "value", TimeSpan.FromSeconds(30))).Should().BeTrue();

        var expiry = await client.Keys.ExpiryAsync(key);
        expiry.Exists.Should().BeTrue();
        expiry.HasExpiry.Should().BeTrue();
        expiry.TimeToLive!.Value.Should().BeGreaterThan(TimeSpan.FromSeconds(25))
            .And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task Set_WithAbsoluteTtl_LeavesATimeToLiveEndingAtThatInstant()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        const string key = "ttl:absolute";
        var instant = DateTimeOffset.UtcNow.AddSeconds(30);

        (await client.SetAsync(key, "value", RespireExpiry.At(instant))).Should().BeTrue();

        var expiry = await client.Keys.ExpiryAsync(key);
        expiry.HasExpiry.Should().BeTrue();
        var expiresAt = DateTimeOffset.UtcNow + expiry.TimeToLive!.Value;
        expiresAt.Should().BeCloseTo(instant, TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task Set_WithAbsoluteTtlInTheNearFuture_ExpiresTheKey()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        const string key = "ttl:absolute:soon";

        await client.SetAsync(key, "value", RespireExpiry.At(DateTimeOffset.UtcNow.AddMilliseconds(300)));
        (await client.GetStringAsync(key)).Should().Be("value");

        await Task.Delay(TimeSpan.FromSeconds(1));

        (await client.GetStringAsync(key)).Should().BeNull();
    }

    [Test]
    public async Task Set_WithKeep_RetainsTheExistingTimeToLive()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        const string key = "ttl:keep";

        await client.SetAsync(key, "first", TimeSpan.FromSeconds(60));
        (await client.SetAsync(key, "second", RespireExpiry.Keep)).Should().BeTrue();

        var expiry = await client.Keys.ExpiryAsync(key);
        expiry.HasExpiry.Should().BeTrue();
        expiry.TimeToLive!.Value.Should().BeGreaterThan(TimeSpan.FromSeconds(50));
        (await client.GetStringAsync(key)).Should().Be("second");
    }

    [Test]
    public async Task Set_WithNone_ClearsTheExistingTimeToLive()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        const string key = "ttl:none";

        await client.SetAsync(key, "first", TimeSpan.FromSeconds(60));
        (await client.SetAsync(key, "second")).Should().BeTrue();

        var expiry = await client.Keys.ExpiryAsync(key);
        expiry.Exists.Should().BeTrue();
        expiry.HasExpiry.Should().BeFalse();
    }

    [Test]
    public async Task Set_WithNotExists_AppliesTheTtlOnlyOnTheFirstWrite()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        const string key = "ttl:nx";

        (await client.SetAsync(key, "first", TimeSpan.FromSeconds(30), SetWhen.NotExists)).Should().BeTrue();
        (await client.SetAsync(key, "second", TimeSpan.FromSeconds(90), SetWhen.NotExists)).Should().BeFalse();

        (await client.GetStringAsync(key)).Should().Be("first");
        var expiry = await client.Keys.ExpiryAsync(key);
        expiry.TimeToLive!.Value.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task Set_WithExists_RequiresTheKeyAndCanKeepItsTtl()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        const string key = "ttl:xx";

        (await client.SetAsync(key, "first", TimeSpan.FromSeconds(30), SetWhen.Exists)).Should().BeFalse();
        (await client.ExistsAsync(key)).Should().BeFalse();

        await client.SetAsync(key, "first", TimeSpan.FromSeconds(30));
        (await client.SetAsync(key, "second", RespireExpiry.Keep, SetWhen.Exists)).Should().BeTrue();

        (await client.GetStringAsync(key)).Should().Be("second");
        (await client.Keys.ExpiryAsync(key)).HasExpiry.Should().BeTrue();
    }

    [Test]
    public async Task Batch_SetWithExpiryUnion_AppliesEachForm()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);

        await client.SetAsync("ttl:batch:keep", "first", TimeSpan.FromSeconds(60));

        var batch = client.CreateBatch();
        var relative = batch.Set("ttl:batch:relative", "value", TimeSpan.FromSeconds(30));
        var absolute = batch.Set("ttl:batch:absolute", "value", RespireExpiry.At(DateTimeOffset.UtcNow.AddSeconds(30)));
        var keep = batch.Set("ttl:batch:keep", "second", RespireExpiry.Keep);
        await batch.ExecuteAsync();

        (await relative).Should().BeTrue();
        (await absolute).Should().BeTrue();
        (await keep).Should().BeTrue();

        (await client.Keys.ExpiryAsync("ttl:batch:relative")).TimeToLive!.Value
            .Should().BeGreaterThan(TimeSpan.FromSeconds(25));
        (await client.Keys.ExpiryAsync("ttl:batch:absolute")).TimeToLive!.Value
            .Should().BeGreaterThan(TimeSpan.FromSeconds(25));
        (await client.Keys.ExpiryAsync("ttl:batch:keep")).TimeToLive!.Value
            .Should().BeGreaterThan(TimeSpan.FromSeconds(50));
    }
}
