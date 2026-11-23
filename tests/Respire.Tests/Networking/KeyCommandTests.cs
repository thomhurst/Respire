using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class KeyCommandTests
{
    [Test]
    public async Task KeyCommands_WriteConditionalCopyAndTypedScanFrames()
    {
        await using var server = new FakeRespServer(
            Bulk("none"),
            Bulk("zset"),
            Bulk("future"),
            FakeRespServer.OkReply,
            ":1\r\n"u8.ToArray(),
            ":0\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray(),
            "*2\r\n$1\r\n0\r\n*2\r\n$2\r\nk1\r\n$2\r\nk2\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(await client.Keys.TypeAsync("missing")).IsEqualTo(RespireKeyType.None);
        await Assert.That(await client.Keys.TypeAsync("sorted")).IsEqualTo(RespireKeyType.SortedSet);
        await Assert.That(await client.Keys.TypeAsync("future")).IsEqualTo(RespireKeyType.Unknown);
        await client.Keys.RenameAsync("old", "new");
        await Assert.That(await client.Keys.TryRenameAsync("old", "new")).IsTrue();
        await Assert.That(await client.Keys.CopyAsync("source", "target")).IsFalse();
        await Assert.That(await client.Keys.CopyAsync("source", "target", replace: true)).IsTrue();
        var keys = await CollectAsync(client.Keys.ScanAsync(
            "user:*", RespireKeyType.Hash, 12, CancellationToken.None));

        await Assert.That(keys).IsEquivalentTo(["k1", "k2"]);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo([
            "TYPE missing",
            "TYPE sorted",
            "TYPE future",
            "RENAME old new",
            "RENAMENX old new",
            "COPY source target",
            "COPY source target REPLACE",
            "SCAN 0 MATCH user:* COUNT 12 TYPE hash",
        ]);
    }

    [Test]
    [Arguments(RespireKeyType.String, "string")]
    [Arguments(RespireKeyType.List, "list")]
    [Arguments(RespireKeyType.Set, "set")]
    [Arguments(RespireKeyType.SortedSet, "zset")]
    [Arguments(RespireKeyType.Hash, "hash")]
    [Arguments(RespireKeyType.Stream, "stream")]
    [Arguments(RespireKeyType.VectorSet, "vectorset")]
    public async Task Scan_TypeFilterUsesRedisToken(RespireKeyType type, string token)
    {
        await using var server = new FakeRespServer("*2\r\n$1\r\n0\r\n*0\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        _ = await CollectAsync(client.Keys.ScanAsync("user:*", type));

        await Assert.That(server.ReceivedCommands[0]).IsEqualTo($"SCAN 0 MATCH user:* COUNT 250 TYPE {token}");
    }

    [Test]
    public async Task Scan_RejectsInvalidHintsAndTypesBeforeSending()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(async () => await CollectAsync(client.Keys.ScanAsync(countHint: 0)))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(async () => await CollectAsync(client.Keys.ScanAsync(type: RespireKeyType.None)))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(async () => await CollectAsync(client.Keys.ScanAsync(type: RespireKeyType.Unknown)))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(async () => await CollectAsync(client.Keys.ScanAsync(type: (RespireKeyType)999)))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task DeferredKeyCommands_MatchTypedImmediateSurface()
    {
        await using var server = new FakeRespServer(
            Bulk("hash"),
            ":1\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var batch = client.CreateBatch();

        var type = batch.Keys.Type("key");
        var renamed = batch.Keys.TryRename("key", "renamed");
        var copied = batch.Keys.Copy("renamed", "copy", replace: true);
        await batch.ExecuteAsync();

        await Assert.That(type.Result).IsEqualTo(RespireKeyType.Hash);
        await Assert.That(renamed.Result).IsTrue();
        await Assert.That(copied.Result).IsTrue();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo([
            "TYPE key",
            "RENAMENX key renamed",
            "COPY renamed copy REPLACE",
        ]);
    }

    [Test]
    [MatrixDataSource]
    public async Task CopyAndRenameRejectCrossSlotBeforeDispatchOrQueueing(
        [Matrix("RENAME", "RENAMENX", "COPY", "COPY REPLACE")] string operation,
        [Matrix("immediate", "batch", "transaction")] string surface,
        [Matrix(false, true)] bool includeKeyContext)
    {
        await using var server = new FakeRespServer();
#pragma warning disable RESP010 // Test-owned client lets the test verify rejection before discovery.
        await using var root = RespireClient.Create(new RespireOptions
        {
            Protocol = RespireProtocol.Resp2,
            UseCluster = true,
            Connections = 1,
            Endpoints = [new("127.0.0.1", server.Port)],
            IncludeCrossSlotKeyContext = includeKeyContext,
        });
#pragma warning restore RESP010
        var client = root.WithKeyPrefix("tenant:");
        RespireKey source = (byte[])[255, .. "source"u8];
        RespireKey destination = (byte[])[0, 128, .. "target"u8];
        RespireKey physicalSource = (byte[])[.. "tenant:"u8, 255, .. "source"u8];
        byte[] physicalDestination = [.. "tenant:"u8, 0, 128, .. "target"u8];
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();

        async Task Invoke()
        {
            if (surface == "immediate")
            {
                _ = await (operation switch
                {
                    "RENAME" => client.Keys.RenameAsync(source, destination),
                    "RENAMENX" => client.Keys.TryRenameAsync(source, destination),
                    _ => client.Keys.CopyAsync(source, destination, replace: operation == "COPY REPLACE"),
                });
                return;
            }

            var keys = surface == "batch" ? batch.Keys : transaction.Keys;
            _ = operation switch
            {
                "RENAME" => keys.Rename(source, destination),
                "RENAMENX" => keys.TryRename(source, destination),
                _ => keys.Copy(source, destination, replace: operation == "COPY REPLACE"),
            };
        }

        var error = (await Assert.That(Invoke).ThrowsExactly<RespireCrossSlotException>())!;
        await Assert.That(error.CommandName).IsEqualTo(operation == "COPY REPLACE" ? "COPY" : operation);
        await Assert.That(error.ExpectedSlot).IsEqualTo(physicalSource.ClusterSlot);
        await Assert.That(error.ConflictingSlot).IsEqualTo(((RespireKey)physicalDestination).ClusterSlot);
        await Assert.That(error.ConflictingKeyHex)
            .IsEqualTo(includeKeyContext ? Convert.ToHexString(physicalDestination) : null);
        await Assert.That(batch.Count).IsEqualTo(0);
        await Assert.That(transaction.Count).IsEqualTo(0);
        _ = transaction.Set("{unrelated}:key", "value");
        await Assert.That(transaction.Count).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task CopyAndRenameRouteByThePrefixHashTag()
    {
        byte[] success = ":1\r\n"u8.ToArray();
        await using var owner = new FakeRespServer(
            FakeRespServer.OkReply, success, success, FakeRespServer.OkReply, success, success);
        RespireKey physicalSource = "{tenant}:{first}:source";
        int slot = physicalSource.ClusterSlot;
        byte[] topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{owner.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
#pragma warning disable RESP010 // Test-owned client uses the scripted cluster slot map.
        await using var root = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespireProtocol.Resp2,
            UseCluster = true,
            Connections = 1,
            Endpoints = [new("127.0.0.1", seed.Port)],
        });
#pragma warning restore RESP010
        var client = root.WithKeyPrefix("{tenant}:");
        const string Source = "{first}:source";
        const string Destination = "{second}:target";
        await Assert.That(await client.Keys.RenameAsync(Source, Destination)).IsTrue();
        await Assert.That(await client.Keys.TryRenameAsync(Source, Destination)).IsTrue();
        await Assert.That(await client.Keys.CopyAsync(Source, Destination)).IsTrue();

        using var batch = client.CreateBatch();
        var renamed = batch.Keys.Rename(Source, Destination);
        var tryRenamed = batch.Keys.TryRename(Source, Destination);
        var copied = batch.Keys.Copy(Source, Destination, replace: true);
        await batch.ExecuteAsync();

        await Assert.That(renamed.Result).IsTrue();
        await Assert.That(tryRenamed.Result).IsTrue();
        await Assert.That(copied.Result).IsTrue();
        await Assert.That(seed.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS"]);
        await Assert.That(owner.ReceivedCommands).IsEquivalentTo([
            "RENAME {tenant}:{first}:source {tenant}:{second}:target",
            "RENAMENX {tenant}:{first}:source {tenant}:{second}:target",
            "COPY {tenant}:{first}:source {tenant}:{second}:target",
            "RENAME {tenant}:{first}:source {tenant}:{second}:target",
            "RENAMENX {tenant}:{first}:source {tenant}:{second}:target",
            "COPY {tenant}:{first}:source {tenant}:{second}:target REPLACE",
        ]);
    }

    private static async Task<string[]> CollectAsync(IAsyncEnumerable<string> source)
    {
        var values = new List<string>();

        await foreach (var value in source)
        {
            values.Add(value);
        }

        return [.. values];
    }

    private static byte[] Bulk(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return Encoding.UTF8.GetBytes($"${bytes.Length}\r\n{value}\r\n");
    }
}
