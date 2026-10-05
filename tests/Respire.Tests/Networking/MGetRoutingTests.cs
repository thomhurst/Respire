using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class MGetRoutingTests
{
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task PrefixedMGetRoutesToValidatedSlotAndRejectsCrossSlot(int cacheMode)
    {
        await using var target = new FakeRespServer
        {
            ReplyOverride = (_, command) => command.StartsWith("MGET ", StringComparison.Ordinal)
                ? "*3\r\n$1\r\na\r\n$1\r\nb\r\n$1\r\na\r\n"u8.ToArray() : SetupReply(command),
        };
        var topology = Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*3\r\n$9\r\n127.0.0.1\r\n:{target.Port}\r\n$6\r\ntarget\r\n");
        await using var seed = new FakeRespServer
        {
            ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? topology : SetupReply(command),
        };
        await using var owner = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, UseCluster = true,
            Endpoints = [new("127.0.0.1", seed.Port)],
            ClientSideCache = cacheMode == 0 ? null : new() { CoalesceConcurrentMisses = cacheMode == 2 },
        });
        var client = owner.WithKeyPrefix("tenant:");
        await Assert.That(await client.Strings.GetManyAsync("{route}a", "{route}b", "{route}a"))
            .IsEquivalentTo(new string?[] { "a", "b", "a" }, CollectionOrdering.Matching);
        await Assert.That(await client.Strings.GetManyAsync("{route}a", "{route}b", "{route}a"))
            .IsEquivalentTo(new string?[] { "a", "b", "a" }, CollectionOrdering.Matching);
        await Assert.That(target.ReceivedCommands.Count(command => command == "MGET tenant:{route}a tenant:{route}b tenant:{route}a"))
            .IsEqualTo(cacheMode == 0 ? 2 : 1);
        await Assert.That(seed.ReceivedCommands.Any(command => command.StartsWith("MGET ", StringComparison.Ordinal))).IsFalse();
        await Assert.That(async () => await client.Strings.GetManyAsync("{first}a", "{second}b"))
            .ThrowsExactly<RespireServerException>().WithMessage("CROSSSLOT Keys in request don't hash to the same slot");
    }

    private static byte[] SetupReply(string command) => command.StartsWith("HELLO", StringComparison.Ordinal)
        ? "%1\r\n+proto\r\n:3\r\n"u8.ToArray()
        : command == "CLUSTER SLOTS" ? "*0\r\n"u8.ToArray() : FakeRespServer.OkReply;
}
