using System.Text.Json.Serialization;
using Respire;
using Respire.Extensions.Json;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public partial class RespireJsonClientTests
{
    [Test]
    [Arguments(RespProtocol.Resp2)]
    [Arguments(RespProtocol.Resp3)]
    public async Task GetAsyncDeserializesJsonAndPrefixesGeneratedCommandKey(RespProtocol protocol)
    {
        var jsonReply = "$9\r\n{\"Age\":1}\r\n"u8.ToArray();
        await using var server = new FakeRespServer(1, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => "%1\r\n+proto\r\n:3\r\n"u8.ToArray(),
                "CLIENT MAINT_NOTIFICATIONS ON" => FakeRespServer.OkReply,
                "JSON.GET tenant:profile ." => jsonReply,
                _ => null,
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = protocol,
            MaintenanceNotifications = protocol == RespProtocol.Resp3
                ? RespireMaintenanceNotificationMode.Enabled
                : RespireMaintenanceNotificationMode.Disabled,
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            ThreadPoolMonitoring = false,
        });
        var json = new RespireJsonClient(client.WithKeyPrefix("tenant:"));

        var result = await json.GetAsync("profile", JsonTestContext.Default.Profile);

        await Assert.That(result.Found).IsTrue();
        await Assert.That(result.Value!.Age).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands).Contains("JSON.GET tenant:profile .");
    }

    [Test]
    public async Task CatalogCommandsPrefixEveryKnownJsonDocumentKey()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var prefixed = client.WithKeyPrefix("tenant:");
        (RespireCommand Command, RespireValue[] Arguments)[] commands =
        [
            (RespireCommands.Json.JSON_ARRLEN, ["profile", "."]),
            (RespireCommands.Json.JSON_MERGE, ["profile", ".", "{}"]),
            (RespireCommands.Json.JSON_NUMPOWBY, ["profile", ".", 2]),
            (RespireCommands.Json.JSON_MSET, ["first", ".", "{}", "second", ".", "{}"]),
            (RespireCommands.Json.JSON_DEBUG_MEMORY, ["profile", "."]),
        ];

        foreach (var (command, arguments) in commands)
        {
            using var result = await prefixed.ExecuteAsync(command, arguments);
        }

        await Assert.That(server.ReceivedCommands).Contains("JSON.ARRLEN tenant:profile .");
        await Assert.That(server.ReceivedCommands).Contains("JSON.MERGE tenant:profile . {}");
        await Assert.That(server.ReceivedCommands).Contains("JSON.NUMPOWBY tenant:profile . 2");
        await Assert.That(server.ReceivedCommands).Contains("JSON.MSET tenant:first . {} tenant:second . {}");
        await Assert.That(server.ReceivedCommands).Contains("JSON.DEBUG MEMORY tenant:profile .");
    }

    [Test]
    public async Task PrefixedDebugCatalogCommandsPrefixFieldsAndKeepHelpKeyless()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var prefixed = client.WithKeyPrefix("tenant:");

        using (await prefixed.ExecuteAsync(RespireCommands.Dragonfly.JSON_DEBUG_FIELDS, ["profile", "."])) { }
        using (await prefixed.ExecuteAsync(RespireCommands.Dragonfly.JSON_DEBUG_HELP, [])) { }

        await Assert.That(server.ReceivedCommands).Contains("JSON.DEBUG FIELDS tenant:profile .");
        await Assert.That(server.ReceivedCommands).Contains("JSON.DEBUG HELP");
    }

    [Test]
    public async Task PrefixedJsonCommandsRejectNullKeysAndReportLayoutErrorsThroughTheTask()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var prefixed = client.WithKeyPrefix("tenant:");

        // A null key must not become the bare prefix.
        await Assert.That(async () => await prefixed.ExecuteAsync(RespireCommands.Json.JSON_GET, [RespireValue.Null, "."]))
            .Throws<ArgumentNullException>();

        // Malformed layouts fault the returned task instead of throwing synchronously.
        ValueTask<RespireResult> malformed = default;
        await Assert.That(() => { malformed = prefixed.ExecuteAsync(RespireCommands.Json.JSON_MGET, ["."]); }).ThrowsNothing();
        await Assert.That(async () => { using var result = await malformed; }).Throws<Exception>();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("JSON.", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task MultiSetRejectsCrossSlotKeysBeforeConnecting()
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", 1) },
        });
        var json = new RespireJsonClient(client);
        RespireJsonSetEntry<Profile>[] entries =
        [
            new("{left}:profile", new(1)),
            new("{right}:profile", new(2)),
        ];

        var error = await Assert.That(async () => await json.MultiSetAsync(entries, JsonTestContext.Default.Profile))
            .Throws<RespireServerException>();
        await Assert.That(error!.Message).Contains("CROSSSLOT");
    }

    [Test]
    public async Task MultiSetWritesFlattenedKeyPathValueTriples()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var json = new RespireJsonClient(client);
        RespireJsonSetEntry<Profile>[] entries =
        [
            new("{same}:one", new(1), "$.field"),
            new("{same}:two", new(2), "$.field"),
        ];

        await Assert.That(await json.MultiSetAsync(entries, JsonTestContext.Default.Profile)).IsTrue();

        await Assert.That(server.ReceivedCommands)
            .Contains("JSON.MSET {same}:one $.field {\"Age\":1} {same}:two $.field {\"Age\":2}");
    }

    private sealed record Profile(int Age);

    [JsonSerializable(typeof(Profile))]
    private sealed partial class JsonTestContext : JsonSerializerContext;
}
