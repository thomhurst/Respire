using Respire.Internal;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ResponseReaderTests
{
    [Test]
    [Arguments("inf", double.PositiveInfinity)]
    [Arguments("+inf", double.PositiveInfinity)]
    [Arguments("-inf", double.NegativeInfinity)]
    [Arguments("Infinity", double.PositiveInfinity)]
    [Arguments("1.25", 1.25)]
    [Arguments("-2.5e+2", -250)]
    [Arguments("0", 0)]
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
