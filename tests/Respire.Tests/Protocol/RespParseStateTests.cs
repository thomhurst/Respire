using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Protocol;

public class RespParseStateTests
{
    [Test]
    public async Task FragmentedNestedAggregate_ResumesFromConsumedPosition()
    {
        var frame = "*3\r\n:1\r\n*2\r\n+OK\r\n:2\r\n$5\r\nhello\r\n"u8.ToArray();
        using var parser = new RespParseState(int.MaxValue);
        var pos = 0;
        var status = RespParseStatus.NeedMoreData;
        RespValue value = default;

        for (var length = 1; length <= frame.Length; length++)
        {
            status = parser.TryParse(frame.AsSpan(0, length), ref pos, out value, out _);
            if (length < frame.Length)
            {
                await Assert.That(status).IsEqualTo(RespParseStatus.NeedMoreData);
            }
        }

        await Assert.That(status).IsEqualTo(RespParseStatus.Done);
        await Assert.That(pos).IsEqualTo(frame.Length);
        var first = value.AsArray()[0].AsInteger();
        var nestedFirst = value.AsArray()[1].AsArray()[0].AsString();
        var nestedSecond = value.AsArray()[1].AsArray()[1].AsInteger();
        var last = value.AsArray()[2].AsString();
        await Assert.That(first).IsEqualTo(1);
        await Assert.That(nestedFirst).IsEqualTo("OK");
        await Assert.That(nestedSecond).IsEqualTo(2);
        await Assert.That(last).IsEqualTo("hello");
        value.Dispose();
    }

    [Test]
    public async Task ConsumedBulkHeader_IsNotRequiredAfterCompaction()
    {
        using var parser = new RespParseState(int.MaxValue);
        var pos = 0;
        var status = parser.TryParse("$5\r\nhe"u8, ref pos, out _, out _);

        await Assert.That(status).IsEqualTo(RespParseStatus.NeedMoreData);
        await Assert.That(pos).IsEqualTo(4);

        pos = 0;
        status = parser.TryParse("hello\r\n"u8, ref pos, out var value, out _);

        await Assert.That(status).IsEqualTo(RespParseStatus.Done);
        await Assert.That(value.AsString()).IsEqualTo("hello");
        value.Dispose();
    }

    [Test]
    public async Task NestedLargeBulk_RequestsDirectFillAndCompletesAggregate()
    {
        using var parser = new RespParseState(directFillThreshold: 8);
        var pos = 0;
        var status = parser.TryParse("*2\r\n$10\r\n"u8, ref pos, out _, out var request);

        await Assert.That(status).IsEqualTo(RespParseStatus.NeedDirectFill);
        await Assert.That(request.Type).IsEqualTo(RespDataType.BulkString);
        await Assert.That(request.PayloadLength).IsEqualTo(10);

        var filled = RespValue.BulkString("0123456789");
        await Assert.That(parser.SupplyDirectFill(in filled, out _)).IsFalse();

        pos = 0;
        status = parser.TryParse(":42\r\n"u8, ref pos, out var aggregate, out _);

        await Assert.That(status).IsEqualTo(RespParseStatus.Done);
        await Assert.That(aggregate.AsArray()[0].AsString()).IsEqualTo("0123456789");
        await Assert.That(aggregate.AsArray()[1].AsInteger()).IsEqualTo(42);
        aggregate.Dispose();
    }

    [Test]
    public async Task AttributeAcrossSegments_IsDiscardedBeforeReply()
    {
        using var parser = new RespParseState(int.MaxValue);
        var pos = 0;
        var status = parser.TryParse("|1\r\n+key\r\n"u8, ref pos, out _, out _);

        await Assert.That(status).IsEqualTo(RespParseStatus.NeedMoreData);

        pos = 0;
        status = parser.TryParse(":1\r\n:99\r\n"u8, ref pos, out var value, out _);

        await Assert.That(status).IsEqualTo(RespParseStatus.Done);
        await Assert.That(value.AsInteger()).IsEqualTo(99);
    }
}
