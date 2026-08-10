using System.Text;
using Respire.Internal;
using TUnit.Core;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Respire.Tests;

public class Utf8StringTests
{
    [Test]
    [Arguments("")]
    [Arguments("OK")]
    [Arguments("Hello World")]
    [Arguments("key:with:colons_and-symbols!@#$%^&*()")]
    [Arguments("héllo wörld — ünïcödé")]
    [Arguments("日本語のテキスト")]
    [Arguments("mixed ascii and 絵文字 🎉 content")]
    public async Task GetString_Memory_RoundTrips(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);

        await Assert.That(Utf8String.GetString(bytes.AsMemory())).IsEqualTo(value);
    }

    [Test]
    [Arguments("")]
    [Arguments("OK")]
    [Arguments("héllo wörld — ünïcödé")]
    [Arguments("日本語のテキスト")]
    public async Task GetString_Span_RoundTrips(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);

        await Assert.That(Utf8String.GetString(bytes.AsSpan())).IsEqualTo(value);
    }

    [Test]
    [Arguments(255)]
    [Arguments(256)]
    [Arguments(257)]
    [Arguments(4096)]
    public async Task GetString_Span_RoundTrips_AcrossStackallocBoundary(int length)
    {
        var value = new string('x', length - 1) + 'é';
        var bytes = Encoding.UTF8.GetBytes(value);

        await Assert.That(Utf8String.GetString(bytes.AsSpan())).IsEqualTo(value);

        var asciiValue = new string('x', length);
        var asciiBytes = Encoding.UTF8.GetBytes(asciiValue);

        await Assert.That(Utf8String.GetString(asciiBytes.AsSpan())).IsEqualTo(asciiValue);
        await Assert.That(Utf8String.GetString(asciiBytes.AsMemory())).IsEqualTo(asciiValue);
    }
}
