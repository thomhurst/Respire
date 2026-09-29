using System.Text;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SortedSetSinglePopTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task Immediate_ParsesOneEntryOrNullWithoutSendingCount(bool descending, bool nested)
    {
        await using var server = new FakeRespServer(EntryReply(nested), "*0\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var entry = await client.SortedSets.PopAsync("scores", descending);
        var missing = await client.SortedSets.PopAsync("missing", descending);

        await Assert.That(entry).IsEqualTo(new SortedSetEntry("7", 1.5));
        await Assert.That(missing).IsNull();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            $"{Verb(descending)} scores", $"{Verb(descending)} missing",
        });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Typed_DeserializesSingleMemberOrReturnsNull(bool nested)
    {
        await using var server = new FakeRespServer(EntryReply(nested), "*0\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(await client.SortedSets.PopAsync<int>("scores", descending: true))
            .IsEqualTo(new SortedSetEntry<int>(7, 1.5));
        await Assert.That(await client.SortedSets.PopAsync<int>("missing")).IsNull();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[] { "ZPOPMAX scores", "ZPOPMIN missing" });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Batch_CompletesNullableAndTypedResults(bool descending)
    {
        await using var server = new FakeRespServer(
            EntryReply(false), EntryReply(true), "*0\r\n"u8.ToArray(), "*0\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var batch = client.CreateBatch();
        var entry = batch.SortedSets.Pop("scores", descending);
        var typed = batch.SortedSets.Pop<int>("typed", descending);
        var missing = batch.SortedSets.Pop("missing");
        var missingTyped = batch.SortedSets.Pop<int>("missing-typed");
        await batch.ExecuteAsync();

        await Assert.That(entry.Result).IsEqualTo(new SortedSetEntry("7", 1.5));
        await Assert.That(typed.Result).IsEqualTo(new SortedSetEntry<int>(7, 1.5));
        await Assert.That(missing.Result).IsNull();
        await Assert.That(missingTyped.Result).IsNull();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            $"{Verb(descending)} scores", $"{Verb(descending)} typed", "ZPOPMIN missing", "ZPOPMIN missing-typed",
        });
    }

    [Test]
    public async Task Transaction_CompletesSingleResultsInsideExec()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply,
            "+QUEUED\r\n"u8.ToArray(), "+QUEUED\r\n"u8.ToArray(),
            [.. "*2\r\n"u8, .. EntryReply(false), .. "*0\r\n"u8]);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var transaction = client.CreateTransaction();
        var entry = transaction.SortedSets.Pop<int>("scores", descending: true);
        var missing = transaction.SortedSets.Pop("missing");
        await transaction.CommitAsync();

        await Assert.That(entry.Result).IsEqualTo(new SortedSetEntry<int>(7, 1.5));
        await Assert.That(missing.Result).IsNull();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "MULTI", "ZPOPMAX scores", "ZPOPMIN missing", "EXEC",
        });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Immediate_ForwardsCancellation(bool typed)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer
        {
            SuppressReply = _ =>
            {
                received.TrySetResult();
                return true;
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        Task pending = typed
            ? client.SortedSets.PopAsync<int>("scores", cancellationToken: cancellation.Token).AsTask()
            : client.SortedSets.PopAsync("scores", cancellationToken: cancellation.Token).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.That(async () => await pending).Throws<OperationCanceledException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Immediate_PreservesInfiniteScores(bool resp3)
    {
        var positive = resp3 ? "*1\r\n*2\r\n$1\r\n7\r\n,inf\r\n" : "*2\r\n$1\r\n7\r\n$3\r\ninf\r\n";
        var negative = resp3 ? "*1\r\n*2\r\n$1\r\n7\r\n,-inf\r\n" : "*2\r\n$1\r\n7\r\n$4\r\n-inf\r\n";
        await using var server = new FakeRespServer(Encoding.UTF8.GetBytes(positive), Encoding.UTF8.GetBytes(negative));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(await client.SortedSets.PopAsync("scores"))
            .IsEqualTo(new SortedSetEntry("7", double.PositiveInfinity));
        await Assert.That(await client.SortedSets.PopAsync<int>("scores"))
            .IsEqualTo(new SortedSetEntry<int>(7, double.NegativeInfinity));
    }

    [Test]
    public async Task Parsers_RejectMalformedPairShapesAndAcceptNull()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        RespValue[] malformed =
        [
            RespValue.Integer(7),
            RespValue.Array(RespValue.BulkString("7")),
            RespValue.Array(RespValue.Array(Array.Empty<RespValue>())),
            RespValue.Array(RespValue.Array(RespValue.BulkString("7"))),
            RespValue.Array(RespValue.BulkString("7"), RespValue.Double(1.5), RespValue.Integer(2)),
            RespValue.Array(
                RespValue.Array(RespValue.BulkString("7"), RespValue.Double(1.5)),
                RespValue.Array(RespValue.BulkString("9"), RespValue.Double(2.5))),
        ];
        foreach (var reply in malformed)
        {
            await Assert.That(() => SortedSetCommands.ParseEntry(in reply)).Throws<RespireProtocolException>();
            await Assert.That(() => SortedSetCommands.ParseEntry<int>(client, in reply)).Throws<RespireProtocolException>();
        }

        var nullReply = RespValue.Null;
        await Assert.That(SortedSetCommands.ParseEntry(in nullReply)).IsNull();
        await Assert.That(SortedSetCommands.ParseEntry<int>(client, in nullReply)).IsNull();
    }

    [Test]
    [Arguments("invalid")]
    [Arguments("1.5junk")]
    public async Task Immediate_RejectsMalformedScoreAndPreservesNextReply(string score)
    {
        var malformed = Encoding.UTF8.GetBytes($"*2\r\n$1\r\n7\r\n${score.Length}\r\n{score}\r\n");
        await using var server = new FakeRespServer(malformed, EntryReply(false));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(async () => await client.SortedSets.PopAsync("bad"))
            .Throws<RespireProtocolException>();
        await Assert.That(await client.SortedSets.PopAsync("good"))
            .IsEqualTo(new SortedSetEntry("7", 1.5));
    }

    private static string Verb(bool descending) => descending ? "ZPOPMAX" : "ZPOPMIN";

    private static byte[] EntryReply(bool nested)
        => Encoding.UTF8.GetBytes(nested ? "*1\r\n*2\r\n$1\r\n7\r\n,1.5\r\n" : "*2\r\n$1\r\n7\r\n$3\r\n1.5\r\n");
}
