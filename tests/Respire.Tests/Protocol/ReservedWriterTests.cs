using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Protocol;

public class ReservedWriterTests
{
    [Test]
    [Arguments(0)]
    [Arguments(17)]
    [Arguments(127)]
    public async Task SerializationLeavesThePreviouslyPublishedCountUnchanged(int leadingLength)
    {
        var buffer = new WriteBuffer(256);
        try
        {
            buffer.Append(new byte[leadingLength]);
            var writer = new RespWriter(buffer);
            writer.WriteArrayHeader(3);
            writer.WriteRaw("$3\r\nSET\r\n"u8);
            writer.WriteBulkString("key");
            writer.WriteBulkString("value");
            await Assert.That(buffer.Count).IsEqualTo(leadingLength);
#if DEBUG
            await Assert.That(() => buffer.WrittenMemory).ThrowsExactly<InvalidOperationException>();
#else
            await Assert.That(buffer.WrittenMemory.Length).IsEqualTo(leadingLength);
#endif
        }
        finally { buffer.Release(); }
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(5)]
    [Arguments(6)]
    [Arguments(7)]
    [Arguments(8)]
    [Arguments(9)]
    [Arguments(10)]
    [Arguments(11)]
    [Arguments(12)]
    [Arguments(13)]
    [Arguments(14)]
    [Arguments(15)]
    [Arguments(16)]
    public async Task CompleteBoundsPreserveEveryCommandShapeWithoutGrowth(int shape)
    {
        RespireValue key = RespireValue.Prefixed(new KeyPrefix("tenant:\uD800"), "\uDC00-é", default);
        RespireValue[] arguments = [key, "a\uD800z", long.MinValue, ulong.MaxValue, double.MaxValue];
        IRespCommand command = shape switch
        {
            0 => new Cmd(RespireCommands.Connection.PING.Verb),
            1 => new Cmd1(Verbs.Get, key),
            2 => new Cmd5(Verbs.Set, arguments[0], arguments[1], arguments[2], arguments[3], arguments[4]),
            3 => new Cmd1N(Verbs.Set, key, arguments[1..]),
            4 => new Cmd2N(Verbs.Set, key, arguments[1], arguments[2..]),
            5 => new DynamicCommand(["SET", .. arguments], 1),
            6 => new CatalogCommand(RespireCommands.String.SET, arguments),
            7 => new SetCommand(key, "a\uD800z", RespireExpiry.In(TimeSpan.FromSeconds(3)), SetWhen.Exists, true),
            8 => new GetExCommand(key, RespireExpiry.At(DateTimeOffset.FromUnixTimeMilliseconds(123456))),
            9 => new Cmd2(Verbs.Set, key, arguments[1]),
            10 => new Cmd3(Verbs.Set, key, arguments[1], arguments[2]),
            11 => new Cmd4(Verbs.Set, key, arguments[1], arguments[2], arguments[3]),
            12 => new CmdN(Verbs.Set, arguments),
            13 => new MSetExCommand(Verbs.Set, arguments),
            14 => new IncrementCommand(Verbs.Set, Verbs.Set, key, 1),
            15 => new IncrementCommand(Verbs.Set, Verbs.Set, key, long.MinValue),
            _ => SnapshotCommand.Create(new Cmd2(Verbs.Set, key, arguments[1])),
        };
        var sizeHint = command.GetWriteSizeHint();
        await Assert.That(sizeHint).IsGreaterThan(0);
        var buffer = new WriteBuffer(sizeHint + 16);
        try
        {
            var leadingLength = buffer.Capacity - sizeHint;
            buffer.Append(Enumerable.Repeat((byte)0xA5, leadingLength).ToArray());
            MemoryMarshal.TryGetArray(buffer.WrittenMemory, out var before);
            var writer = new RespWriter(buffer, sizeHint);
            command.Write(ref writer);
            var unpublishedCount = buffer.Count;
            writer.Complete();
            MemoryMarshal.TryGetArray(buffer.WrittenMemory, out var after);
            await Assert.That(unpublishedCount).IsEqualTo(leadingLength);
            await Assert.That(after.Array).IsSameReferenceAs(before.Array);
            await Assert.That(buffer.Count - leadingLength).IsLessThanOrEqualTo(sizeHint);
            await Assert.That(buffer.WrittenMemory.Span[..leadingLength].IndexOfAnyExcept((byte)0xA5)).IsEqualTo(-1);
            var position = leadingLength;
            await Assert.That(RespParser.TryParseValue(buffer.WrittenMemory.Span, ref position, out var reply))
                .IsEqualTo(RespParseStatus.Done);
            using (reply)
            {
                await Assert.That(position).IsEqualTo(buffer.Count);
                if (shape != 0)
                    await Assert.That(reply.AsArray()[1].AsString()).IsEqualTo("tenant:𐀀-é");
            }
        }
        finally { buffer.Release(); }
    }

