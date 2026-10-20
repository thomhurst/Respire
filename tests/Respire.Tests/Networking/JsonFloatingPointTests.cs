using Respire.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public partial class RespireJsonClientTests
{
    [Test]
    [Arguments(RespireJsonFloatingPointType.Fp16, "FP16")]
    [Arguments(RespireJsonFloatingPointType.Bf16, "BF16")]
    [Arguments(RespireJsonFloatingPointType.Fp32, "FP32")]
    [Arguments(RespireJsonFloatingPointType.Fp64, "FP64")]
    public async Task FloatingPointSetWritesAllPayloadFormsAndConditions(RespireJsonFloatingPointType type, string token)
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.Contains(" NX FPHA ", StringComparison.Ordinal)
                ? "$-1\r\n"u8.ToArray() : null,
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var json = client.WithKeyPrefix("tenant:").Json;
        foreach (var condition in Enum.GetValues<RespireJsonSetCondition>())
        {
            var expected = condition != RespireJsonSetCondition.Nx;
            await Assert.That(await json.SetAsync(type, "typed", new[] { 1.5, 2.5 }, JsonTestContext.Default.DoubleArray,
                "$.values", condition)).IsEqualTo(expected);
            await Assert.That(await json.SetJsonAsync(type, "text", "[1.5,2.5]", "$.values", condition)).IsEqualTo(expected);
            await Assert.That(await json.SetJsonAsync(type, "bytes", "[1.5,2.5]"u8.ToArray(), "$.values", condition)).IsEqualTo(expected);
            var conditionToken = condition switch { RespireJsonSetCondition.Nx => " NX", RespireJsonSetCondition.Xx => " XX", _ => "" };
            foreach (var key in new[] { "typed", "text", "bytes" })
                await Assert.That(server.ReceivedCommands).Contains($"JSON.SET tenant:{key} $.values [1.5,2.5]{conditionToken} FPHA {token}");
        }

        // The catalog already carries JSON.SET key/mutation metadata for immediate raw commands.
        using var result = await client.WithKeyPrefix("tenant:").ExecuteAsync(
            RespireCommands.Json.JSON_SET, "raw", ".", "[1.5,2.5]", "XX", "FPHA", token);
        await Assert.That(result.AsString()).IsEqualTo("OK");
        await Assert.That(server.ReceivedCommands).Contains($"JSON.SET tenant:raw . [1.5,2.5] XX FPHA {token}");
    }

    [Test]
    public async Task FloatingPointSetRejectsInvalidEnumsThroughReturnedTasksBeforeSending()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var json = client.Json;
        var invalid = (RespireJsonFloatingPointType)42;
        // These calls themselves must not throw, matching existing typed JSON setters.
        var typed = json.SetAsync(invalid, "doc", new[] { 1.5 }, JsonTestContext.Default.DoubleArray);
        var text = json.SetJsonAsync(invalid, "doc", "[1.5]");
        var bytes = json.SetJsonAsync(invalid, "doc", "[1.5]"u8.ToArray());
        await Assert.That(async () => await typed).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await text).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await bytes).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await json.SetJsonAsync(RespireJsonFloatingPointType.Fp16, "doc", "[1.5]",
            condition: (RespireJsonSetCondition)42)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(server.ReceivedCommands.Any(c => c.StartsWith("JSON.SET ", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task LegacySetDefaultArgumentsRemainUnambiguousAndDoNotSendFpha()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await client.Json.SetAsync("typed", new[] { 1.5 }, JsonTestContext.Default.DoubleArray, default, default, default);
        await client.Json.SetJsonAsync("text", "[1.5]", default, default, default);
        await client.Json.SetJsonAsync("bytes", "[1.5]"u8.ToArray(), default, default, default);
        foreach (var key in new[] { "typed", "text", "bytes" })
            await Assert.That(server.ReceivedCommands).Contains($"JSON.SET {key} . [1.5]");
    }
}
