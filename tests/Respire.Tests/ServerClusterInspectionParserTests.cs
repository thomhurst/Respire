using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ServerClusterInspectionParserTests
{
    [Test]
    public async Task ShardsOwnNestedFieldsAndPreserveUnknownEndpointsRolesAndHealth()
    {
        byte[] payload = [255, 0, 32];
        var extra = RespValue.BulkString(payload);
        var member = RespValue.Array(Text("id"), Text("id"), Text("role"), Text("future-role"),
            Text("health"), Text("future-health"), Text("replication-offset"), RespValue.Integer(-1),
            Text("endpoint"), RespValue.Null, Text("ip"), Text(""), Text("hostname"), Text("?"),
            Text("port"), RespValue.Integer(0), Text("tls-port"), RespValue.Integer(6380),
            Text("future-member"), RespValue.Array(extra));
        var reply = RespValue.Array(RespValue.Array(Text("slots"), RespValue.Array(RespValue.Integer(0), RespValue.Integer(3), RespValue.Integer(8), RespValue.Integer(8)),
            Text("nodes"), RespValue.Array(member), Text("future-shard"), extra));
        var shard = ClusterInspectionParser.Shards(in reply).Single();
        reply.Dispose();
        payload.AsSpan().Clear();
        await Assert.That(shard.Slots).IsEquivalentTo([new RespireClusterSlotRange(0, 3), new RespireClusterSlotRange(8, 8)]);
        await Assert.That(shard.Nodes[0].Endpoint).IsNull();
        await Assert.That(shard.Nodes[0].Ip).IsEqualTo("");
        await Assert.That(shard.Nodes[0].Hostname).IsEqualTo("?");
        await Assert.That(shard.Nodes[0].Port).IsEqualTo(0);
        await Assert.That(shard.Nodes[0].TlsPort).IsEqualTo(6380);
        await Assert.That(shard.Nodes[0].Role).IsEqualTo("future-role");
        await Assert.That(shard.Nodes[0].ReplicationOffset).IsEqualTo(-1);
        await Assert.That(shard.AdditionalFields["future-shard"].AsBytes()).IsEquivalentTo((byte[])[255, 0, 32]);
        await Assert.That(shard.Nodes[0].AdditionalFields["future-member"][0].AsBytes()).IsEquivalentTo((byte[])[255, 0, 32]);
    }

    [Test]
    public async Task NodesPreserveAddressAnnotationsAndUnfamiliarFlags()
    {
        var reply = Text("id ::1:6380@16380,host.example myself,master,new-flag - 10 20 3 connected 0-4 8 [9->-peer-a] [10-<-peer-b] future-token\n" +
            "unknown :0@0 noaddr peer-a 0 0 0 disconnected\n");
        var nodes = ClusterInspectionParser.Nodes(in reply);
        await Assert.That(nodes.Length).IsEqualTo(2);
        await Assert.That(nodes[0].Address).IsEqualTo("::1:6380@16380,host.example");
        await Assert.That(nodes[0].PrimaryId).IsNull();
        await Assert.That(nodes[0].Flags).Contains("new-flag");
        await Assert.That(nodes[0].Slots).IsEquivalentTo([new RespireClusterSlotRange(0, 4), new RespireClusterSlotRange(8, 8)]);
        await Assert.That(nodes[0].Transitions).IsEquivalentTo([
            new RespireClusterSlotTransition(9, "migrating", "peer-a"), new RespireClusterSlotTransition(10, "importing", "peer-b")]);
        await Assert.That(nodes[0].AdditionalTokens).IsEquivalentTo(["future-token"]);
        await Assert.That(nodes[1].Address).IsEqualTo(":0@0");
        await Assert.That(nodes[1].Slots).IsEmpty();
    }

    [Test]
    [Arguments("handshake")]
    [Arguments("noaddr")]
    public async Task NodesPreserveTransientPlaceholders(string flag)
    {
        var reply = Text($"transient - {flag} - 0 0 0 -\n");
        var node = ClusterInspectionParser.Nodes(in reply).Single();
        reply.Dispose();
        await Assert.That(node.Address).IsEqualTo("-");
        await Assert.That(node.Flags).IsEquivalentTo([flag]);
        await Assert.That(node.PrimaryId).IsNull();
        await Assert.That(node.LinkState).IsEqualTo("-");
        await Assert.That(node.Slots).IsEmpty();
        await Assert.That(node.Transitions).IsEmpty();
        await Assert.That(node.AdditionalTokens).IsEmpty();
    }

    [Test]
    public async Task InfoRetainsUnknownFieldsAndOptionalCounters()
    {
        var info = ClusterInspectionParser.Info(Text("cluster_state:ok\r\ncluster_slots_assigned:16384\r\nfuture:value:with:colons\r\n"));
        await Assert.That(info.State).IsEqualTo("ok");
        await Assert.That(info.SlotsAssigned).IsEqualTo(16384);
        await Assert.That(info.CurrentEpoch).IsNull();
        await Assert.That(info.Attributes["future"]).IsEqualTo("value:with:colons");
    }

    [Test]
    public async Task SlotStatsKeepDisabledMetricsNullAndOwnFutureValues()
    {
        byte[] bytes = [255, 0];
        var reply = RespValue.Array(RespValue.Array(RespValue.Integer(4), RespValue.Array(
            Text("key-count"), RespValue.Integer(2), Text("cpu-usec"), RespValue.Integer(9),
            Text("future"), RespValue.BulkString(bytes))));
        var stats = ClusterInspectionParser.SlotStats(in reply).Single();
        bytes.AsSpan().Clear();
        reply.Dispose();
        await Assert.That(stats.Slot).IsEqualTo(4);
        await Assert.That(stats.KeyCount).IsEqualTo(2);
        await Assert.That(stats.CpuMicroseconds).IsEqualTo(9);
        await Assert.That(stats.MemoryBytes).IsNull();
        await Assert.That(stats.AdditionalFields["future"].AsBytes()).IsEquivalentTo((byte[])[255, 0]);
    }

    [Test]
    public async Task LinksRequireTheAssociatedPeerId()
    {
        // Redis emits CLUSTER LINKS entries only for node-associated links. A missing
        // or null peer is malformed, not an unassociated inbound-link placeholder.
        RespValue[] fields = [Text("direction"), Text("from"), Text("create-time"), RespValue.Integer(1234),
            Text("events"), Text("r"), Text("send-buffer-allocated"), RespValue.Integer(512),
            Text("send-buffer-used"), RespValue.Integer(4)];
        using var missing = RespValue.Array(RespValue.Array(fields));
        using var nullPeer = RespValue.Array(RespValue.Array([.. fields, Text("node"), RespValue.Null]));
        using var numericPeer = RespValue.Array(RespValue.Array([.. fields, Text("node"), RespValue.Integer(1)]));
        await Assert.That(() => ClusterInspectionParser.Links(in missing)).ThrowsExactly<RespireProtocolException>();
        var nullError = await Assert.That(() => ClusterInspectionParser.Links(in nullPeer)).ThrowsExactly<RespireProtocolException>();
        await Assert.That(nullError!.Message).Contains("'node'");
        await Assert.That(() => ClusterInspectionParser.Links(in numericPeer)).ThrowsExactly<RespireProtocolException>();
    }

    [Test]
    public async Task MalformedShapesSlotsAndCountsAreRejected()
    {
        await Assert.That(() => ClusterInspectionParser.Shards(RespValue.Array(RespValue.Array(Text("slots")))))
            .ThrowsExactly<RespireProtocolException>();
        await Assert.That(() => ClusterInspectionParser.Shards(RespValue.Array(RespValue.Array(Text("slots"), RespValue.Array(RespValue.Integer(4), RespValue.Integer(2)), Text("nodes"), RespValue.Array()))))
            .ThrowsExactly<RespireProtocolException>();
        await Assert.That(() => ClusterInspectionParser.Info(Text("cluster_state:ok\ncluster_state:fail"))).ThrowsExactly<RespireProtocolException>();
        var counterError = await Assert.That(() => ClusterInspectionParser.Info(Text("cluster_state:ok\ncluster_slots_ok:invalid")))
            .ThrowsExactly<RespireProtocolException>();
        await Assert.That(counterError!.Message).Contains("'cluster_slots_ok'");
        await Assert.That(() => ClusterInspectionParser.Nodes(Text("id address flags - 0 0 0 connected 16384"))).ThrowsExactly<RespireProtocolException>();
        await Assert.That(() => ClusterInspectionParser.Nodes(Text("incomplete"))).ThrowsExactly<RespireProtocolException>();
        await Assert.That(() => ClusterInspectionParser.Slot(RespValue.Integer(-1))).ThrowsExactly<RespireProtocolException>();
        await Assert.That(() => ClusterInspectionParser.NonnegativeInteger(RespValue.Integer(-1))).ThrowsExactly<RespireProtocolException>();
        await Assert.That(() => ClusterInspectionParser.SlotStats(RespValue.Array(RespValue.Array(RespValue.Integer(1))))).ThrowsExactly<RespireProtocolException>();
    }

    [Test]
    [Arguments("0-16384")]
    [Arguments("8192-0")]
    [Arguments("[16384->-peer]")]
    [Arguments("[16384-<-peer]")]
    public async Task NodeSlotRangesAndTransitionsRejectInvalidBounds(string slot)
    {
        await Assert.That(() => ClusterInspectionParser.Nodes(Text($"id address flags - 0 0 0 connected {slot}")))
            .ThrowsExactly<RespireProtocolException>();
    }

    private static RespValue Text(string value) => RespValue.BulkString(value);
}
