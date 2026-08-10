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
        var keys = await CollectAsync(client.Keys.ScanAsync("user:*", countHint: 12, type: "hash"));

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
    public async Task Scan_RejectsInvalidHintsAndTypesBeforeSending()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(async () => await CollectAsync(client.Keys.ScanAsync(countHint: 0)))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(async () => await CollectAsync(client.Keys.ScanAsync(type: " ")))
            .ThrowsExactly<ArgumentException>();
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
