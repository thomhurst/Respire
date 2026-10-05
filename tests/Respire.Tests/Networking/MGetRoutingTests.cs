using System.Text;
using Respire.Internal;
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
        await using var otherOwner = new FakeRespServer
        {
            ReplyOverride = (_, command) => command.StartsWith("MGET ", StringComparison.Ordinal)
                ? "-ERR MGET reached the wrong slot owner\r\n"u8.ToArray() : SetupReply(command),
        };
        var slot = ClusterHash.GetSlot("tenant:{route}a");
        // Only the exact validated slot belongs to target. Any other retained slot
        // reaches otherOwner and fails instead of silently returning the expected values.
        var topology = Encoding.ASCII.GetBytes("*3\r\n"
            + SlotRange(0, slot - 1, otherOwner.Port, "other")
            + SlotRange(slot, slot, target.Port, "target")
            + SlotRange(slot + 1, 16383, otherOwner.Port, "other"));
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
        await Assert.That(otherOwner.ReceivedCommands.Any(command => command.StartsWith("MGET ", StringComparison.Ordinal))).IsFalse();
        await Assert.That(async () => await client.Strings.GetManyAsync("{first}a", "{second}b"))
            .ThrowsExactly<RespireServerException>().WithMessage("CROSSSLOT Keys in request don't hash to the same slot");
    }

    private static string SlotRange(int first, int last, int port, string node)
        => $"*3\r\n:{first}\r\n:{last}\r\n*3\r\n$9\r\n127.0.0.1\r\n:{port}\r\n${node.Length}\r\n{node}\r\n";

    private static byte[] SetupReply(string command) => command.StartsWith("HELLO", StringComparison.Ordinal)
        ? "%1\r\n+proto\r\n:3\r\n"u8.ToArray()
        : command == "CLUSTER SLOTS" ? "*0\r\n"u8.ToArray() : FakeRespServer.OkReply;
}
