using System.Text;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ClusterMigrationParserTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OwnsBothProtocolsAndPreservesUnknownFields(bool map)
    {
        var fields = Fields();
        fields.Add(RespValue.BulkString("future"));
        fields.Add(RespValue.Array(RespValue.BulkString(new byte[] { 255, 0, 128 })));
        var reply = Parse(fields, map);
        var tasks = ClusterMigrationParser.Tasks(in reply);
        reply.Dispose();
        var task = tasks.Single();
        await Assert.That(task.Id).IsEqualTo("task id");
        await Assert.That(task.Slots).IsEqualTo("0-10 20-30");
        await Assert.That(task.SourceNodeId).IsEqualTo("source");
        await Assert.That(task.DestinationNodeId).IsEqualTo("destination");
        await Assert.That(task.Operation).IsEqualTo("future-operation");
        await Assert.That(task.State).IsEqualTo("future-state");
        await Assert.That(task.LastError).IsEqualTo("");
        await Assert.That(task.Retries).IsEqualTo(2L);
        await Assert.That(task.CreateTime).IsEqualTo(DateTimeOffset.FromUnixTimeMilliseconds(1720000000123));
        await Assert.That(task.StartTime).IsEqualTo(DateTimeOffset.UnixEpoch);
        await Assert.That(task.EndTime).IsNull();
        await Assert.That(task.WritePauseMilliseconds).IsEqualTo(25L);
        await Assert.That(task.AdditionalFields["future"][0].AsBytes()).IsEquivalentTo(new byte[] { 255, 0, 128 });
    }

    [Test]
    public async Task EmptyStatusAndOwnedImportId()
    {
        using var empty = RespValue.Array();
        await Assert.That(ClusterMigrationParser.Tasks(in empty)).IsEmpty();
        byte[] bytes = Encoding.UTF8.GetBytes("task");
        var reply = RespValue.BulkString(bytes);
        var id = ClusterMigrationParser.TaskId(in reply);
        reply.Dispose(); bytes.AsSpan().Clear();
        await Assert.That(id).IsEqualTo("task");
    }

    [Test]
    public async Task UnknownNestedPayloadDoesNotBorrowReplyStorage()
    {
        byte[] bytes = [255, 0, 128];
        var fields = Fields();
        fields.AddRange([RespValue.BulkString("future"), RespValue.Array(RespValue.BulkString(bytes))]);
        var reply = RespValue.Array(RespValue.Array(fields.ToArray()));
        var task = ClusterMigrationParser.Tasks(in reply).Single();
        reply.Dispose(); bytes.AsSpan().Clear();
        await Assert.That(task.AdditionalFields["future"][0].AsBytes()).IsEquivalentTo(new byte[] { 255, 0, 128 });
    }

    [Test]
    [Arguments("outer")]
    [Arguments("row")]
    [Arguments("odd")]
    [Arguments("missing")]
    [Arguments("duplicate")]
    [Arguments("empty-id")]
    [Arguments("text-type")]
    [Arguments("count-type")]
    [Arguments("negative-count")]
    [Arguments("negative-create")]
    [Arguments("invalid-unset")]
    [Arguments("overflow-time")]
    [Arguments("time-type")]
    [Arguments("negative-pause")]
    public async Task RejectsMalformedStatus(string shape)
    {
        var fields = Fields();
        switch (shape)
        {
            case "odd": fields.RemoveAt(fields.Count - 1); break;
            case "missing": fields.RemoveRange(0, 2); break;
            case "duplicate": fields.AddRange([RespValue.BulkString("id"), RespValue.BulkString("other")]); break;
            case "empty-id": fields[1] = RespValue.BulkString(""); break;
            case "text-type": fields[3] = RespValue.Integer(1); break;
            case "count-type": fields[15] = RespValue.BulkString("2"); break;
            case "negative-count": fields[15] = RespValue.Integer(-1); break;
            case "negative-create": fields[17] = RespValue.Integer(-1); break;
            case "invalid-unset": fields[19] = RespValue.Integer(-2); break;
            case "overflow-time": fields[21] = RespValue.Integer(long.MaxValue); break;
            case "time-type": fields[21] = RespValue.BulkString("0"); break;
            case "negative-pause": fields[23] = RespValue.Integer(-1); break;
        }
        using var reply = shape switch
        {
            "outer" => RespValue.Integer(1),
            "row" => RespValue.Array(RespValue.Integer(1)),
            _ => RespValue.Array(RespValue.Array(fields.ToArray())),
        };
        await Assert.That(() => ClusterMigrationParser.Tasks(in reply)).ThrowsExactly<RespireProtocolException>();
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task RejectsMalformedImport(int kind)
    {
        using var value = kind switch { 0 => RespValue.Integer(1), 1 => RespValue.SimpleString("task"), _ => RespValue.BulkString("") };
        await Assert.That(() => ClusterMigrationParser.TaskId(in value)).ThrowsExactly<RespireProtocolException>();
    }

    private static List<RespValue> Fields() =>
    [
        RespValue.BulkString("id"), RespValue.BulkString("task id"),
        RespValue.BulkString("slots"), RespValue.BulkString("0-10 20-30"),
        RespValue.BulkString("source"), RespValue.BulkString("source"),
        RespValue.BulkString("dest"), RespValue.BulkString("destination"),
        RespValue.BulkString("operation"), RespValue.BulkString("future-operation"),
        RespValue.BulkString("state"), RespValue.BulkString("future-state"),
        RespValue.BulkString("last_error"), RespValue.BulkString(""),
        RespValue.BulkString("retries"), RespValue.Integer(2),
        RespValue.BulkString("create_time"), RespValue.Integer(1720000000123),
        RespValue.BulkString("start_time"), RespValue.Integer(0),
        RespValue.BulkString("end_time"), RespValue.Integer(-1),
        RespValue.BulkString("write_pause_ms"), RespValue.Integer(25),
    ];

    private static RespValue Parse(List<RespValue> fields, bool map)
    {
        using var stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes($"*1\r\n{(map ? '%' : '*')}{(map ? fields.Count / 2 : fields.Count)}\r\n"));
        foreach (var field in fields) Write(stream, field);
        var position = 0;
        if (RespParser.TryParseValue(stream.ToArray(), ref position, out var reply) != RespParseStatus.Done)
            throw new InvalidOperationException("Invalid test wire.");
        return reply;
    }

    private static void Write(Stream stream, RespValue field)
    {
        if (field.Type == RespDataType.Integer) stream.Write(Encoding.ASCII.GetBytes($":{field.AsInteger()}\r\n"));
        else if (field.Type == RespDataType.Array)
        {
            stream.Write(Encoding.ASCII.GetBytes($"*{field.AsArray().Length}\r\n"));
            foreach (var child in field.AsArray()) Write(stream, child);
        }
        else
        {
            stream.Write(Encoding.ASCII.GetBytes($"${field.AsSpan().Length}\r\n"));
            stream.Write(field.AsSpan()); stream.Write("\r\n"u8);
        }
    }
}
