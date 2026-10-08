using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Respire.Json;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Core;

namespace Respire.Tests;

[RespireJson("json:{{{Id}}}:{Price}")]
internal partial record JsonMapperModel(string Id,
    [property: JsonPropertyName("姓名.\"'\\\n")] string? Name, int? Age, decimal Price,
    bool Enabled, long Count, double Score, Guid Token, DateTimeOffset Created);

[RespireJson("mutable:{Id}")]
internal partial class JsonMapperMutable
{
    public required string Id { get; init; }
    public required int Value { get; set; }
}

[RespireJson("nullable")]
internal partial record JsonMapperNullable(bool? Flag, long? Count, double? Score, decimal? Amount,
    Guid? Token, DateTimeOffset? Created);

public class GeneratedJsonMapperTests
{
    private static JsonMapperModel Create(string? name = null, int? age = null) => new(
        "ü:{}", name, age, 12.50M, true, long.MaxValue, 1.125,
        Guid.Parse("754a1f42-3f49-42e9-9cb6-7e477b3e4098"),
        new DateTimeOffset(2026, 10, 8, 15, 0, 0, TimeSpan.FromHours(5.5)));

    [Test]
    public async Task AllNullableScalarConvertersHandleNullAndPresentValues()
    {
        var values = new[]
        {
            new JsonMapperNullable(null, null, null, null, null, null),
            new JsonMapperNullable(false, long.MinValue, double.Epsilon, decimal.MinValue, Guid.NewGuid(), DateTimeOffset.UtcNow),
        };
        foreach (var value in values)
            await Assert.That(JsonMapperNullableJsonMapper.FromJson(JsonMapperNullableJsonMapper.ToJson(value))).IsEqualTo(value);
        await Assert.That(JsonMapperNullableJsonMapper.FromJson("{}"u8)).IsEqualTo(values[0]);
    }

    [Test]
    [Arguments(null, null)]
    [Arguments("", 0)]
    [Arguments("姓名😀", 42)]
    public async Task NullableUnicodeAndAllScalarTypesRoundTrip(string? name, int? age)
    {
        var value = Create(name, age);
        await Assert.That(JsonMapperModelJsonMapper.FromJson(JsonMapperModelJsonMapper.ToJson(value))).IsEqualTo(value);
        await Assert.That(JsonMapperModelJsonMapper.GetKey(value).ToString()).IsEqualTo("json:{ü:{}}:12.50");
        await Assert.That(JsonMapperModelJsonMapper.NamePath.Value).IsEqualTo("$[\"姓名.\\\"'\\\\\n\"]");
        await Assert.That(JsonMapperModelJsonMapper.FromJson("null"u8)).IsNull();
        await Assert.That(Encoding.UTF8.GetString(JsonMapperModelJsonMapper.ToJson(null))).IsEqualTo("null");
    }

