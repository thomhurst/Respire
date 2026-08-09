using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class BitmapCommandTests
{
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
            ":4\r\n"u8.ToArray(),
            "*4\r\n:1\r\n:2\r\n$-1\r\n:3\r\n"u8.ToArray(),
            "*1\r\n:7\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(await client.Bitmaps.GetAsync("bits", 4)).IsTrue();
        await Assert.That(await client.Bitmaps.SetAsync("bits", 4, true)).IsFalse();
        await Assert.That(await client.Bitmaps.CountAsync("bits")).IsEqualTo(3);
        await Assert.That(await client.Bitmaps.CountAsync("bits", 1, 9, BitIndexUnit.Bit)).IsEqualTo(2);
        await Assert.That(await client.Bitmaps.PositionAsync("bits", true)).IsEqualTo(8);
        await Assert.That(await client.Bitmaps.PositionAsync("bits", false, 2)).IsEqualTo(9);
        await Assert.That(await client.Bitmaps.PositionAsync("bits", true, 2, 8, BitIndexUnit.Bit)).IsEqualTo(10);
        await Assert.That(await client.Bitmaps.OperateAsync(BitOperation.Xor, "dest", "one", "two")).IsEqualTo(4);
        await Assert.That(await client.Bitmaps.FieldAsync(
            "bits",
            BitFieldOperation.Get("u8", "0"),
            BitFieldOperation.SetOverflow(BitFieldOverflow.Fail),
            BitFieldOperation.Increment("i8", "#1", 2),
            BitFieldOperation.Set("u4", "12", 3))).IsEquivalentTo(new long?[] { 1, 2, null, 3 });
        await Assert.That(await client.Bitmaps.FieldReadOnlyAsync("bits", BitFieldOperation.Get("u8", "0")))
            .IsEquivalentTo(new long?[] { 7 });

        await AssertCommands(server.ReceivedCommands,
            "GETBIT bits 4",
            "SETBIT bits 4 1",
            "BITCOUNT bits",
            "BITCOUNT bits 1 9 BIT",
            "BITPOS bits 1",
            "BITPOS bits 0 2",
            "BITPOS bits 1 2 8 BIT",
            "BITOP XOR dest one two",
            "BITFIELD bits GET u8 0 OVERFLOW FAIL INCRBY i8 #1 2 SET u4 12 3",
            "BITFIELD_RO bits GET u8 0");
    }

    [Test]
    public async Task BitmapCommands_RejectInvalidShapesBeforeSending()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(async () => await client.Bitmaps.PositionAsync("bits", true, end: 2))
            .Throws<ArgumentException>();
        await Assert.That(async () => await client.Bitmaps.OperateAsync(BitOperation.Not, "dest", "a", "b"))
            .Throws<ArgumentException>();
        await Assert.That(async () => await client.Bitmaps.FieldReadOnlyAsync("bits", BitFieldOperation.Set("u8", "0", 1)))
            .Throws<ArgumentException>();
        await Assert.That(async () => await client.Bitmaps.GetAsync("bits", -1))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Bitmaps.CountAsync("bits", 0, 1, (BitIndexUnit)42))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Bitmaps.FieldAsync("bits", default(BitFieldOperation)))
            .Throws<ArgumentException>();
        await Assert.That(() => BitFieldOperation.SetOverflow((BitFieldOverflow)42))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(server.ReceivedCommands).IsEmpty();
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
