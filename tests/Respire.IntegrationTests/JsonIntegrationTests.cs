using System.Text.Json.Serialization;
using FluentAssertions;
using Respire.Extensions.Json;
using TUnit.Core;

namespace Respire.IntegrationTests;

/// <summary>Redis 8 bundles RedisJSON, so these run against the shared modern Redis container.</summary>
[ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.PerTestSession)]
public partial class JsonIntegrationTests(ModernRedisTestContainer fixture)
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task TypedDocumentsRoundTripThroughAKeyPrefixedView(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var prefix = $"json:{Guid.NewGuid():N}:";
        var json = new RespireJsonClient(client.WithKeyPrefix(prefix));
        var info = JsonIntegrationContext.Default.Customer;

        (await json.SetAsync("customer", new Customer("Ada", 36), info)).Should().BeTrue();
        (await client.Keys.ExistsAsync(prefix + "customer")).Should().BeTrue();
        (await client.Keys.ExistsAsync("customer")).Should().BeFalse();

        var legacy = await json.GetAsync("customer", info);
        legacy.Found.Should().BeTrue();
        legacy.Value.Should().Be(new Customer("Ada", 36));
        var jsonPath = await json.GetAsync("customer", info, RespireJsonPath.JsonPathRoot);
        jsonPath.Value.Should().Be(new Customer("Ada", 36));
        (await json.GetAsync("missing", info)).Found.Should().BeFalse();
        (await json.GetAsync("customer", JsonIntegrationContext.Default.Int32, "$.missing")).Found.Should().BeFalse();
        (await json.GetManyAsync("customer", JsonIntegrationContext.Default.Int32, "$..Age"))
            .Select(value => value.Value).Should().Equal(36);

        (await json.SetJsonAsync("customer", "null", "$.Name")).Should().BeTrue();
        var storedNull = await json.GetAsync("customer", JsonIntegrationContext.Default.String, "$.Name");
        storedNull.Found.Should().BeTrue();
        storedNull.Value.Should().BeNull();

        (await json.SetAsync("customer", new Customer("Bob", 1), info, condition: RespireJsonSetCondition.Nx))
            .Should().BeFalse();
        (await json.SetAsync("absent", new Customer("Bob", 1), info, condition: RespireJsonSetCondition.Xx))
            .Should().BeFalse();
        (await json.SetJsonAsync("customer", "{\"Name\":\"Grace\",\"Age\":45}"u8.ToArray(),
            condition: RespireJsonSetCondition.Xx)).Should().BeTrue();
        (await json.GetJsonAsync("customer", new RespireJsonGetOptions { Paths = ["$.Name"] }))
            .Should().Be("[\"Grace\"]");
        (await json.GetMemoryUsageAsync("customer")).Should().ContainSingle().Which.Should().BePositive();

        (await json.DeleteAsync("customer", "$.Age")).Should().Be(1);
        (await json.ForgetAsync("customer")).Should().Be(1);
        (await json.GetAsync("customer", info)).Found.Should().BeFalse();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task MultiKeyCommandsPrefixEveryDocumentKey(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var prefix = $"json:{{{Guid.NewGuid():N}}}:";
        var json = new RespireJsonClient(client.WithKeyPrefix(prefix));
        var info = JsonIntegrationContext.Default.Customer;

        await json.MultiSetAsync(
            [new("one", new Customer("Ada", 1)), new("two", new Customer("Grace", 2))], info);
        (await client.Keys.ExistsAsync(prefix + "one")).Should().BeTrue();
        (await client.Keys.ExistsAsync(prefix + "two")).Should().BeTrue();

        var legacy = await json.MultiGetAsync(["one", "missing", "two"], info);
        legacy.Should().HaveCount(3);
        legacy[0]!.Single().Value.Should().Be(new Customer("Ada", 1));
        legacy[1].Should().BeNull();
        legacy[2]!.Single().Value.Should().Be(new Customer("Grace", 2));

        var ages = await json.MultiGetAsync(["one", "two"], JsonIntegrationContext.Default.Int32, "$.Age");
        ages.Select(values => values!.Single().Value).Should().Equal(1, 2);
    }

    [Test]
    public async Task LocalWritesInvalidateCachedJsonReads()
    {
        var options = RespireOptions.Parse(fixture.ConnectionString) with
        {
            Protocol = RespProtocol.Resp3,
            ClientSideCache = new(),
        };
        await using var client = await RespireClient.ConnectAsync(options);
        var prefix = $"json:{{{Guid.NewGuid():N}}}:";
        var json = new RespireJsonClient(client);
        var info = JsonIntegrationContext.Default.Customer;

        await json.SetAsync(prefix + "one", new Customer("Ada", 1), info);
        await json.SetAsync(prefix + "two", new Customer("Grace", 2), info);
        (await json.GetAsync(prefix + "one", info)).Value!.Age.Should().Be(1);
        (await json.GetAsync(prefix + "one", info)).Value!.Age.Should().Be(1);

        using (await json.Commands.NumberIncrementByAsync(prefix + "one", "$.Age", 10)) { }
        (await json.GetAsync(prefix + "one", info)).Value!.Age.Should().Be(11);

        (await json.MultiGetAsync([prefix + "one", prefix + "two"], info)).Should().HaveCount(2);
        await json.MultiSetAsync(
            [new(prefix + "one", new Customer("Ada", 21)), new(prefix + "two", new Customer("Grace", 22))], info);
        (await json.GetAsync(prefix + "one", info)).Value!.Age.Should().Be(21);
        (await json.GetAsync(prefix + "two", info)).Value!.Age.Should().Be(22);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ServerErrorsSurfaceAsExceptions(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var key = $"json:{Guid.NewGuid():N}:string";
        var json = new RespireJsonClient(client);
        await client.SetAsync(key, "not a document");

        await json.Awaiting(j => j.GetAsync(key, JsonIntegrationContext.Default.Customer).AsTask())
            .Should().ThrowAsync<RespireServerException>();
        await json.Awaiting(j => j.SetJsonAsync($"{key}:invalid", "{not json").AsTask())
            .Should().ThrowAsync<RespireServerException>();
    }

    private Task<RespireClient> ConnectAsync(int protocol)
        => RespireClient.ConnectAsync(
            RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol }).AsTask();

    internal sealed record Customer(string? Name, int Age);

    [JsonSerializable(typeof(Customer))]
    [JsonSerializable(typeof(int))]
    [JsonSerializable(typeof(string))]
    internal sealed partial class JsonIntegrationContext : JsonSerializerContext;
}
