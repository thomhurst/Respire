using System.Text;
using Respire.Commands;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ArrayRoutingTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task ClusterMixedArrayPagesUsePrimaryAndRetainQueueOrder(bool typed, bool scanFirst)
    {
        var written = false;
        static byte[] Page(string value) => Encoding.ASCII.GetBytes($"*1\r\n*2\r\n:2\r\n$2\r\n{value}\r\n");
        await using var replica = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("ARSCAN ", StringComparison.Ordinal) ? Page("11") : FakeRespServer.OkReply,
        };
        await using var primary = new FakeRespServer(8, FakeRespServer.OkReply);
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*4\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{primary.Port}\r\n" +
            $"*2\r\n$9\r\n127.0.0.1\r\n:{replica.Port}\r\n");
        primary.ReplyOverride = (_, command) =>
        {
            if (command == "CLUSTER SLOTS") return topology;
            if (command == "ARSET key 2 22") { written = true; return ":1\r\n"u8.ToArray(); }
            return command.StartsWith("ARSCAN ", StringComparison.Ordinal) ? Page(written ? "22" : "11") : FakeRespServer.OkReply;
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, Connections = 1, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", primary.Port)],
        });
        var reads = client.WithReadFrom(RespireReadFrom.Replica);
        using (var readOnly = reads.CreateBatch())
        {
            var page = readOnly.Arrays.ScanPage("key", 2, 2);
            await readOnly.ExecuteAsync();
            await Assert.That(page.Result[0].Value).IsEqualTo("11");
        }
        using var mixed = reads.CreateBatch();
        RespirePending<ulong>? set = null;
        if (!scanFirst) set = mixed.Arrays.Set("key", 2, "22");
        RespirePending<RespireArrayEntry<string>[]>? strings = typed ? null : mixed.Arrays.ScanPage("key", 2, 2);
        RespirePending<RespireArrayEntry<int>[]>? numbers = typed ? mixed.Arrays.ScanPage<int>("key", 2, 2) : null;
        if (scanFirst) set = mixed.Arrays.Set("key", 2, "22");
        await mixed.ExecuteAsync();
        await Assert.That(set!.Result).IsEqualTo(1UL);
        if (typed) await Assert.That(numbers!.Result[0]).IsEqualTo(new RespireArrayEntry<int>(2, scanFirst ? 11 : 22));
        else await Assert.That(strings!.Result[0]).IsEqualTo(new RespireArrayEntry<string>(2, scanFirst ? "11" : "22"));
        var primaryArrays = primary.ReceivedCommands.Where(command => command.StartsWith("AR", StringComparison.Ordinal)).ToArray();
        await Assert.That(primaryArrays).IsEquivalentTo(scanFirst
            ? new[] { "ARSCAN key 2 2", "ARSET key 2 22" }
            : new[] { "ARSET key 2 22", "ARSCAN key 2 2" }, CollectionOrdering.Matching);
        await Assert.That(replica.ReceivedCommands.Count(command => command.StartsWith("ARSCAN ", StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Test]
    public async Task CatalogReadMetadataRoutesArrayReadsAndKeepsWritesOnPrimary()
    {
        var role = Encoding.ASCII.GetBytes("*5\r\n$5\r\nslave\r\n$9\r\n127.0.0.1\r\n:6379\r\n$9\r\nconnected\r\n:0\r\n");
        await using var primary = new FakeRespServer(Encoding.ASCII.GetBytes(":1\r\n"));
        await using var replica = new FakeRespServer(role)
        {
            ReplyOverride = (_, command) => command switch
            {
                "ROLE" => role,
                "ARGET a 2" => Encoding.ASCII.GetBytes("$1\r\n2\r\n"),
                "ARMGET a 2 3" or "ARGETRANGE a 2 3" => Encoding.ASCII.GetBytes("*2\r\n$1\r\n2\r\n$-1\r\n"),
                "ARSCAN a 0 3 LIMIT 1" => Encoding.ASCII.GetBytes("*1\r\n*2\r\n:0\r\n$1\r\n0\r\n"),
                "ARSCAN a 1 3 LIMIT 1" => Encoding.ASCII.GetBytes("*1\r\n*2\r\n:3\r\n$1\r\n3\r\n"),
                "ARGREP a - + EXACT 2" => Encoding.ASCII.GetBytes("*1\r\n:2\r\n"),
                "ARINFO a" => Encoding.ASCII.GetBytes("*2\r\n$5\r\ncount\r\n:1\r\n"),
                "ARLASTITEMS a 1" => Encoding.ASCII.GetBytes("*1\r\n$1\r\n2\r\n"),
                "ARCOUNT a" or "ARLEN a" or "ARNEXT a" or "AROP a 0 3 USED" => Encoding.ASCII.GetBytes(":1\r\n"),
                _ => null,
            },
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1,
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
        });
        var view = client.WithReadFrom(RespireReadFrom.Replica);
        await view.Arrays.GetStringAsync("a", 2);
        await view.Arrays.GetManyAsync("a", 2, 3);
        await view.Arrays.RangeAsync("a", 2, 3);
        await view.Arrays.CountAsync("a");
        await view.Arrays.LengthAsync("a");
        await view.Arrays.NextIndexAsync("a");
        await view.Arrays.LastItemsAsync("a", 1);
        await view.Arrays.InfoAsync("a");
        await view.Arrays.AggregateAsync("a", 0, 3, RespireArrayOperation.Used);
        await view.Arrays.GrepAsync("a", RespireArrayBound.First, RespireArrayBound.Last, [new(RespireArrayPredicateKind.Exact, "2")]);
        List<ulong> indexes = [];
        await foreach (var entry in view.Arrays.ScanAsync("a", 0, 3, pageSize: 1)) indexes.Add(entry.Index);
        await Assert.That(indexes).IsEquivalentTo(new[] { 0UL, 3UL }, CollectionOrdering.Matching);
        await view.Arrays.SetAsync("a", 2, "2");
        await view.Arrays.SeekAsync("a", 3);
        await view.Arrays.DeleteAsync("a", 2);
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(new[] { "ARSET a 2 2", "ARSEEK a 3", "ARDEL a 2" }, CollectionOrdering.Matching);
        await Assert.That(replica.ReceivedCommands.Where(command => command is not "ROLE" and not "READONLY").ToArray()).IsEquivalentTo(new[]
        {
            "ARGET a 2", "ARMGET a 2 3", "ARGETRANGE a 2 3", "ARCOUNT a", "ARLEN a", "ARNEXT a", "ARLASTITEMS a 1",
            "ARINFO a", "AROP a 0 3 USED", "ARGREP a - + EXACT 2", "ARSCAN a 0 3 LIMIT 1", "ARSCAN a 1 3 LIMIT 1",
        }, CollectionOrdering.Matching);
        await Assert.That(RespireCommands.Array.ARSCAN.ReadKind).IsEqualTo(ReadCommandKind.CursorRead);
        await Assert.That(RespireCommands.Array.ARSCAN.CursorArgumentIndex).IsEqualTo(-1);
        await Assert.That(new Cmd1N(RespireCommands.Array.ARSCAN.Verb, "a", [0, 3]).ReadKind).IsEqualTo(RespireCommands.Array.ARSCAN.ReadKind);
    }
}
