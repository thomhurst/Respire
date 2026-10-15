using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class BitmapCommandTests
{
    [Test]
    public async Task BitOpCommand_RoutesByDestinationKey()
    {
        var command = new BitOpCommand(
            RespireCommands.Bitmap.BITOP.Verb,
            "AND",
            "destination-key",
            ["source-key"]);

        await Assert.That(command.TryGetClusterSlot(out var slot)).IsTrue();
        await Assert.That(slot).IsEqualTo(Respire.Internal.ClusterHash.GetSlot("destination-key"));
    }

    [Test]
    public async Task BitFieldCommand_RoutesByKey()
    {
        var command = new BitFieldCommand(
            RespireCommands.Bitmap.BITFIELD.Verb,
            "bitmap-key",
            [RespireBitFieldOperation.Get(RespireBitFieldEncoding.Unsigned(8), RespireBitFieldOffset.Bits(0))]);

        await Assert.That(command.TryGetClusterSlot(out var slot)).IsTrue();
        await Assert.That(slot).IsEqualTo(Respire.Internal.ClusterHash.GetSlot("bitmap-key"));
    }

    [Test]
    public async Task EveryBitmapCommand_WritesExpectedFrameAndParsesReply()
    {
        await using var server = new FakeRespServer(
            ":1\r\n"u8.ToArray(),
            ":0\r\n"u8.ToArray(),
            ":3\r\n"u8.ToArray(),
            ":2\r\n"u8.ToArray(),
            ":8\r\n"u8.ToArray(),
            ":9\r\n"u8.ToArray(),
            ":10\r\n"u8.ToArray(),
            ":-1\r\n"u8.ToArray(),
            ":4\r\n"u8.ToArray(),
            "*4\r\n:1\r\n:2\r\n$-1\r\n:3\r\n"u8.ToArray(),
            "*4\r\n:7\r\n:8\r\n:9\r\n:10\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(await client.Bitmaps.GetAsync("bits", 4)).IsTrue();
        await Assert.That(await client.Bitmaps.SetAsync("bits", 4, true)).IsFalse();
        await Assert.That(await client.Bitmaps.SetBitCountAsync("bits")).IsEqualTo(3);
        await Assert.That(await client.Bitmaps.SetBitCountAsync("bits", 1, 9, RespireBitIndexUnit.Bit)).IsEqualTo(2);
        await Assert.That(await client.Bitmaps.PositionAsync("bits", true)).IsEqualTo(8);
        await Assert.That(await client.Bitmaps.PositionAsync("bits", false, 2)).IsEqualTo(9);
        await Assert.That(await client.Bitmaps.PositionAsync("bits", true, 2, 8, RespireBitIndexUnit.Bit)).IsEqualTo(10);
        await Assert.That(await client.Bitmaps.PositionAsync("missing", true)).IsNull();
        await Assert.That(await client.Bitmaps.OperateAsync(RespireBitOperation.Xor, "dest", "one", "two")).IsEqualTo(4);
        await Assert.That(await client.Bitmaps.FieldAsync(
            "bits",
            RespireBitFieldOperation.Get(RespireBitFieldEncoding.Unsigned(8), RespireBitFieldOffset.Bits(0)),
            RespireBitFieldOperation.SetOverflow(RespireBitFieldOverflow.Fail),
            RespireBitFieldOperation.Increment(RespireBitFieldEncoding.Signed(8), RespireBitFieldOffset.Fields(1), 2),
            RespireBitFieldOperation.Set(RespireBitFieldEncoding.Unsigned(4), RespireBitFieldOffset.Bits(12), 3))).IsEquivalentTo(
                new long?[] { 1, 2, null, 3 }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(await client.Bitmaps.FieldReadOnlyAsync(
            "bits",
            RespireBitFieldOperation.Get(RespireBitFieldEncoding.Signed(1), RespireBitFieldOffset.Bits(0)),
            RespireBitFieldOperation.Get(RespireBitFieldEncoding.Signed(64), RespireBitFieldOffset.Bits(1)),
            RespireBitFieldOperation.Get(RespireBitFieldEncoding.Unsigned(1), RespireBitFieldOffset.Fields(2)),
            RespireBitFieldOperation.Get(RespireBitFieldEncoding.Unsigned(63), RespireBitFieldOffset.Fields(long.MaxValue))))
            .IsEquivalentTo(new long?[] { 7, 8, 9, 10 }, TUnit.Assertions.Enums.CollectionOrdering.Matching);

        await AssertCommands(server.ReceivedCommands,
            "GETBIT bits 4",
            "SETBIT bits 4 1",
            "BITCOUNT bits",
            "BITCOUNT bits 1 9 BIT",
            "BITPOS bits 1",
            "BITPOS bits 0 2",
            "BITPOS bits 1 2 8 BIT",
            "BITPOS missing 1",
            "BITOP XOR dest one two",
            "BITFIELD bits GET u8 0 OVERFLOW FAIL INCRBY i8 #1 2 SET u4 12 3",
            "BITFIELD_RO bits GET i1 0 GET i64 1 GET u1 #2 GET u63 #9223372036854775807");
    }

    [Test]
    public async Task BitmapCommands_RejectInvalidShapesBeforeSending()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(async () => await client.Bitmaps.PositionAsync("bits", true, end: 2))
            .Throws<ArgumentException>();
        await Assert.That(async () => await client.Bitmaps.PositionAsync(
            "bits", true, start: 2, unit: RespireBitIndexUnit.Bit))
            .Throws<ArgumentException>();
        await Assert.That(async () => await client.Bitmaps.OperateAsync(RespireBitOperation.Not, "dest", "a", "b"))
            .Throws<ArgumentException>();
        await Assert.That(async () => await client.Bitmaps.FieldReadOnlyAsync("bits", RespireBitFieldOperation.Set(RespireBitFieldEncoding.Unsigned(8), RespireBitFieldOffset.Bits(0), 1)))
            .Throws<ArgumentException>();
        await Assert.That(async () => await client.Bitmaps.GetAsync("bits", -1))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Bitmaps.SetBitCountAsync("bits", 0, 1, (RespireBitIndexUnit)42))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Bitmaps.FieldAsync("bits", default(RespireBitFieldOperation)))
            .Throws<ArgumentException>();
        await Assert.That(() => RespireBitFieldOperation.SetOverflow((RespireBitFieldOverflow)42))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RespireBitFieldEncoding.Signed(0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RespireBitFieldEncoding.Signed(65)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RespireBitFieldEncoding.Unsigned(0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RespireBitFieldEncoding.Unsigned(64)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RespireBitFieldOperation.Get(default, RespireBitFieldOffset.Bits(0))).Throws<ArgumentException>();
        await Assert.That(() => RespireBitFieldOperation.Set(default, default, 1)).Throws<ArgumentException>();
        await Assert.That(() => RespireBitFieldOperation.Increment(default, default, 1)).Throws<ArgumentException>();
        await Assert.That(() => RespireBitFieldOffset.Bits(-1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RespireBitFieldOffset.Fields(-1)).Throws<ArgumentOutOfRangeException>();

        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TypedOperations_PreserveOffsetsAndValuesInImmediateAndBatchCommands(bool fieldIndex)
    {
        var offset = fieldIndex ? RespireBitFieldOffset.Fields(long.MaxValue) : RespireBitFieldOffset.Bits(long.MaxValue);
        var reply = "*3\r\n:0\r\n:1\r\n:-2\r\n"u8.ToArray();
        await using var server = new FakeRespServer(reply, reply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        RespireBitFieldOperation[] operations =
        [
            RespireBitFieldOperation.Get(RespireBitFieldEncoding.Signed(64), offset),
            RespireBitFieldOperation.Set(RespireBitFieldEncoding.Unsigned(63), offset, long.MaxValue),
            RespireBitFieldOperation.Increment(RespireBitFieldEncoding.Signed(64), offset, long.MinValue),
        ];

        var immediate = await client.Bitmaps.FieldAsync("bits", operations);
        var batch = client.CreateBatch();
        var deferred = batch.Bitmaps.Field("bits", operations);
        await batch.ExecuteAsync();

        await Assert.That(immediate).IsEquivalentTo(new long?[] { 0, 1, -2 }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(deferred.Result).IsEquivalentTo(immediate, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        var wireOffset = fieldIndex ? "#9223372036854775807" : "9223372036854775807";
        var expected = $"BITFIELD bits GET i64 {wireOffset} SET u63 {wireOffset} 9223372036854775807 INCRBY i64 {wireOffset} -9223372036854775808";
        await AssertCommands(server.ReceivedCommands, expected, expected);
        await Assert.That(default(RespireBitFieldOffset)).IsEqualTo(RespireBitFieldOffset.Bits(0));
    }

    private static async Task AssertCommands(IReadOnlyList<string> actual, params string[] expected)
    {
        await Assert.That(actual.Count).IsEqualTo(expected.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            await Assert.That(actual[i]).IsEqualTo(expected[i]);
        }
    }
}
