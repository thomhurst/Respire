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
    public async Task Double_ReadsResp2NumbersAndInfinitySpellings(string text, double expected)
    {
        using var reply = RespValue.BulkString(text);

        await Assert.That(ResponseReader.Double(in reply)).IsEqualTo(expected);
    }
}
