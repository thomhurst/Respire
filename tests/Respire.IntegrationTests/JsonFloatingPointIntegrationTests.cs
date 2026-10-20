using System.Text.Json.Serialization;
using FluentAssertions;
using Respire.Json;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ClassDataSource<VersionedServerFixture>(Shared = SharedType.PerTestSession)]
public partial class JsonFloatingPointIntegrationTests(VersionedServerFixture servers)
{
    [Test]
    [Arguments(2, "redis:8.10-alpine")]
    [Arguments(3, "redis:8.10-alpine")]
    [Arguments(2, "redis:8.8.3-alpine")]
    [Arguments(3, "redis:8.8.3-alpine")]
    [Arguments(2, "redis:8.6-alpine")]
    [Arguments(3, "redis:8.6-alpine")]
    public async Task FloatingPointArraysRoundTripWithConditionsAndRejectOverflow(int protocol, string image)
    {
        var lease = await servers.LeaseAsync(image);
        await using var client = await RespireClient.ConnectAsync(lease.ConnectionString(protocol));
        using (var info = await client.ExecuteAsync(RespireCommands.Server.INFO, "server"))
        {
            var versionLine = info.AsString()!.Split('\n').Single(line => line.StartsWith("redis_version:", StringComparison.Ordinal));
            var version = Version.Parse(versionLine["redis_version:".Length..].Trim());
            Skip.Unless(version >= new Version(8, 8), "JSON.SET FPHA requires Redis 8.8 or later.");
        }

        var prefixed = client.WithKeyPrefix($"fpha:{Guid.NewGuid():N}:");
        var json = prefixed.Json;
        foreach (var type in Enum.GetValues<RespireJsonFloatingPointType>())
        {
            var key = type.ToString();
            (await json.SetAsync(type, key, new[] { 1.5, -2.5, 4000.0 }, FloatingPointContext.Default.DoubleArray,
                condition: RespireJsonSetCondition.Nx)).Should().BeTrue();
            (await json.SetJsonAsync(type, key, "[99.5]", condition: RespireJsonSetCondition.Nx)).Should().BeFalse();
            (await json.GetAsync(key, FloatingPointContext.Default.DoubleArray)).Value.Should().Equal(1.5, -2.5, 4000.0);
            (await json.SetJsonAsync(type, key, "[3.5,4.5]"u8.ToArray(), condition: RespireJsonSetCondition.Xx)).Should().BeTrue();
            (await json.GetAsync(key, FloatingPointContext.Default.DoubleArray)).Value.Should().Equal(3.5, 4.5);
            (await json.SetJsonAsync(type, key + "-missing", "[1.5]", condition: RespireJsonSetCondition.Xx)).Should().BeFalse();

            await json.SetJsonAsync("nested", "{\"values\":[]}");
            (await json.SetJsonAsync(type, "nested", "[5.5,6.5]", "$.values", RespireJsonSetCondition.Xx)).Should().BeTrue();
            (await json.GetAsync("nested", FloatingPointContext.Default.DoubleArray, "$.values")).Value.Should().Equal(5.5, 6.5);

            using var result = await prefixed.ExecuteAsync(RespireCommands.Json.JSON_SET,
                key, ".", "[7.5,8.5]", "FPHA", key.ToUpperInvariant());
            result.AsString().Should().Be("OK");
            (await json.GetAsync(key, FloatingPointContext.Default.DoubleArray)).Value.Should().Equal(7.5, 8.5);

            // Values halfway between representable numbers distinguish each selected precision.
            var (input, expected) = type switch
            {
                RespireJsonFloatingPointType.Fp16 => ("[2049.0]", 2048.0),
                RespireJsonFloatingPointType.Bf16 => ("[257.0]", 256.0),
                RespireJsonFloatingPointType.Fp32 => ("[16777217.0]", 16777216.0),
                _ => ("[16777217.0]", 16777217.0),
            };
            await json.SetJsonAsync(type, key, input);
            (await json.GetAsync(key, FloatingPointContext.Default.DoubleArray)).Value.Should().Equal(expected);
        }

        // FP16 tops out at 65504. Redis must report the error and retain the previous value.
        await json.SetJsonAsync("overflow", "[1.5]");
        Func<Task> overflow = async () => await json.SetJsonAsync(RespireJsonFloatingPointType.Fp16, "overflow", "[70000.0]");
        await overflow.Should().ThrowAsync<RespireServerException>().WithMessage("*out of range*");
        (await json.GetAsync("overflow", FloatingPointContext.Default.DoubleArray)).Value.Should().Equal(1.5);
    }

    [JsonSerializable(typeof(double[]))]
    internal sealed partial class FloatingPointContext : JsonSerializerContext;
}
