using System.Text;
using Respire.Protocol;
using TUnit.Core;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Respire.Tests.Protocol;

public class RespParserTests
{
    private static (RespParseStatus Status, RespValue Value, int Consumed) Parse(ReadOnlySpan<byte> data)
    {
        var pos = 0;
        var status = RespParser.TryParseValue(data, ref pos, out var value);
        return (status, value, pos);
    }

    [Test]
    public async Task SimpleString_ParsesAndConsumesAll()
    {
        var (status, value, consumed) = Parse("+OK\r\n"u8);

        await Assert.That(status).IsEqualTo(RespParseStatus.Done);
        await Assert.That(value.Type).IsEqualTo(RespDataType.SimpleString);
        await Assert.That(value.AsString()).IsEqualTo("OK");
        await Assert.That(consumed).IsEqualTo(5);
        value.Dispose();
    }

    [Test]
    public async Task Error_Parses()
    {
        var (status, value, _) = Parse("-ERR unknown command\r\n"u8);

        await Assert.That(status).IsEqualTo(RespParseStatus.Done);
        await Assert.That(value.IsError).IsTrue();
        await Assert.That(value.GetErrorMessage()).IsEqualTo("ERR unknown command");
        value.Dispose();
    }

    [Test]
    public async Task Integer_Parses()
    {
        var (status, value, _) = Parse(":-12345\r\n"u8);

        await Assert.That(status).IsEqualTo(RespParseStatus.Done);
        await Assert.That(value.AsInteger()).IsEqualTo(-12345);
    }

    [Test]
    public async Task BulkString_Parses()
    {
        var (status, value, consumed) = Parse("$11\r\nHello World\r\n"u8);

        await Assert.That(status).IsEqualTo(RespParseStatus.Done);
        await Assert.That(value.Type).IsEqualTo(RespDataType.BulkString);
        await Assert.That(value.AsString()).IsEqualTo("Hello World");
        await Assert.That(consumed).IsEqualTo(18);
        value.Dispose();
    }

    [Test]
    public async Task NullBulkString_ParsesAsNull()
    {
        var (status, value, _) = Parse("$-1\r\n"u8);

        await Assert.That(status).IsEqualTo(RespParseStatus.Done);
        await Assert.That(value.IsNull).IsTrue();
    }

    [Test]
    public async Task EmptyBulkString_Parses()
    {
        var (status, value, _) = Parse("$0\r\n\r\n"u8);

        await Assert.That(status).IsEqualTo(RespParseStatus.Done);
        await Assert.That(value.AsString()).IsEqualTo(string.Empty);
        value.Dispose();
    }

    [Test]
    public async Task Boolean_Parses()
    {
        var (statusTrue, valueTrue, _) = Parse("#t\r\n"u8);
        var (statusFalse, valueFalse, _) = Parse("#f\r\n"u8);

        await Assert.That(statusTrue).IsEqualTo(RespParseStatus.Done);
        await Assert.That(valueTrue.AsBoolean()).IsTrue();
        await Assert.That(statusFalse).IsEqualTo(RespParseStatus.Done);
        await Assert.That(valueFalse.AsBoolean()).IsFalse();
    }

    [Test]
    public async Task Double_Parses()
    {
        var (status, value, _) = Parse(",3.14\r\n"u8);

        await Assert.That(status).IsEqualTo(RespParseStatus.Done);
        await Assert.That(value.AsDouble()).IsEqualTo(3.14);
    }

    [Test]
    public async Task DoubleInfinity_Parses()
    {
        var (_, positive, _) = Parse(",inf\r\n"u8);
        var (_, negative, _) = Parse(",-inf\r\n"u8);

        await Assert.That(positive.AsDouble()).IsEqualTo(double.PositiveInfinity);
        await Assert.That(negative.AsDouble()).IsEqualTo(double.NegativeInfinity);
    }

    [Test]
    public async Task Resp3Null_Parses()
    {
        var (status, value, _) = Parse("_\r\n"u8);

        await Assert.That(status).IsEqualTo(RespParseStatus.Done);
        await Assert.That(value.IsNull).IsTrue();
    }

