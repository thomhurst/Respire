using System.Text;
using Respire.Internal;
using Respire.Protocol;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class StringLcsIndexTests
{
    [Test]
    [Arguments("immediate", false)]
    [Arguments("batch", false)]
    [Arguments("transaction", false)]
    [Arguments("immediate", true)]
    [Arguments("batch", true)]
    [Arguments("transaction", true)]
    public async Task OptionsAndReplies_PreserveRangesLengthOrderAndBothPrefixes(string mode, bool resp3)
    {
        RespireLcsOptions?[] options = [null, new() { MinimumMatchLength = 4 },
            new() { IncludeMatchLength = true }, new() { MinimumMatchLength = 4, IncludeMatchLength = true },
            new() { MinimumMatchLength = 0 }, new() { MinimumMatchLength = long.MaxValue }];
        string[] replies = options.Select(option => Reply(resp3, option?.IncludeMatchLength == true)).ToArray();
        string[] frames = mode == "transaction"
            ? ["+OK\r\n", .. options.Select(_ => "+QUEUED\r\n"), "*6\r\n" + string.Concat(replies)]
            : replies;
        await using var server = new FakeRespServer(frames.Select(Encoding.UTF8.GetBytes).ToArray());
        await using var root = await FakeRespServer.ConnectClientAsync(server.Port);
        var client = root.WithKeyPrefix("tenant:");
        var results = new List<RespireLcsIndexResult>();
        if (mode == "immediate")
        {
            foreach (var option in options) results.Add(await client.Strings.LcsIndexAsync("first", "second", option));
        }
        else
        {
            using var batch = client.CreateBatch();
            await using var transaction = client.CreateTransaction();
            IRespireCommandQueue queue = mode == "batch" ? batch : transaction;
            byte[] first = "first"u8.ToArray(), second = "second"u8.ToArray();
            var pending = options.Select(option => queue.Strings.LcsIndex(first, second, option)).ToArray();
            Array.Fill(first, (byte)'x');
            Array.Fill(second, (byte)'x');
            if (mode == "batch") await batch.ExecuteAsync();
            else await transaction.CommitAsync();
            results.AddRange(pending.Select(result => result.Result));
        }
        for (var i = 0; i < results.Count; i++)
        {
            await Assert.That(results[i].Length).IsEqualTo(6);
            await Assert.That(results[i].Matches.Length).IsEqualTo(2);
            await Assert.That(results[i].Matches[0]).IsEqualTo(new RespireLcsMatch(new(4, 7), new(5, 8),
                options[i]?.IncludeMatchLength == true ? 4 : null));
            await Assert.That(results[i].Matches[1]).IsEqualTo(new RespireLcsMatch(new(2, 3), new(0, 1),
                options[i]?.IncludeMatchLength == true ? 2 : null));
        }
        await Assert.That(server.ReceivedCommands.Where(command => command.StartsWith("LCS")).SequenceEqual([
            "LCS tenant:first tenant:second IDX",
            "LCS tenant:first tenant:second IDX MINMATCHLEN 4",
            "LCS tenant:first tenant:second IDX WITHMATCHLEN",
            "LCS tenant:first tenant:second IDX MINMATCHLEN 4 WITHMATCHLEN",
            "LCS tenant:first tenant:second IDX MINMATCHLEN 0",
            "LCS tenant:first tenant:second IDX MINMATCHLEN 9223372036854775807"])).IsTrue();
    }

    [Test]
    [Arguments(false, 0)]
    [Arguments(true, 0)]
    [Arguments(false, 6)]
    [Arguments(true, 6)]
    public async Task EmptyAndFilteredReplies_KeepTotalLength(bool resp3, int length)
    {
        var frame = (resp3 ? "%2" : "*4") + $"\r\n$3\r\nlen\r\n:{length}\r\n$7\r\nmatches\r\n*0\r\n";
        await using var server = new FakeRespServer(Encoding.UTF8.GetBytes(frame));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var result = await client.Strings.LcsIndexAsync("first", "second");
        await Assert.That(result.Matches).IsEmpty();
        await Assert.That(result.Length).IsEqualTo(length);
    }

    [Test]
    public async Task NegativeMinimum_IsRejectedBeforeSendingOrEnqueueing()
    {
        await using var server = new FakeRespServer(Encoding.UTF8.GetBytes(Reply(false, false)));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var options = new RespireLcsOptions { MinimumMatchLength = -1 };
        await Assert.That(async () => await client.Strings.LcsIndexAsync("first", "second", options))
            .Throws<ArgumentOutOfRangeException>();
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        await Assert.That(() => batch.Strings.LcsIndex("first", "second", options)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => transaction.Strings.LcsIndex("first", "second", options)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task MalformedReplies_AreRejected()
    {
        var matches = RespValue.BulkString("matches");
        var len = RespValue.BulkString("len");
        var range = RespValue.Array(RespValue.Integer(0), RespValue.Integer(1));
        RespValue[] invalid = [RespValue.Integer(0), RespValue.Array(),
            RespValue.Array(matches, RespValue.Array(), len, RespValue.Integer(-1)),
            RespValue.Array(matches, RespValue.Array(), len, RespValue.BulkString("2")),
            RespValue.Array(matches, RespValue.Integer(0), len, RespValue.Integer(0)),
            RespValue.Array(matches, RespValue.Array(), matches, RespValue.Array()),
            RespValue.Array(matches, RespValue.Array(), RespValue.BulkString("unknown"), RespValue.Integer(0)),
            RespValue.Array(matches, RespValue.Array(RespValue.Array(range)), len, RespValue.Integer(2)),
            RespValue.Array(matches, RespValue.Array(RespValue.Array(RespValue.Array(), range)), len, RespValue.Integer(2)),
            RespValue.Array(matches, RespValue.Array(RespValue.Array(
                RespValue.Array(RespValue.Integer(2), RespValue.Integer(1)), range)), len, RespValue.Integer(2)),
            RespValue.Array(matches, RespValue.Array(RespValue.Array(range, range, RespValue.Integer(-1))), len, RespValue.Integer(2))];
        foreach (var value in invalid)
            await Assert.That(() => StringCommands.ParseLcsIndex(value)).Throws<RespireProtocolException>();
    }

    [Test]
    [Arguments("immediate")]
    [Arguments("batch")]
    [Arguments("transaction")]
    public async Task Cancellation_IsPreserved(string mode)
    {
        await using var server = new FakeRespServer(Encoding.UTF8.GetBytes(Reply(false, false)));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        if (mode == "immediate")
            await Assert.That(async () => await client.Strings.LcsIndexAsync("first", "second",
                cancellationToken: cancellation.Token)).Throws<OperationCanceledException>();
        else if (mode == "batch")
        {
            using var batch = client.CreateBatch();
            _ = batch.Strings.LcsIndex("first", "second");
            await Assert.That(async () => await batch.ExecuteAsync(cancellation.Token)).Throws<OperationCanceledException>();
        }
        else
        {
            await using var transaction = client.CreateTransaction();
            _ = transaction.Strings.LcsIndex("first", "second");
            await Assert.That(async () => await transaction.CommitAsync(cancellation.Token)).Throws<OperationCanceledException>();
        }
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task InFlightCancellation_StopsWaitingForReply()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer
        {
            SuppressReply = command =>
            {
                if (!command.StartsWith("LCS")) return false;
                received.TrySetResult();
                return true;
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        var pending = client.Strings.LcsIndexAsync("first", "second", cancellationToken: cancellation.Token).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5))).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task CrossSlotKeys_AreRejectedAfterPrefixing()
    {
        await using var server = new FakeRespServer("*0\r\n"u8.ToArray());
        await using var root = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, Connections = 1,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        var client = root.WithKeyPrefix("tenant:");
        await Assert.That(async () => await client.Strings.LcsIndexAsync("{a}:first", "{b}:second"))
            .Throws<RespireServerException>().WithMessage("CROSSSLOT Keys in request don't hash to the same slot");
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        await Assert.That(() => batch.Strings.LcsIndex("{a}:first", "{b}:second")).Throws<RespireServerException>();
        await Assert.That(() => transaction.Strings.LcsIndex("{a}:first", "{b}:second")).Throws<RespireServerException>();
        _ = transaction.Strings.LcsIndex("{same}:first", "{same}:second");
        await Assert.That(server.ReceivedCommands.Where(command => command != "CLUSTER SLOTS")).IsEmpty();
    }

    [Test]
    [Arguments("immediate")]
    [Arguments("batch")]
    [Arguments("transaction")]
    public async Task ClusterRouting_UsesFirstPrefixedKey(string mode)
    {
        var response = Reply(false, false);
        string[] frames = mode == "transaction"
            ? ["+OK\r\n", "+QUEUED\r\n", "*1\r\n" + response] : [response];
        await using var owner = new FakeRespServer(2, frames.Select(Encoding.UTF8.GetBytes).ToArray());
        // The prefix supplies the hash tag, so slot validation must use resolved keys.
        var slot = ClusterHash.GetSlot("tenant:{same}:first");
        var topology = $"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{owner.Port}\r\n";
        await using var seed = new FakeRespServer(Encoding.UTF8.GetBytes(topology), "-ERR wrong route\r\n"u8.ToArray());
        await using var root = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, Connections = 1,
            Endpoints = [new("127.0.0.1", seed.Port)],
        });
        var client = root.WithKeyPrefix("tenant:{same}:");
        if (mode == "immediate") await client.Strings.LcsIndexAsync("first", "second");
        else if (mode == "batch")
        {
            using var batch = client.CreateBatch();
            _ = batch.Strings.LcsIndex("first", "second");
            await batch.ExecuteAsync();
        }
        else
        {
            await using var transaction = client.CreateTransaction();
            _ = transaction.Strings.LcsIndex("first", "second");
            await transaction.CommitAsync();
        }
        await Assert.That(seed.ReceivedCommands.Single()).IsEqualTo("CLUSTER SLOTS");
        await Assert.That(owner.ReceivedCommands.Single(command => command.StartsWith("LCS")))
            .IsEqualTo("LCS tenant:{same}:first tenant:{same}:second IDX");
    }

    private static string Reply(bool resp3, bool withLength)
        => (resp3 ? "%2" : "*4") + "\r\n$7\r\nmatches\r\n*2\r\n"
            + (withLength ? "*3" : "*2") + "\r\n*2\r\n:4\r\n:7\r\n*2\r\n:5\r\n:8\r\n"
            + (withLength ? ":4\r\n*3" : "*2") + "\r\n*2\r\n:2\r\n:3\r\n*2\r\n:0\r\n:1\r\n"
            + (withLength ? ":2\r\n" : "") + "$3\r\nlen\r\n:6\r\n";
}
