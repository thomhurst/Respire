using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Coordination.Tests;

public class CoordinationTrackingReadTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task RegistrationAlwaysSendsTrackedReadWithoutPublishing(bool hash, bool concurrentMutation)
    {
        var read = hash ? "HGET prefix:key field" : "GET prefix:key";
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
                var value when value == read => "$5\r\nvalue\r\n"u8.ToArray(),
                _ => FakeRespServer.OkReply,
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespireProtocol.Resp3, Endpoints = [new("127.0.0.1", server.Port)],
            Connections = 1, ClientSideCache = new(),
        });
        var view = client.WithKeyPrefix("prefix:");
        // A resident value must not replace a fresh tracking registration either.
        _ = hash ? await view.Hashes.GetStringAsync("key", "field") : await view.GetStringAsync("key");
        var cache = client.Core.ClientCache!;
        await Assert.That(cache.Count).IsEqualTo(1);
        var mutation = concurrentMutation ? cache.BeginUnknownMutation() : default;
        try
        {
            var cached = cache.Count;
            var before = server.ReceivedCommands.Count;
            RespireKey? field = hash ? new RespireKey("field") : default(RespireKey?);
            await CoordinationCommandHooks.TrackAsync(view, "key", field, default);
            await CoordinationCommandHooks.TrackAsync(view, "key", field, default);
            var commands = server.ReceivedCommands.Skip(before).ToArray();
            await Assert.That(commands.SequenceEqual(
                new[] { "CLIENT CACHING YES", read, "CLIENT CACHING YES", read })).IsTrue();
            await Assert.That(cache.Count).IsEqualTo(cached);
        }
        finally
        {
            if (concurrentMutation) cache.CompleteMutation(in mutation);
        }
    }
}
