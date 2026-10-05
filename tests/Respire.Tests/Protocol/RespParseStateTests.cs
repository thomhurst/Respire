using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Protocol;

public class RespParseStateTests
{
    [Test]
    [Arguments(4096, false)]
    [Arguments(16384, false)]
    [Arguments(64512, false)]
    [Arguments(4096, true)]
    [Arguments(16384, true)]
    [Arguments(64512, true)]
    public async Task LargeBulkUsesDirectFillOnlyWhenIncomplete(int length, bool nested)
    {
        foreach (var type in new[] { '$', '=', '!' })
        foreach (var missing in new[] { 0, 1, length / 2 })
        {
            var payload = type == '=' ? "txt:" + new string('x', length - 4) : new string('x', length);
            var frame = System.Text.Encoding.ASCII.GetBytes($"{type}{length}\r\n{payload}\r\n");
            using var parser = new RespParseState(4096);
            var pos = 0;
            if (nested)
            {
                await Assert.That(parser.TryParse("*1\r\n"u8, ref pos, out _, out _))
                    .IsEqualTo(RespParseStatus.NeedMoreData);
                pos = 0;
            }

            var status = parser.TryParse(frame.AsSpan(0, frame.Length - missing), ref pos, out var value, out var request);
            if (missing != 0)
            {
                await Assert.That(status).IsEqualTo(RespParseStatus.NeedDirectFill);
                await Assert.That(request.PayloadLength).IsEqualTo(length);
                await Assert.That(pos).IsEqualTo(frame.Length - length - 2);
                continue;
            }

            using (value)
            {
                await Assert.That(status).IsEqualTo(RespParseStatus.Done);
                await Assert.That(pos).IsEqualTo(frame.Length);
                var bulk = nested ? value.AsArray()[0] : value;
                await Assert.That(bulk.Type).IsEqualTo(type switch
                {
                    '$' => RespDataType.BulkString,
                    '=' => RespDataType.VerbatimString,
                    _ => RespDataType.BulkError,
                });
                await Assert.That(bulk.AsSpan().Length).IsEqualTo(type == '=' ? length - 4 : length);
                await Assert.That(parser.IsIdle).IsTrue();
            }
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BufferedLargeBulkRejectsMalformedTerminator(bool nested)
    {
        using var parser = new RespParseState(4096);
        var pos = 0;
        if (nested)
        {
            parser.TryParse("*1\r\n"u8, ref pos, out _, out _);
            pos = 0;
        }
        var frame = System.Text.Encoding.ASCII.GetBytes($"$4096\r\n{new string('x', 4096)}!\n");
        await Assert.That(parser.TryParse(frame, ref pos, out _, out _)).IsEqualTo(RespParseStatus.InvalidData);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FragmentedAttribute_YieldsOnlyWhenEnabled(bool stopAfterAttributes)
    {
        using var parser = new RespParseState(int.MaxValue, stopAfterAttributes);
        var pos = 0;
        var status = parser.TryParse("|1\r\n+key\r\n"u8, ref pos, out _, out _);
        await Assert.That(status).IsEqualTo(RespParseStatus.NeedMoreData);

        pos = 0;
        status = parser.TryParse(":1\r\n:99\r\n"u8, ref pos, out var value, out _);
        if (stopAfterAttributes)
        {
            await Assert.That(status).IsEqualTo(RespParseStatus.SkippedAttribute);
            await Assert.That(pos).IsEqualTo(4);
            await Assert.That(parser.IsIdle).IsTrue();
            status = parser.TryParse(":1\r\n:99\r\n"u8, ref pos, out value, out _);
        }

        await Assert.That(status).IsEqualTo(RespParseStatus.Done);
        await Assert.That(value.AsInteger()).IsEqualTo(99);
        await Assert.That(pos).IsEqualTo(9);
        value.Dispose();
    }

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