    [Test]
    public async Task Array_ParsesElements()
    {
        var (status, value, _) = Parse("*3\r\n$3\r\nSET\r\n$3\r\nkey\r\n:42\r\n"u8);

        await Assert.That(status).IsEqualTo(RespParseStatus.Done);
        await Assert.That(value.Type).IsEqualTo(RespDataType.Array);
        var count = value.AsArray().Length;
        var first = value.AsArray()[0].AsString();
        var second = value.AsArray()[1].AsString();
        var third = value.AsArray()[2].AsInteger();
        await Assert.That(count).IsEqualTo(3);
        await Assert.That(first).IsEqualTo("SET");
        await Assert.That(second).IsEqualTo("key");
        await Assert.That(third).IsEqualTo(42);
        value.Dispose();
    }

    [Test]
    public async Task NestedArray_Parses()
    {
        var (status, value, _) = Parse("*2\r\n*2\r\n:1\r\n:2\r\n+OK\r\n"u8);

        await Assert.That(status).IsEqualTo(RespParseStatus.Done);
        var outerCount = value.AsArray().Length;
        var innerCount = value.AsArray()[0].AsArray().Length;
        var innerSecond = value.AsArray()[0].AsArray()[1].AsInteger();
        var outerSecond = value.AsArray()[1].AsString();
        await Assert.That(outerCount).IsEqualTo(2);
        await Assert.That(innerCount).IsEqualTo(2);
        await Assert.That(innerSecond).IsEqualTo(2);
        await Assert.That(outerSecond).IsEqualTo("OK");
        value.Dispose();
    }

    [Test]
    public async Task Map_ParsesAsFlattenedPairs()
    {
        var (status, value, _) = Parse("%2\r\n+first\r\n:1\r\n+second\r\n:2\r\n"u8);

        await Assert.That(status).IsEqualTo(RespParseStatus.Done);
        await Assert.That(value.Type).IsEqualTo(RespDataType.Map);
        var count = value.AsArray().Length;
        var firstKey = value.AsArray()[0].AsString();
        var lastValue = value.AsArray()[3].AsInteger();
        await Assert.That(count).IsEqualTo(4);
        await Assert.That(firstKey).IsEqualTo("first");
        await Assert.That(lastValue).IsEqualTo(2);
        value.Dispose();
    }

    [Test]
    public async Task EmptyArray_Parses()
    {
        var (status, value, _) = Parse("*0\r\n"u8);

        await Assert.That(status).IsEqualTo(RespParseStatus.Done);
        await Assert.That(value.AsArray().Length).IsEqualTo(0);
        value.Dispose();
    }

    [Test]
    public async Task NullArray_ParsesAsNull()
    {
        var (status, value, _) = Parse("*-1\r\n"u8);

        await Assert.That(status).IsEqualTo(RespParseStatus.Done);
        await Assert.That(value.IsNull).IsTrue();
    }

    [Test]
    [Arguments("+OK")]
    [Arguments("+OK\r")]
    [Arguments(":42")]
    [Arguments("$11\r\nHello")]
    [Arguments("$11\r\nHello World\r")]
    [Arguments("*2\r\n:1\r\n")]
    [Arguments("*2\r\n:1\r\n:2")]
    [Arguments("%1\r\n+key\r\n")]
    public async Task PartialFrame_ReturnsNeedMoreData(string partial)
    {
        var (status, _, consumed) = Parse(Encoding.UTF8.GetBytes(partial));

        await Assert.That(status).IsEqualTo(RespParseStatus.NeedMoreData);
        await Assert.That(consumed).IsEqualTo(0);
    }

