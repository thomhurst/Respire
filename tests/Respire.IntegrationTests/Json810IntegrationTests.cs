using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Respire.Json;
using TUnit.Core;

namespace Respire.IntegrationTests;

public sealed class Redis810JsonTestContainer() : StandaloneRedisTestContainer("redis:8.10-alpine");

[ClassDataSource<Redis810JsonTestContainer>(Shared = SharedType.PerTestSession)]
public partial class Json810IntegrationTests(Redis810JsonTestContainer fixture)
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task MergePatchAndArrayLengthPreserveReplyShapes(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync(
            RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });
        var json = client.WithKeyPrefix($"json810:{Guid.NewGuid():N}:").Json;
        await json.SetJsonAsync("doc", """{"keep":1,"remove":2,"nested":{"old":3},"items":[1,2]}""");
        var patch = JsonSerializer.Deserialize("""{"remove":null,"nested":{"new":4},"items":[5]}""", Json810Context.Default.JsonElement);
        await json.MergeAsync("doc", patch, Json810Context.Default.JsonElement);
        var document = (await json.GetAsync("doc", Json810Context.Default.JsonElement)).Value;
        document.GetProperty("keep").GetInt32().Should().Be(1);
        document.TryGetProperty("remove", out _).Should().BeFalse();
        document.GetProperty("nested").GetProperty("old").GetInt32().Should().Be(3);
        document.GetProperty("nested").GetProperty("new").GetInt32().Should().Be(4);
        using (var length = await json.Commands.ArrayLengthAsync("doc", ".items"))
            length.AsInteger().Should().Be(1);
        using (var lengths = await json.Commands.ArrayLengthAsync("doc", "$.items"))
        {
            lengths.Count.Should().Be(1);
            lengths[0].AsInteger().Should().Be(1);
        }
        using (var mismatch = await json.Commands.ArrayLengthAsync("doc", "$.keep"))
            mismatch[0].IsNull.Should().BeTrue();
        await json.Commands.Awaiting(commands => commands.ArrayLengthAsync("missing", "$").AsTask())
            .Should().ThrowAsync<RespireServerException>();
        using (var unmatched = await json.Commands.ArrayLengthAsync("doc", "$.absent"))
            unmatched.Count.Should().Be(0);

        var replacement = JsonSerializer.Deserialize("[8,9]", Json810Context.Default.JsonElement);
        await json.MergeAsync("doc", replacement, Json810Context.Default.JsonElement);
        (await json.GetJsonAsync("doc")).Should().Be("[8,9]");
        // A legacy array value and an aggregate reply cannot be distinguished from JSON text alone.
        (await json.GetAsync("doc", Json810Context.Default.Int32Array)).Value.Should().Equal(8, 9);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task PowerAndProjectionReplies(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync(
            RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });
        var json = client.WithKeyPrefix($"json810:{Guid.NewGuid():N}:").Json;
        await json.SetJsonAsync("doc", """{"n":3,"items":[1,2]}""");
        using (var power = await json.Commands.NumberPowerByAsync("doc", "$.n", 2))
        {
            if (protocol == 2) power.AsString().Should().Be("[9]");
            else power[0].AsDouble().Should().Be(9);
        }
        using (var power = await json.Commands.NumberPowerByAsync("doc", ".n", 0.5))
        {
            if (protocol == 2)
                double.Parse(power.AsString()!, System.Globalization.CultureInfo.InvariantCulture).Should().Be(3);
            else power[0].AsDouble().Should().Be(3);
        }

        (string Path, double Expected)[] projections =
        [
            ("$.items.sum()", 3),
            ("sum($.items)", 3),
            ("sum ( $.items )", 3),
            ("($.n + 1)", 4),
            ("(2 * $.n)", 6),
            ("- $.n", -3),
        ];
        foreach (var (path, expected) in projections)
        {
            var result = await json.GetAsync("doc", Json810Context.Default.Double, RespireJsonPath.Projection(path));
            result.Found.Should().BeTrue();
            result.Value.Should().Be(expected);
        }

        var multiple = await json.MultiGetAsync(["doc", "missing"], Json810Context.Default.Double, RespireJsonPath.Projection("sum($.items)"));
        multiple[0]!.Single().Value.Should().Be(3);
        multiple[1].Should().BeNull();
        var legacyProjection = await json.GetAsync("doc", Json810Context.Default.Double,
            RespireJsonPath.Projection("items.sum()"));
        legacyProjection.Value.Should().Be(3);
        // Redis treats the ungrouped numeric-leading expression as legacy $.2 * $.n,
        // producing no match for this document. Declare its projection reply explicitly.
        var numericLeading = await json.GetAsync("doc", Json810Context.Default.Double,
            RespireJsonPath.Projection("2 * $.n"));
        numericLeading.Found.Should().BeFalse();
        await json.SetJsonAsync("doc", """{"items":[]}""");
        (await json.GetManyAsync("doc", Json810Context.Default.Double, RespireJsonPath.Projection("sum($.items)")))
            .Should().BeEmpty();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task CollectionProjectionsPreserveOneTypedArray(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync(
            RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });
        var json = client.WithKeyPrefix($"json810:{Guid.NewGuid():N}:").Json;
        await json.SetJsonAsync("doc", """{"obj":{"x":1,"y":2},"items":[1,2]}""");
        var keysPath = RespireJsonPath.DirectArray("$.obj.keys()");
        var keys = await json.GetAsync("doc", Json810Context.Default.StringArray, keysPath);
        keys.Found.Should().BeTrue();
        keys.Value.Should().BeEquivalentTo(new[] { "x", "y" });
        var multiple = await json.MultiGetAsync(["doc", "missing"], Json810Context.Default.StringArray, keysPath);
        multiple[0]!.Single().Value.Should().BeEquivalentTo(new[] { "x", "y" });
        multiple[1].Should().BeNull();

        var appended = await json.GetManyAsync("doc", Json810Context.Default.Int32Array,
            RespireJsonPath.DirectArray("$.items.append(9)"));
        appended.Should().ContainSingle();
        appended[0].Found.Should().BeTrue();
        appended[0].Value.Should().Equal(1, 2, 9);
        (await json.GetAsync("doc", Json810Context.Default.Int32Array, ".items")).Value.Should().Equal(1, 2);
        await json.SetJsonAsync("doc", """{"obj":{}}""");
        var empty = await json.GetAsync("doc", Json810Context.Default.StringArray, keysPath);
        empty.Found.Should().BeTrue();
        empty.Value.Should().BeEmpty();
    }

    [JsonSerializable(typeof(JsonElement))]
    [JsonSerializable(typeof(double))]
    [JsonSerializable(typeof(int[]))]
    [JsonSerializable(typeof(string[]))]
    internal sealed partial class Json810Context : JsonSerializerContext;
}