    [Test]
    public async Task ScalarAndBinaryArgumentsFitTheirBounds()
    {
        RespireValue[] arguments = ["", "é😀\uD800", new byte[] { 0, 255, 13, 10 },
            long.MinValue, long.MaxValue, ulong.MaxValue, float.MinValue, float.MaxValue,
            double.MinValue, double.MaxValue, double.NaN, double.PositiveInfinity, true, false,
            CommandOptionFrames.WITHSCORESValue];
        var command = new CmdN(Verbs.MGet, arguments);
        var sizeHint = command.GetWriteSizeHint();
        var buffer = new WriteBuffer(sizeHint);
        try
        {
            var writer = new RespWriter(buffer, sizeHint);
            command.Write(ref writer);
            writer.Complete();
            var position = 0;
            await Assert.That(RespParser.TryParseValue(buffer.WrittenMemory.Span, ref position, out var reply))
                .IsEqualTo(RespParseStatus.Done);
            using (reply)
            {
                await Assert.That(position).IsEqualTo(buffer.Count);
                await Assert.That(buffer.Count).IsLessThanOrEqualTo(sizeHint);
                await Assert.That(reply.AsArray().Length).IsEqualTo(arguments.Length + 1);
                for (var index = 0; index < arguments.Length; index++)
                {
                    var payload = new byte[arguments[index].GetWireLength()];
                    arguments[index].WriteWirePayload(payload);
                    await Assert.That(reply.AsArray()[index + 1].AsSpan().SequenceEqual(payload)).IsTrue();
                }
            }
        }
        finally { buffer.Release(); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UnknownTextBoundsGrowWithoutPublishingOrLosingAdjacentBytes(bool prefixed)
    {
        var text = new string('a', 2047) + "é😀";
        RespireValue value = prefixed ? RespireValue.Prefixed(new KeyPrefix("tenant:"), text, default) : text;
        var command = new Cmd2(Verbs.Set, "key", value);
        await Assert.That(command.GetWriteSizeHint()).IsEqualTo(0);
        var buffer = new WriteBuffer(16);
        try
        {
            var capacity = buffer.Capacity;
            buffer.Append(Enumerable.Repeat((byte)0xA5, capacity - 1).ToArray());
            var writer = new RespWriter(buffer, command.GetWriteSizeHint());
            command.Write(ref writer);
            var unpublishedCount = buffer.Count;
            writer.Complete();
            await Assert.That(unpublishedCount).IsEqualTo(capacity - 1);
            await Assert.That(buffer.Capacity).IsGreaterThan(capacity);
            await Assert.That(buffer.WrittenMemory.Span[..(capacity - 1)].IndexOfAnyExcept((byte)0xA5)).IsEqualTo(-1);
            var position = capacity - 1;
            await Assert.That(RespParser.TryParseValue(buffer.WrittenMemory.Span, ref position, out var reply))
                .IsEqualTo(RespParseStatus.Done);
            using (reply)
            {
                await Assert.That(position).IsEqualTo(buffer.Count);
                await Assert.That(reply.AsArray()[2].AsString()).IsEqualTo((prefixed ? "tenant:" : "") + text);
            }
        }
        finally { buffer.Release(); }
    }

    [Test]
    public async Task RejectedBoundsLeavePublishedBytesIntact()
    {
        var buffer = new WriteBuffer(16);
        try
        {
            buffer.Append("+before\r\n"u8);
            await Assert.That(() => CommandWriteSizeHint.Bulk(int.MaxValue)).ThrowsExactly<OverflowException>();
            await Assert.That(() => CommandWriteSizeHint.For(Verbs.Get, int.MaxValue)).ThrowsExactly<OverflowException>();
            await Assert.That(() => { _ = new RespWriter(buffer, -1); }).ThrowsExactly<ArgumentOutOfRangeException>();
            await Assert.That(() => { _ = new RespWriter(buffer, int.MaxValue); }).ThrowsExactly<InvalidOperationException>();
            await Assert.That(buffer.WrittenMemory.ToArray()).IsEquivalentTo("+before\r\n"u8.ToArray());
        }
        finally { buffer.Release(); }
    }

    [Test]
    [NotInParallel]
    public async Task WarmReservedCommandsAllocateNothing()
    {
        var buffer = new WriteBuffer(256);
        var command = new Cmd2(Verbs.Set, "key", "value-é");
        try
        {
            for (var index = 0; index < 32; index++)
            {
                Measure(buffer, command, false);
                Measure(buffer, command, true);
            }
            var measured = AllocationMeasurement.WithoutConcurrentGc(() => (
                Actual: Measure(buffer, command, false), Control: Measure(buffer, command, true)));
            await Assert.That(measured.Actual).IsEqualTo(0);
            await Assert.That(measured.Control).IsGreaterThanOrEqualTo(37_000);
        }
        finally { buffer.Release(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure(WriteBuffer buffer, Cmd2 command, bool control)
    {
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++)
        {
            buffer.Reset();
            var writer = new RespWriter(buffer, command.GetWriteSizeHint());
            command.Write(ref writer);
            writer.Complete();
            if (control) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }
}
