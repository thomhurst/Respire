using System.Text;
using Respire.Protocol;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SortedSetRankWithScoreTests
{
    [Test]
    [Arguments("immediate", false)]
    [Arguments("batch", false)]
    [Arguments("transaction", false)]
    [Arguments("immediate", true)]
    [Arguments("batch", true)]
    [Arguments("transaction", true)]
    public async Task RankScoreAndNullReplies_KeepWireOrderAndPrefix(string mode, bool resp3)
    {
        var score = resp3 ? ",2.5\r\n" : "$3\r\n2.5\r\n";
        var ascending = "*2\r\n:2\r\n" + score;
        var descending = "*2\r\n:0\r\n" + score;
        var missing = resp3 ? "_\r\n" : "*-1\r\n";
        string[] frames = mode == "transaction"
            ? new[] { "+OK\r\n", "+QUEUED\r\n", "+QUEUED\r\n", "+QUEUED\r\n", "*3\r\n" + ascending + descending + missing }
            : [ascending, descending, missing];
        await using var server = new FakeRespServer(frames.Select(Encoding.UTF8.GetBytes).ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var view = client.WithKeyPrefix("tenant:");
        SortedSetRank? first, last, absent;
        if (mode == "immediate")
        {
            first = await view.SortedSets.RankWithScoreAsync("key", "member");
            last = await view.SortedSets.RankWithScoreAsync("key", "member", descending: true);
            absent = await view.SortedSets.RankWithScoreAsync("key", "missing");
        }
        else
        {
            using var batch = view.CreateBatch();
            await using var transaction = view.CreateTransaction();
            IRespireCommandQueue queue = mode == "batch" ? batch : transaction;
            var a = queue.SortedSets.RankWithScore("key", "member");
            var d = queue.SortedSets.RankWithScore("key", "member", descending: true);
            var n = queue.SortedSets.RankWithScore("key", "missing");
            if (mode == "batch") await batch.ExecuteAsync();
            else await transaction.CommitAsync();
            (first, last, absent) = (a.Result, d.Result, n.Result);
        }
        await Assert.That(first).IsEqualTo(new SortedSetRank(2, 2.5));
        await Assert.That(last).IsEqualTo(new SortedSetRank(0, 2.5));
        await Assert.That(absent).IsNull();
        await Assert.That(server.ReceivedCommands.Where(c => c.StartsWith("ZR")).SequenceEqual(
            ["ZRANK tenant:key member WITHSCORE", "ZREVRANK tenant:key member WITHSCORE", "ZRANK tenant:key missing WITHSCORE"])).IsTrue();
    }

    [Test]
    public async Task MalformedRanksAndPairs_AreRejected()
    {
        RespValue[] malformed =
        [
            RespValue.Integer(1), RespValue.Array(), RespValue.Array(RespValue.Integer(1)),
            RespValue.Array(RespValue.Integer(1), RespValue.Double(2), RespValue.Integer(3)),
            RespValue.Array(RespValue.Integer(-1), RespValue.Double(2)),
            RespValue.Array(RespValue.BulkString("1"), RespValue.Double(2)),
            RespValue.Array(RespValue.Integer(1), RespValue.BulkString("2.5junk")),
        ];
        foreach (var value in malformed)
            await Assert.That(() => SortedSetCommands.ParseRankWithScore(value)).Throws<RespireProtocolException>();
        var infinity = RespValue.Array(RespValue.Integer(0), RespValue.BulkString("inf"));
        await Assert.That(SortedSetCommands.ParseRankWithScore(infinity)).IsEqualTo(new SortedSetRank(0, double.PositiveInfinity));
    }

    [Test]
    public async Task MemberBytesAndCancellation_ArePreserved()
    {
        await using var server = new FakeRespServer("*2\r\n:0\r\n$1\r\n1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        byte[] member = [0xff, 0, 0x80];
        await client.SortedSets.RankWithScoreAsync("key", member);
        await Assert.That(server.ReceivedArguments.Single()[2].SequenceEqual(member)).IsTrue();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () => await client.SortedSets.RankWithScoreAsync("key", member,
            cancellationToken: cancellation.Token)).Throws<OperationCanceledException>();
    }
}
