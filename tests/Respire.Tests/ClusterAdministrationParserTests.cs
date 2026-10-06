using System.Text;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ClusterAdministrationParserTests
{
    [Test]
    [Arguments("0")]
    [Arguments("9223372036854775807")]
    [Arguments("9223372036854775808")]
    [Arguments("18446744073709551615")]
    public async Task ReplicaEpochPreservesEntireUnsignedRange(string epoch)
    {
        using var reply = RespValue.Array(RespValue.BulkString($"replica host:6379 slave primary 0 0 {epoch} connected"));
        var node = ClusterAdministrationParser.Replicas(in reply).Single();
        await Assert.That(node.ConfigurationEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture)).IsEqualTo(epoch);
    }

    [Test]
    [Arguments("-1")]
    [Arguments("+1")]
    [Arguments("18446744073709551616")]
    [Arguments("invalid")]
    public async Task InvalidReplicaEpochRemainsAProtocolError(string epoch)
    {
        using var reply = RespValue.Array(RespValue.BulkString($"replica host:6379 slave primary 0 0 {epoch} connected"));
        await Assert.That(() => ClusterAdministrationParser.Replicas(in reply)).ThrowsExactly<RespireProtocolException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MalformedSecondRowReportsItsIndex(bool replicas)
    {
        using var reply = replicas
            ? RespValue.Array(RespValue.BulkString("replica host:6379 slave primary 0 0 1 connected"), RespValue.Integer(9))
            : RespValue.Array(RespValue.Array(RespValue.Integer(0), RespValue.Integer(1),
                RespValue.Array(RespValue.BulkString("host"), RespValue.Integer(6379))), RespValue.Integer(9));
        var error = await Assert.That(() =>
        {
            if (replicas) _ = ClusterAdministrationParser.Replicas(in reply);
            else _ = ClusterAdministrationParser.Slots(in reply);
        }).ThrowsExactly<RespireProtocolException>();
        await Assert.That(error!.Message).Contains("row 1");
        await Assert.That(error.InnerException).IsTypeOf<RespireProtocolException>();
    }

    [Test]
    [Arguments(false)] [Arguments(true)]
    public async Task SlotResultsOwnOptionalAndFutureFields(bool resp3)
    {
        var metadata = Encoding.ASCII.GetBytes(resp3 ? "%2\r\n+hostname\r\n+announced\r\n+future\r\n$2\r\n"
            : "*4\r\n+hostname\r\n+announced\r\n+future\r\n$2\r\n");
        byte[] wire = [.. "*1\r\n*6\r\n:0\r\n:16383\r\n*4\r\n$-1\r\n:6379\r\n+primary\r\n"u8,
            .. metadata, 255, 0, 13, 10, .. "*2\r\n$0\r\n\r\n:0\r\n*3\r\n+?\r\n:6380\r\n+replica\r\n*2\r\n+host\r\n:65535\r\n"u8];
        var position = 0;
        await Assert.That(RespParser.TryParseValue(wire, ref position, out var reply)).IsEqualTo(RespParseStatus.Done);
        var result = ClusterAdministrationParser.Slots(in reply);
        reply.Dispose(); wire.AsSpan().Clear();
        var row = result.Single();
        await Assert.That(row.Slots).IsEqualTo(new RespireClusterSlotRange(0, 16383));
        await Assert.That(row.Primary.Endpoint).IsNull();
        await Assert.That(row.Primary.NodeId).IsEqualTo("primary");
        await Assert.That(row.Primary.Metadata["hostname"].AsString()).IsEqualTo("announced");
        await Assert.That(row.Primary.Metadata["future"].AsBytes()).IsEquivalentTo(new byte[] { 255, 0 });
        await Assert.That(row.Replicas[0].Endpoint).IsEqualTo("");
        await Assert.That(row.Replicas[0].NodeId).IsNull();
        await Assert.That(row.Replicas[0].Port).IsEqualTo(0);
        await Assert.That(row.Replicas[1].Endpoint).IsEqualTo("?");
        await Assert.That(row.Replicas[2].Port).IsEqualTo(65535);
    }

    [Test]
    public async Task ReplicaRowsRetainOwnedFlagsAndSlotAnnotations()
    {
        var bytes = Encoding.ASCII.GetBytes("replica host:6379@16379,hostname slave,future primary 1 2 3 connected 0-5 [6-<-peer] future-token\n");
        var reply = RespValue.Array(RespValue.BulkString(bytes));
        var result = ClusterAdministrationParser.Replicas(in reply);
        reply.Dispose(); bytes.AsSpan().Clear();
        var node = result.Single();
        await Assert.That(node.Id).IsEqualTo("replica");
        await Assert.That(node.Address).IsEqualTo("host:6379@16379,hostname");
        await Assert.That(node.Flags).IsEquivalentTo(["slave", "future"]);
        await Assert.That(node.PrimaryId).IsEqualTo("primary");
        await Assert.That(node.Slots).IsEquivalentTo([new RespireClusterSlotRange(0, 5)]);
        await Assert.That(node.Transitions.Single().PeerNodeId).IsEqualTo("peer");
        await Assert.That(node.AdditionalTokens).IsEquivalentTo(["future-token"]);
    }

    [Test]
    public async Task FutureNodeFieldsAreOwnedInsteadOfRejected()
    {
        byte[] bytes = [255, 0];
        var reply = WithNode(RespValue.Array(RespValue.BulkString("host"), RespValue.Integer(6379),
            RespValue.BulkString("id"), RespValue.Array(), RespValue.BulkString(bytes)));
        var result = ClusterAdministrationParser.Slots(in reply);
        reply.Dispose(); bytes.AsSpan().Clear();
        await Assert.That(result[0].Primary.AdditionalValues.Single().AsBytes()).IsEquivalentTo(new byte[] { 255, 0 });
    }

    [Test]
    [Arguments("BUMPED 0", true, 0UL)]
    [Arguments("STILL 18446744073709551615", false, ulong.MaxValue)]
    public async Task EpochPreservesUnsignedValue(string text, bool bumped, ulong epoch)
    {
        var reply = RespValue.SimpleString(text);
        await Assert.That(ClusterAdministrationParser.Epoch(in reply)).IsEqualTo(new RespireClusterEpochResult(bumped, epoch));
    }

    [Test]
    [Arguments("BUMPED")] [Arguments("STILL -1")] [Arguments("OTHER 1")] [Arguments("BUMPED 1 2")]
    [Arguments("BUMPED 18446744073709551616")] [Arguments("BUMPED  1")] [Arguments("STILL +1")]
    public async Task MalformedEpochIsRejected(string text)
    {
        var reply = RespValue.SimpleString(text);
        await Assert.That(() => ClusterAdministrationParser.Epoch(in reply)).ThrowsExactly<RespireProtocolException>();
    }

    [Test]
    public async Task MalformedTopologyAndReplicaShapesAreRejected()
    {
        var validNode = RespValue.Array(RespValue.BulkString("host"), RespValue.Integer(6379));
        RespValue[] malformed =
        [
            RespValue.Integer(0), RespValue.Array(RespValue.Integer(0)),
            RespValue.Array(RespValue.Array(RespValue.Integer(0), RespValue.Integer(1))),
            Row(RespValue.Integer(-1), RespValue.Integer(1), validNode),
            Row(RespValue.Integer(0), RespValue.Integer(16384), validNode),
            Row(RespValue.Integer(2), RespValue.Integer(1), validNode),
            Row(RespValue.BulkString("0"), RespValue.Integer(1), validNode),
            WithNode(RespValue.Integer(1)), WithNode(RespValue.Array()),
            WithNode(RespValue.Array(RespValue.Integer(1), RespValue.Integer(6379))),
            WithNode(RespValue.Array(RespValue.BulkString("host"), RespValue.Integer(-1))),
            WithNode(RespValue.Array(RespValue.BulkString("host"), RespValue.Integer(65536))),
            WithNode(RespValue.Array(RespValue.BulkString("host"), RespValue.BulkString("6379"))),
            WithNode(RespValue.Array(RespValue.BulkString("host"), RespValue.Integer(6379), RespValue.Integer(1))),
            WithMetadata(RespValue.Integer(0)), WithMetadata(RespValue.Array(RespValue.BulkString("odd"))),
            WithMetadata(RespValue.Array(RespValue.BulkString("duplicate"), RespValue.Integer(1),
                RespValue.BulkString("duplicate"), RespValue.Integer(2))),
        ];
        foreach (var reply in malformed)
            await Assert.That(() => ClusterAdministrationParser.Slots(in reply)).ThrowsExactly<RespireProtocolException>();
        RespValue[] invalidReplicas = [RespValue.Integer(1), RespValue.Array(RespValue.Integer(1)),
            RespValue.Array(RespValue.BulkString("")), RespValue.Array(RespValue.BulkString("incomplete")),
            RespValue.Array(RespValue.BulkString("a :0 slave b 0 0 0 connected\nb :0 slave a 0 0 0 connected"))];
        foreach (var reply in invalidReplicas)
            await Assert.That(() => ClusterAdministrationParser.Replicas(in reply)).ThrowsExactly<RespireProtocolException>();
    }

    private static RespValue Row(RespValue start, RespValue end, RespValue node) => RespValue.Array(RespValue.Array(start, end, node));
    private static RespValue WithNode(RespValue node) => Row(RespValue.Integer(0), RespValue.Integer(1), node);
    private static RespValue WithMetadata(RespValue metadata)
        => WithNode(RespValue.Array(RespValue.BulkString("host"), RespValue.Integer(6379), RespValue.BulkString("id"), metadata));
}
