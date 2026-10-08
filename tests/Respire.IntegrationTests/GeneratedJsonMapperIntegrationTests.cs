using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Respire.Json;
using TUnit.Core;

namespace Respire.IntegrationTests;

[RespireJson("generated-json:{Id}")]
internal partial record JsonMappedCustomer(string Id,
    [property: JsonPropertyName("姓名.\"'\\\n")] string? Name, int? Age);

[Category(TestCategories.ProtocolIndependent)]
[ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.PerTestSession)]
public class GeneratedJsonMapperIntegrationTests(ModernRedisTestContainer fixture)
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task GeneratedDocumentAndEscapedPropertyOperationsUseRealJsonModule(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync(
            RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });
        var json = new RespireJsonClient(client.WithKeyPrefix($"mapper:{Guid.NewGuid():N}:"));
        var model = new JsonMappedCustomer("ü:{}", "姓名😀", null);
        var key = JsonMappedCustomerJsonMapper.GetKey(model);
        try
        {
            (await JsonMappedCustomerJsonMapper.SetAsync(json, model)).Should().BeTrue();
            (await JsonMappedCustomerJsonMapper.GetAsync(json, key)).Value.Should().Be(model);
            (await JsonMappedCustomerJsonMapper.GetNameAsync(json, key)).Value.Should().Be(model.Name);
            (await JsonMappedCustomerJsonMapper.GetAgeAsync(json, key)).Should().Be(new RespireJsonValue<int?>(true, null));
            (await JsonMappedCustomerJsonMapper.GetAsync(json, "missing")).Found.Should().BeFalse();

            (await JsonMappedCustomerJsonMapper.SetNameAsync(json, key, null)).Should().BeTrue();
            (await JsonMappedCustomerJsonMapper.GetNameAsync(json, key)).Should().Be(new RespireJsonValue<string?>(true, null));
            (await JsonMappedCustomerJsonMapper.SetAgeAsync(json, key, 42)).Should().BeTrue();
            (await JsonMappedCustomerJsonMapper.GetAsync(json, key)).Value.Should().Be(model with { Name = null, Age = 42 });
            (await JsonMappedCustomerJsonMapper.SetAsync(json, model, RespireJsonSetCondition.Nx)).Should().BeFalse();
            (await JsonMappedCustomerJsonMapper.SetAsync(json, "missing", model, RespireJsonSetCondition.Xx)).Should().BeFalse();

            (await json.DeleteAsync(key, JsonMappedCustomerJsonMapper.AgePath)).Should().Be(1);
            (await JsonMappedCustomerJsonMapper.GetAgeAsync(json, key)).Found.Should().BeFalse();
            (await JsonMappedCustomerJsonMapper.GetAsync(json, key)).Value!.Age.Should().BeNull();
            await json.SetJsonAsync(key, "\"wrong\"", JsonMappedCustomerJsonMapper.AgePath);
            await FluentActions.Awaiting(async () => await JsonMappedCustomerJsonMapper.GetAsync(json, key))
                .Should().ThrowAsync<JsonException>();
            await JsonMappedCustomerJsonMapper.SetAsync(json, key, null);
            (await JsonMappedCustomerJsonMapper.GetAsync(json, key)).Should().Be(new RespireJsonValue<JsonMappedCustomer>(true, null));
        }
        finally { await json.DeleteAsync(key); }
    }
}