    [Test]
    public async Task PartialFrame_CompletesAfterMoreData()
    {
        var full = "$5\r\nhello\r\n"u8.ToArray();

        // Every prefix must report NeedMoreData; the full frame must parse.
        for (var length = 1; length < full.Length; length++)
        {
            var pos = 0;
            var status = RespParser.TryParseValue(full.AsSpan(0, length), ref pos, out _);
            await Assert.That(status).IsEqualTo(RespParseStatus.NeedMoreData);
        }

        var (finalStatus, value, _) = Parse(full);
        await Assert.That(finalStatus).IsEqualTo(RespParseStatus.Done);
        await Assert.That(value.AsString()).IsEqualTo("hello");
        value.Dispose();
    }

    [Test]
    public async Task InvalidTypeByte_ReturnsInvalidData()
    {
        var (status, _, _) = Parse("@bogus\r\n"u8);

        await Assert.That(status).IsEqualTo(RespParseStatus.InvalidData);
    }

    [Test]
    public async Task BulkString_MissingCrLfTerminator_ReturnsInvalidData()
    {
        var (status, _, _) = Parse("$5\r\nhelloXX"u8);

        await Assert.That(status).IsEqualTo(RespParseStatus.InvalidData);
    }

    [Test]
    public async Task MultipleValues_ParseSequentially()
    {
        var data = "+OK\r\n:7\r\n$3\r\nfoo\r\n"u8.ToArray();

        var pos = 0;
        var status1 = RespParser.TryParseValue(data, ref pos, out var first);
        var status2 = RespParser.TryParseValue(data, ref pos, out var second);
        var status3 = RespParser.TryParseValue(data, ref pos, out var third);

        await Assert.That(status1).IsEqualTo(RespParseStatus.Done);
        await Assert.That(status2).IsEqualTo(RespParseStatus.Done);
        await Assert.That(status3).IsEqualTo(RespParseStatus.Done);
        await Assert.That(first.AsString()).IsEqualTo("OK");
        await Assert.That(second.AsInteger()).IsEqualTo(7);
        await Assert.That(third.AsString()).IsEqualTo("foo");
        await Assert.That(pos).IsEqualTo(data.Length);
        first.Dispose();
        third.Dispose();
    }

    [Test]
    public async Task VerbatimString_TrimsFormatPrefix()
    {
        var (status, value, _) = Parse("=15\r\ntxt:Some string\r\n"u8);

        await Assert.That(status).IsEqualTo(RespParseStatus.Done);
        await Assert.That(value.Type).IsEqualTo(RespDataType.VerbatimString);
        await Assert.That(value.AsString()).IsEqualTo("Some string");
        value.Dispose();
    }

    [Test]
    public async Task Attribute_IsSkippedBeforeActualReply()
    {
        var (status, value, _) = Parse("|1\r\n+key\r\n:1\r\n:99\r\n"u8);

        await Assert.That(status).IsEqualTo(RespParseStatus.Done);
        await Assert.That(value.AsInteger()).IsEqualTo(99);
    }

    [Test]
    public async Task BigNumber_ParsesAsString()
    {
        var (status, value, _) = Parse("(3492890328409238509324850943850943825024385\r\n"u8);

        await Assert.That(status).IsEqualTo(RespParseStatus.Done);
        await Assert.That(value.Type).IsEqualTo(RespDataType.BigNumber);
        await Assert.That(value.AsString()).IsEqualTo("3492890328409238509324850943850943825024385");
        value.Dispose();
    }

    [Test]
    public async Task TryPeekBulkHeader_ReportsLengthAndHeaderEnd()
    {
        var data = "$100\r\npayload..."u8;

        var found = RespParser.TryPeekBulkHeader(data, 0, out var type, out var length, out var headerEnd);

        await Assert.That(found).IsTrue();
        await Assert.That(type).IsEqualTo(RespDataType.BulkString);
        await Assert.That(length).IsEqualTo(100);
        await Assert.That(headerEnd).IsEqualTo(6);
    }

    [Test]
    public async Task TryPeekBulkHeader_IncompleteHeader_ReturnsFalse()
    {
        var found = RespParser.TryPeekBulkHeader("$100\r"u8, 0, out _, out _, out _);

        await Assert.That(found).IsFalse();
    }
}
