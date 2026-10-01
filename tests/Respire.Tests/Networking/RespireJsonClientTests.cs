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
