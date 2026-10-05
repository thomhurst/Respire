using Respire.Commands;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class HashFieldScanTests
{
    private static readonly byte[] Page = "*2\r\n$20\r\n18446744073709551615\r\n*2\r\n$1\r\na\r\n$1\r\nb\r\n"u8.ToArray();

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task PagesPreserveOptionsCursorAndOwnedFields(int execution)
    {
        byte[][] replies = execution == 2
            ? [FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), "+QUEUED\r\n"u8.ToArray(), [.. "*2\r\n"u8, .. Page, .. Page]]
            : [Page, Page];
        await using var server = new FakeRespServer(replies);
        await using var owner = await FakeRespServer.ConnectClientAsync(server.Port);
        var client = owner.WithKeyPrefix("tenant:");
        using var batch = client.CreateBatch();
        await using var tx = client.CreateTransaction();
        RespireHashScanPage first, next;
        if (execution == 0)
        {
            first = await client.Hashes.ScanFieldsPageAsync("hash");
            next = await client.Hashes.ScanFieldsPageAsync("hash", first.Cursor, "f*", 7);
        }
        else
        {
            IRespireCommandQueue queue = execution == 2 ? tx : batch;
            var pendingFirst = queue.Hashes.ScanFieldsPage("hash");
            var pendingNext = queue.Hashes.ScanFieldsPage("hash", ulong.MaxValue, "f*", 7);
            if (execution == 2) await tx.CommitAsync(); else await batch.ExecuteAsync();
            first = pendingFirst.Result;
            next = pendingNext.Result;
        }
        await Assert.That(first.Cursor).IsEqualTo(ulong.MaxValue);
        await Assert.That(first.IsComplete).IsFalse();
        await Assert.That(next.Fields).IsEquivalentTo(new[] { "a", "b" });
        await Assert.That(first.Fields).IsEquivalentTo(new[] { "a", "b" });
        await Assert.That(server.ReceivedCommands.Where(command => command.StartsWith("HSCAN", StringComparison.Ordinal)).ToArray())
            .IsEquivalentTo(new[] { "HSCAN tenant:hash 0 NOVALUES", "HSCAN tenant:hash 18446744073709551615 MATCH f* COUNT 7 NOVALUES" });
    }

    [Test]
    public async Task EnumerationContinuesThroughEmptyPages()
    {
        await using var server = new FakeRespServer(
            "*2\r\n$2\r\n17\r\n*0\r\n"u8.ToArray(),
            "*2\r\n$1\r\n0\r\n*1\r\n$5\r\nfield\r\n"u8.ToArray());
        await using var owner = await FakeRespServer.ConnectClientAsync(server.Port);
        var fields = new List<string>();
        await foreach (var field in owner.WithKeyPrefix("p:").Hashes.ScanFieldsAsync("hash", "f*", 3)) fields.Add(field);
        await Assert.That(fields.ToArray()).IsEquivalentTo(new[] { "field" });
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "HSCAN p:hash 0 MATCH f* COUNT 3 NOVALUES", "HSCAN p:hash 17 MATCH f* COUNT 3 NOVALUES",
        });
    }

    [Test]
    public async Task ValidationAndCancellationHappenBeforeSending()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(async () => await client.Hashes.ScanFieldsPageAsync("hash", countHint: 0))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Hashes.ScanFieldsPageAsync("hash", cancellationToken: new(true)))
            .Throws<OperationCanceledException>();
        using var batch = client.CreateBatch();
        await using var tx = client.CreateTransaction();
        foreach (IRespireCommandQueue queue in new IRespireCommandQueue[] { batch, tx })
            await Assert.That(() => queue.Hashes.ScanFieldsPage("hash", countHint: -1)).ThrowsExactly<ArgumentOutOfRangeException>();
        await using var scan = client.Hashes.ScanFieldsAsync("hash", cancellationToken: new(true)).GetAsyncEnumerator();
        await Assert.That(async () => await scan.MoveNextAsync()).Throws<OperationCanceledException>();
        await Assert.That(batch.Count).IsEqualTo(0);
        await Assert.That(tx.Count).IsEqualTo(0);
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task ReplicaCursorCannotSwitchToPrimaryInMixedBatch()
    {
        await using var primary = new FakeRespServer(FakeRespServer.OkReply);
        var topology = System.Text.Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{primary.Port}\r\n");
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? topology : null;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = { new RespireEndpoint("127.0.0.1", primary.Port) },
        });
        using var batch = client.WithReadFrom(RespireReadFrom.Replica).CreateBatch();
        var page = batch.Hashes.ScanFieldsPage("key", 7);
        var write = batch.Strings.Set("key", (RespireValue)"value");
        await Assert.That(async () => await batch.ExecuteAsync()).ThrowsExactly<NotSupportedException>();
        await Assert.That(() => page.Result).ThrowsExactly<NotSupportedException>();
        await Assert.That(() => write.Result).ThrowsExactly<NotSupportedException>();
        await Assert.That(primary.ReceivedCommands.Any(command => command.StartsWith("HSCAN", StringComparison.Ordinal)
            || command.StartsWith("SET ", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task CommandPreservesCursorAndKeyRoutingMetadata()
    {
        await using var client = RespireClient.Create(new RespireOptions { Endpoints = { new RespireEndpoint("localhost") } });
        var command = HashCommands.ScanFieldsCommand(client.WithKeyPrefix("p:"), "hash", 17, null, null);
        await Assert.That(command.ReadKind).IsEqualTo(ReadCommandKind.CursorRead);
        await Assert.That(command.CursorArgumentIndex).IsEqualTo(1);
        await Assert.That(command.TryGetClusterSlot(out var slot)).IsTrue();
        await Assert.That(slot).IsEqualTo(ClusterHash.GetSlot("p:hash"));
    }
}
