using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class RespireResultTests
{
    [Test]
    public async Task AsDouble_ReturnsRespDoublesUnchanged()
    {
        using var result = new RespireResult(RespValue.Double(3.5));

        await Assert.That(result.AsDouble()).IsEqualTo(3.5);
    }

    [Test]
    [Arguments("3.14", 3.14)]
    [Arguments("-0.5", -0.5)]
    [Arguments("42", 42d)]
    public async Task AsDouble_ParsesNumericTextWithInvariantCulture(string reply, double expected)
    {
        using var result = new RespireResult(RespValue.BulkString(reply));

        await Assert.That(result.AsDouble()).IsEqualTo(expected);
    }

    [Test]
    public async Task AsDouble_ParsesIntegerReplies()
    {
        using var result = new RespireResult(RespValue.Integer(7));

        await Assert.That(result.AsDouble()).IsEqualTo(7d);
    }

    [Test]
    [Arguments("not-a-number")]
    [Arguments("1,5")]
    [Arguments("")]
    public async Task AsDouble_ThrowsWhenTheReplyIsNotANumber(string reply)
    {
        using var result = new RespireResult(RespValue.BulkString(reply));

        await Assert.That(() => result.AsDouble()).Throws<FormatException>();
    }

    [Test]
    public async Task AsDouble_ThrowsForNullReplies()
    {
        using var result = new RespireResult(RespValue.Null);

        await Assert.That(() => result.AsDouble()).Throws<FormatException>();
    }
}
