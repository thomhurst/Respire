using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.PerTestSession)]
public class UnifiedExpiryIntegrationTests(ModernRedisTestContainer fixture)
{
    [Test]
    public async Task KeyExpiry_ConditionsAndPersistShareOneMethod()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        await client.SetAsync("unified:key", "value", RespireExpiry.In(TimeSpan.FromSeconds(30)));

        (await client.Keys.ExpireAsync(
            "unified:key", RespireExpiry.In(TimeSpan.FromSeconds(60)), ExpireWhen.NotExists)).Should().BeFalse();
        (await client.Keys.ExpireAsync(
            "unified:key", RespireExpiry.In(TimeSpan.FromSeconds(60)), ExpireWhen.GreaterThan)).Should().BeTrue();
        (await client.Keys.ExpireAsync("unified:key", RespireExpiry.Persist)).Should().BeTrue();

        (await client.Keys.ExpiryAsync("unified:key")).HasExpiry.Should().BeFalse();
    }

    [Test]
    public async Task StringGetExpire_UpdatesAndRemovesExpiry()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        await client.SetAsync("unified:string", "value");

        (await client.Strings.GetExpireAsync(
            "unified:string", RespireExpiry.In(TimeSpan.FromSeconds(30)))).Should().Be("value");
        (await client.Keys.ExpiryAsync("unified:string")).HasExpiry.Should().BeTrue();
        (await client.Strings.GetExpireAsync("unified:string", RespireExpiry.Persist)).Should().Be("value");
        (await client.Keys.ExpiryAsync("unified:string")).HasExpiry.Should().BeFalse();
    }

    [Test]
    public async Task HashExpiry_UsesRelativeAbsolutePersistAndKeepForms()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        await client.Hashes.SetAsync("unified:hash", "field", "value");

        (await client.Hashes.ExpireAsync(
            "unified:hash", RespireExpiry.In(TimeSpan.FromSeconds(30)), "field"))
            .Should().Equal(HashFieldExpiryResult.Applied);
        (await client.Hashes.GetExpireAsync(
            "unified:hash", RespireExpiry.At(DateTimeOffset.UtcNow.AddSeconds(60)), "field"))
            .Should().Equal("value");
        (await client.Hashes.SetExpireAsync(
            "unified:hash", RespireExpiry.Keep, ("field", "updated"))).Should().BeTrue();
        (await client.Hashes.GetExpireAsync("unified:hash", RespireExpiry.Persist, "field"))
            .Should().Equal("updated");

        (await client.Hashes.ExpiryAsync("unified:hash", "field"))[0].HasExpiry.Should().BeFalse();
    }

    [Test]
    public async Task ConditionalHashSet_ClearsExistingFieldExpiry()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        await client.Hashes.SetAsync("conditional:hash", "field", "value");
        await client.Hashes.ExpireAsync(
            "conditional:hash", RespireExpiry.In(TimeSpan.FromSeconds(30)), "field");

        (await client.Hashes.SetAsync(
            "conditional:hash", "field", "updated", SetWhen.Exists)).Should().BeTrue();

        (await client.Hashes.ExpiryAsync("conditional:hash", "field"))[0].HasExpiry.Should().BeFalse();
    }
}