    [Test]
    [NotInParallel]
    public async Task MetadataAndKeysIgnoreCurrentCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var value = Create("é", 1);
            await Assert.That(JsonMapperModelJsonMapper.GetKey(value).ToString()).IsEqualTo("json:{ü:{}}:12.50");
            await Assert.That(JsonSerializer.Deserialize(JsonSerializer.Serialize(value, JsonMapperModelJsonMapper.JsonTypeInfo),
                JsonMapperModelJsonMapper.JsonTypeInfo)).IsEqualTo(value);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Test]
    public async Task UnknownMembersAreSkippedAndDuplicateMembersUseLastValue()
    {
        var value = JsonMapperMutableJsonMapper.FromJson("{\"Id\":\"a\",\"Value\":1,\"future\":{\"nested\":[1,null]},\"Value\":2}"u8);
        await Assert.That(value!.Id).IsEqualTo("a");
        await Assert.That(value.Value).IsEqualTo(2);
        await Assert.That(JsonMapperMutableJsonMapper.FromJson(JsonMapperMutableJsonMapper.ToJson(value))!.Value).IsEqualTo(2);
    }

    [Test]
    [Arguments("{}")]
    [Arguments("{\"Id\":null,\"Value\":1}")]
    [Arguments("{\"Id\":\"a\",\"Value\":null}")]
    [Arguments("{\"Id\":\"a\",\"Value\":\"1\"}")]
    [Arguments("{\"Id\":\"a\",\"Value\":2147483648}")]
    [Arguments("{\"Id\":\"a\",\"Value\":1")]
    [Arguments("[]")]
    [Arguments("{broken")]
    [Arguments("{\"Id\":\"a\",\"Value\":1} false")]
    public async Task MalformedMissingNullAndWrongTypePayloadsFail(string payload)
        => await Assert.That(() => JsonMapperMutableJsonMapper.FromJson(Encoding.UTF8.GetBytes(payload))).Throws<JsonException>();

    [Test]
    public async Task NullRequiredPropertyCannotBeWritten()
        => await Assert.That(() => JsonMapperMutableJsonMapper.ToJson(new JsonMapperMutable { Id = null!, Value = 1 })).Throws<JsonException>();

    [Test]
    [Arguments(double.NaN)]
    [Arguments(double.PositiveInfinity)]
    [Arguments(double.NegativeInfinity)]
    public async Task NonFiniteNumbersCannotBeWritten(double score)
        => await Assert.That(() => JsonMapperModelJsonMapper.ToJson(Create() with { Score = score })).Throws<ArgumentException>();

    [Test]
    public async Task OutOfRangeFloatingPointPayloadFails()
        => await Assert.That(() => JsonMapperNullableJsonMapper.FromJson("{\"Score\":1e999}"u8)).Throws<JsonException>();

    [Test]
    public async Task TypedWritesUseModelKeysPrefixConditionsAndEscapedPropertyPaths()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var json = new RespireJsonClient(client.WithKeyPrefix("tenant:"));
        var model = Create("姓名", 1);
        await JsonMapperModelJsonMapper.SetAsync(json, model, RespireJsonSetCondition.Nx);
        await JsonMapperModelJsonMapper.SetNameAsync(json, "document", null);
        await JsonMapperModelJsonMapper.SetAsync(json, "document", null, RespireJsonSetCondition.Xx);
        await Assert.That(server.ReceivedCommands).Contains("JSON.SET tenant:json:{ü:{}}:12.50 $ " +
            Encoding.UTF8.GetString(JsonMapperModelJsonMapper.ToJson(model)) + " NX");
        await Assert.That(server.ReceivedCommands).Contains("JSON.SET tenant:document " + JsonMapperModelJsonMapper.NamePath.Value + " null");
        await Assert.That(server.ReceivedCommands).Contains("JSON.SET tenant:document $ null XX");
    }

    [Test]
    [Arguments("$-1\r\n", false, null)]
    [Arguments("$2\r\n[]\r\n", false, null)]
    [Arguments("$6\r\n[null]\r\n", true, null)]
    [Arguments("$3\r\n[5]\r\n", true, 5)]
    public async Task PartialReadsDistinguishMissingAndNull(string reply, bool found, int? value)
    {
        await using var server = new FakeRespServer(Encoding.UTF8.GetBytes(reply));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var result = await JsonMapperModelJsonMapper.GetAgeAsync(new RespireJsonClient(client), "document");
        await Assert.That(result.Found).IsEqualTo(found);
        await Assert.That(result.Value).IsEqualTo(value);
        await Assert.That(server.ReceivedCommands).Contains("JSON.GET document $[\"Age\"]");
    }

    [Test]
    public async Task NonNullablePropertyReadsReturnFoundDefaultsForJsonNull()
    {
        await using var server = new FakeRespServer("$6\r\n[null]\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var json = new RespireJsonClient(client);
        await AssertFoundDefault(await JsonMapperModelJsonMapper.GetIdAsync(json, "document"));
        await AssertFoundDefault(await JsonMapperMutableJsonMapper.GetValueAsync(json, "document"));
        await AssertFoundDefault(await JsonMapperModelJsonMapper.GetPriceAsync(json, "document"));
        await AssertFoundDefault(await JsonMapperModelJsonMapper.GetEnabledAsync(json, "document"));
        await AssertFoundDefault(await JsonMapperModelJsonMapper.GetCountAsync(json, "document"));
        await AssertFoundDefault(await JsonMapperModelJsonMapper.GetScoreAsync(json, "document"));
        await AssertFoundDefault(await JsonMapperModelJsonMapper.GetTokenAsync(json, "document"));
        await AssertFoundDefault(await JsonMapperModelJsonMapper.GetCreatedAsync(json, "document"));

        static async Task AssertFoundDefault<T>(RespireJsonValue<T> result)
        {
            await Assert.That(result.Found).IsTrue();
            await Assert.That(result.Value).IsEqualTo(default(T));
        }
    }

    [Test]
    public async Task MultipleMatchesAndMalformedRepliesFail()
    {
        await using var server = new FakeRespServer("$5\r\n[1,2]\r\n"u8.ToArray(), "$6\r\n[\"no\"]\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var json = new RespireJsonClient(client);
        await Assert.That(async () => await JsonMapperModelJsonMapper.GetAgeAsync(json, "document")).Throws<InvalidOperationException>();
        await Assert.That(async () => await JsonMapperModelJsonMapper.GetAgeAsync(json, "document")).Throws<JsonException>();
    }

    [Test]
    public async Task PreCanceledOperationsSendNoJsonCommand()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var json = new RespireJsonClient(client);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.That(async () => await JsonMapperModelJsonMapper.GetAsync(json, "document", canceled.Token)).Throws<OperationCanceledException>();
        await Assert.That(async () => await JsonMapperModelJsonMapper.SetAsync(json, Create(), cancellationToken: canceled.Token)).Throws<OperationCanceledException>();
        await Assert.That(async () => await JsonMapperModelJsonMapper.SetAgeAsync(json, "document", null, canceled.Token)).Throws<OperationCanceledException>();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("JSON.", StringComparison.Ordinal))).IsFalse();
    }
}
