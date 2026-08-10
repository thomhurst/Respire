using System.Globalization;
using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class StreamManagementCommandTests
{
    [Test]
    public async Task StreamManagementCommands_WriteExpectedFramesAndParseReplies()
    {
        await using var server = new FakeRespServer(
            Integer(2),
            Integer(3),
            Integer(4),
            Integer(1),
            Integer(5),
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            PendingSummaryReply(),
            PendingDetailsReply(),
            StreamInfoReply(),
            GroupInfoReply(),
            ConsumerInfoReply());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(await client.Streams.DeleteAsync("events", "1-0", "2-0")).IsEqualTo(2);
        await Assert.That(await client.Streams.TrimByMaxLengthAsync("events", 100)).IsEqualTo(3);
        await Assert.That(await client.Streams.TrimByMaxLengthAsync("events", 50, approximate: true)).IsEqualTo(4);
        await Assert.That(await client.Streams.DeleteGroupAsync("events", "workers")).IsTrue();
        await Assert.That(await client.Streams.DeleteConsumerAsync("events", "workers", "alice")).IsEqualTo(5);
        await client.Streams.SetGroupPositionAsync("events", "workers", RespireStreamId.New);
        await client.Streams.SetGroupPositionAsync("events", "workers", RespireStreamId.Beginning, entriesRead: 7);

        var pendingSummary = await client.Streams.GetPendingSummaryAsync("events", "workers");
        await Assert.That(pendingSummary.Count).IsEqualTo(3);
        await Assert.That(pendingSummary.SmallestId?.ToString()).IsEqualTo("1-0");
        await Assert.That(pendingSummary.GreatestId?.ToString()).IsEqualTo("3-0");
        await Assert.That(pendingSummary.Consumers).IsEquivalentTo(new[]
        {
            new RespireStreamConsumerPendingCount("alice", 2),
            new RespireStreamConsumerPendingCount("bob", 1),
        });

        var pending = await client.Streams.GetPendingAsync("events", "workers", count: 2, consumer: "alice");
        await Assert.That(pending).IsEquivalentTo(new[]
        {
            new RespireStreamPendingEntry("1-0", "alice", TimeSpan.FromMilliseconds(1200), 2),
            new RespireStreamPendingEntry("3-0", "alice", TimeSpan.FromMilliseconds(50), 1),
        });

        var streamInfo = await client.Streams.GetInfoAsync("events");
        await Assert.That(streamInfo.Length).IsEqualTo(7);
        await Assert.That(streamInfo.RadixTreeKeys).IsEqualTo(1);
        await Assert.That(streamInfo.RadixTreeNodes).IsEqualTo(2);
        await Assert.That(streamInfo.Groups).IsEqualTo(2);
        await Assert.That(streamInfo.LastGeneratedId.ToString()).IsEqualTo("9-0");
        await Assert.That(streamInfo.MaxDeletedEntryId?.ToString()).IsEqualTo("2-0");
        await Assert.That(streamInfo.EntriesAdded).IsEqualTo(12);
        await Assert.That(streamInfo.RecordedFirstEntryId?.ToString()).IsEqualTo("1-0");
        await Assert.That(streamInfo.FirstEntry?.Id.ToString()).IsEqualTo("1-0");
        await Assert.That(streamInfo.FirstEntry?.GetString("name")).IsEqualTo("anna");
        await Assert.That(streamInfo.LastEntry?.Id.ToString()).IsEqualTo("9-0");
        await Assert.That(streamInfo.LastEntry?.GetString("name")).IsEqualTo("zed");

        var groups = await client.Streams.GetGroupInfoAsync("events");
        await Assert.That(groups).IsEquivalentTo(new[]
        {
            new RespireStreamGroupInfo("workers", 2, 3, "3-0", 8, 4),
            new RespireStreamGroupInfo("archivers", 1, 0, "0-0", null, null),
        });

        var consumers = await client.Streams.GetConsumerInfoAsync("events", "workers");
        await Assert.That(consumers).IsEquivalentTo(new[]
        {
            new RespireStreamConsumerInfo("alice", 2, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(75)),
            new RespireStreamConsumerInfo("bob", 1, TimeSpan.FromMilliseconds(500), null),
        });

        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "XDEL events 1-0 2-0",
            "XTRIM events MAXLEN 100",
            "XTRIM events MAXLEN ~ 50",
            "XGROUP DESTROY events workers",
            "XGROUP DELCONSUMER events workers alice",
            "XGROUP SETID events workers $",
            "XGROUP SETID events workers 0 ENTRIESREAD 7",
            "XPENDING events workers",
            "XPENDING events workers - + 2 alice",
            "XINFO STREAM events",
            "XINFO GROUPS events",
            "XINFO CONSUMERS events workers",
        });
    }

    [Test]
    public async Task PendingSummary_ParsesEmptyRedisShape()
    {
        await using var server = new FakeRespServer(Resp(0, null, null, null));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var summary = await client.Streams.GetPendingSummaryAsync("events", "workers");

        await Assert.That(summary.Count).IsEqualTo(0);
        await Assert.That(summary.SmallestId).IsNull();
        await Assert.That(summary.GreatestId).IsNull();
        await Assert.That(summary.Consumers).IsEmpty();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[] { "XPENDING events workers" });
    }

    private static byte[] Integer(long value)
        => Encoding.ASCII.GetBytes($":{value.ToString(CultureInfo.InvariantCulture)}\r\n");

    private static byte[] PendingSummaryReply()
        => Resp(
            3,
            "1-0",
            "3-0",
            Arr(
                Arr("alice", 2),
                Arr("bob", 1)));

    private static byte[] PendingDetailsReply()
        => Resp(
            Arr("1-0", "alice", 1200, 2),
            Arr("3-0", "alice", 50, 1));

    private static byte[] StreamInfoReply()
        => Resp(
            "length", 7,
            "radix-tree-keys", 1,
            "radix-tree-nodes", 2,
            "last-generated-id", "9-0",
            "max-deleted-entry-id", "2-0",
            "entries-added", 12,
            "recorded-first-entry-id", "1-0",
            "groups", 2,
            "first-entry", Arr("1-0", Arr("name", "anna")),
            "last-entry", Arr("9-0", Arr("name", "zed")));

    private static byte[] GroupInfoReply()
        => Resp(
            Arr(
                "name", "workers",
                "consumers", 2,
                "pending", 3,
                "last-delivered-id", "3-0",
                "entries-read", 8,
                "lag", 4),
            Arr(
                "name", "archivers",
                "consumers", 1,
                "pending", 0,
                "last-delivered-id", "0-0",
                "entries-read", null,
                "lag", null));

    private static byte[] ConsumerInfoReply()
        => Resp(
            Arr(
                "name", "alice",
                "pending", 2,
                "idle", 50,
                "inactive", 75),
            Arr(
                "name", "bob",
                "pending", 1,
                "idle", 500));

    private static byte[] Resp(params object?[] values)
    {
        var builder = new StringBuilder();
        WriteArray(builder, values);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static object?[] Arr(params object?[] values) => values;

    private static void WriteArray(StringBuilder builder, object?[] values)
    {
        builder.Append('*').Append(values.Length).Append("\r\n");
        foreach (var value in values)
        {
            WriteValue(builder, value);
        }
    }

    private static void WriteValue(StringBuilder builder, object? value)
    {
        switch (value)
        {
            case null:
                builder.Append("$-1\r\n");
                break;
            case object?[] array:
                WriteArray(builder, array);
                break;
            case int number:
                builder.Append(':').Append(number).Append("\r\n");
                break;
            case long number:
                builder.Append(':').Append(number).Append("\r\n");
                break;
            case string text:
                builder.Append('$')
                    .Append(Encoding.UTF8.GetByteCount(text))
                    .Append("\r\n")
                    .Append(text)
                    .Append("\r\n");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(value), value, "Unsupported RESP test value.");
        }
    }
}
