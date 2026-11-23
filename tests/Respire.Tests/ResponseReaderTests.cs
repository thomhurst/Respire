using Respire.Internal;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ResponseReaderTests
{
    [Test]
    public async Task IntegerSentinelReaderPreservesMinusOneAndRejectsOtherTypes()
    {
        RespValue sentinel = RespValue.Integer(-1);
        RespValue zero = RespValue.Integer(0);
        using var text = RespValue.BulkString("0");

        await Assert.That(ResponseReader.IntegerMinusOneOrNull(in sentinel)).IsNull();
        await Assert.That(ResponseReader.IntegerMinusOneOrNull(in zero)).IsEqualTo(0L);
        await Assert.That(() => ResponseReader.IntegerMinusOneOrNull(in text)).ThrowsExactly<RespireProtocolException>();
        await Assert.That(() => ResponseReader.Integer(in RespValue.Null)).ThrowsExactly<RespireProtocolException>();
    }

    [Test]
    public async Task IntegerArrayReadersRequireArraysAndValidateElements()
    {
        using var nullable = RespValue.Array(RespValue.Null, RespValue.Integer(0));
        using var malformed = RespValue.Array(RespValue.Integer(2), RespValue.BulkString("0"));
        RespValue missing = RespValue.Null;

        await Assert.That(ResponseReader.NullableIntegerArray(in nullable)).IsEquivalentTo(new long?[] { null, 0 });
        await Assert.That(() => ResponseReader.IntegerArray(in nullable)).ThrowsExactly<RespireProtocolException>();
        await Assert.That(() => ResponseReader.NullableIntegerArray(in missing)).ThrowsExactly<RespireProtocolException>();
        await Assert.That(() => ResponseReader.NullableIntegerArray(in malformed)).ThrowsExactly<RespireProtocolException>();
        await Assert.That(() => ResponseReader.TtlArray(in malformed)).ThrowsExactly<RespireProtocolException>();
        await Assert.That(() => ResponseReader.HashFieldExpiryResultArray(in malformed)).ThrowsExactly<RespireProtocolException>();
    }

    [Test]
    [Arguments("inf", double.PositiveInfinity)]
    [Arguments("+inf", double.PositiveInfinity)]
    [Arguments("-inf", double.NegativeInfinity)]
    [Arguments("Infinity", double.PositiveInfinity)]
    [Arguments("1.25", 1.25)]
    [Arguments("-2.5e+2", -250)]
    [Arguments("0", 0)]
    [Arguments("1e999", double.PositiveInfinity)]
    [Arguments("-1e999", double.NegativeInfinity)]
    public async Task Double_ReadsResp2NumbersAndInfinitySpellings(string text, double expected)
    {
        using var reply = RespValue.BulkString(text);

        await Assert.That(ResponseReader.Double(in reply)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("")]
    [Arguments("garbage")]
    [Arguments("1.25junk")]
    [Arguments("1e")]
    [Arguments("1.25 ")]
    [Arguments("infjunk")]
    [Arguments("NaNjunk")]
    public async Task Double_RejectsMalformedOrPartialNumbers(string text)
    {
        using var reply = RespValue.BulkString(text);

        await Assert.That(() => ResponseReader.Double(in reply)).Throws<RespireProtocolException>();
        await Assert.That(() => ResponseReader.DoubleOrNull(in reply)).Throws<RespireProtocolException>();
    }

    [Test]
    [Arguments("nan")]
    [Arguments("NaN")]
    public async Task Double_PreservesTextNaN(string text)
    {
        using var reply = RespValue.BulkString(text);

        await Assert.That(double.IsNaN(ResponseReader.Double(in reply))).IsTrue();
    }

    [Test]
    [Arguments(1.25)]
    [Arguments(double.PositiveInfinity)]
    [Arguments(double.NegativeInfinity)]
    [Arguments(double.NaN)]
    public async Task Double_PreservesNativeResp3Values(double expected)
    {
        var reply = RespValue.Double(expected);

        await Assert.That(ResponseReader.Double(in reply).Equals(expected)).IsTrue();
    }

    [Test]
    public async Task NullableReaders_PreserveNullAndEmptyCollections()
    {
        var missing = RespValue.Null;
        using var empty = RespValue.Array([]);
        using var values = RespValue.Array(RespValue.Null, RespValue.Integer(2), RespValue.BulkString("1.25"));

        await Assert.That(() => ResponseReader.Double(in missing)).Throws<RespireProtocolException>();
        await Assert.That(ResponseReader.DoubleOrNull(in missing)).IsNull();
        await Assert.That(ResponseReader.NullableDoubleArray(in empty)).IsEmpty();
        await Assert.That(ResponseReader.NullableDoubleArray(in values)).IsEquivalentTo(new double?[] { null, 2, 1.25 });
    }
}
