using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ValkeySlotMigrationParserTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task OwnsRowsAndPreservesOptionalAndFutureFields(bool map, bool trackingReplica)
    {
        byte[] bytes = [255, 0, 128];
        var fields = Fields();
        if (!trackingReplica)
            fields.AddRange([Text("source_node"), Text("source"), Text("target_node"), Text("target"),
                Text("remaining_repl_size"), RespValue.Integer(123)]);
        fields.AddRange([Text("future"), RespValue.Array(RespValue.BulkString(bytes))]);
        var storage = System.Buffers.ArrayPool<RespValue>.Shared.Rent(fields.Count);
        fields.CopyTo(storage);
        var reply = RespValue.Array(RespValue.PooledAggregate(map ? RespDataType.Map : RespDataType.Array, storage, fields.Count));
        var result = ValkeySlotMigrationParser.Parse(in reply).Single();
        reply.Dispose();
        bytes.AsSpan().Clear();
        await Assert.That(result.Name).IsEqualTo("job");
        await Assert.That(result.Operation).IsEqualTo("FUTURE");
        await Assert.That(result.SlotRanges).IsEqualTo("0-1 4-5");
        await Assert.That(result.CreateTime).IsEqualTo(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        await Assert.That(result.LastUpdateTime).IsEqualTo(DateTimeOffset.FromUnixTimeSeconds(1_700_000_001));
        await Assert.That(result.LastAcknowledgementTime).IsEqualTo(DateTimeOffset.FromUnixTimeSeconds(1_700_000_002));
        await Assert.That(result.CopyOnWriteBytes).IsEqualTo(42);
        await Assert.That(result.Message).IsEqualTo("");
        await Assert.That(result.IsTerminal).IsFalse();
        await Assert.That(result.SourceNodeId).IsEqualTo(trackingReplica ? null : "source");
        await Assert.That(result.TargetNodeId).IsEqualTo(trackingReplica ? null : "target");
        await Assert.That(result.RemainingReplicationBytes).IsEqualTo(trackingReplica ? (long?)null : 123);
        await Assert.That(result.AdditionalFields["future"][0].AsBytes()).IsEquivalentTo((byte[])[255, 0, 128]);
        foreach (var state in new[] { "success", "failed", "cancelled" })
            await Assert.That((result with { State = state }).IsTerminal).IsTrue();
    }

    [Test]
    [Arguments("outer")]
    [Arguments("row")]
    [Arguments("odd")]
    [Arguments("key-type")]
    [Arguments("missing")]
    [Arguments("duplicate")]
    [Arguments("duplicate-extra")]
    [Arguments("text-type")]
    [Arguments("optional-type")]
    [Arguments("time-type")]
    [Arguments("negative-time")]
    [Arguments("time-overflow")]
    [Arguments("negative-cow")]
    [Arguments("remaining-type")]
    [Arguments("negative-remaining")]
    public async Task RejectsMalformedReplies(string shape)
    {
        var fields = Fields();
        switch (shape)
        {
            case "odd": fields.Add(Text("dangling")); break;
            case "key-type": fields[0] = RespValue.Integer(1); break;
            case "missing": fields.RemoveRange(0, 2); break;
            case "duplicate": fields.AddRange([Text("name"), Text("other")]); break;
            case "duplicate-extra": fields.AddRange([Text("extra"), Text("a"), Text("extra"), Text("b")]); break;
            case "text-type": fields[1] = RespValue.Integer(1); break;
            case "optional-type": fields.AddRange([Text("source_node"), RespValue.Null]); break;
            case "time-type": fields[7] = Text("1700000000"); break;
            case "negative-time": fields[7] = RespValue.Integer(-1); break;
            case "time-overflow": fields[7] = RespValue.Integer(long.MaxValue); break;
            case "negative-cow": fields[^1] = RespValue.Integer(-1); break;
            case "remaining-type": fields.AddRange([Text("remaining_repl_size"), Text("1")]); break;
            case "negative-remaining": fields.AddRange([Text("remaining_repl_size"), RespValue.Integer(-1)]); break;
        }
        using var reply = shape switch
        {
            "outer" => Text("bad"),
            "row" => RespValue.Array(Text("bad")),
            _ => RespValue.Array(RespValue.Array(fields.ToArray())),
        };
        await Assert.That(() => ValkeySlotMigrationParser.Parse(in reply)).ThrowsExactly<RespireProtocolException>();
    }

    [Test]
    public async Task EmptyStatusIsValid()
        => await Assert.That(ValkeySlotMigrationParser.Parse(RespValue.Array())).IsEmpty();

    private static List<RespValue> Fields() => [Text("name"), Text("job"), Text("operation"), Text("FUTURE"),
        Text("slot_ranges"), Text("0-1 4-5"), Text("create_time"), RespValue.Integer(1_700_000_000),
        Text("last_update_time"), RespValue.Integer(1_700_000_001), Text("last_ack_time"), RespValue.Integer(1_700_000_002),
        Text("state"), Text("future-state"), Text("message"), Text(""), Text("cow_size"), RespValue.Integer(42)];
    private static RespValue Text(string value) => RespValue.BulkString(value);
}
