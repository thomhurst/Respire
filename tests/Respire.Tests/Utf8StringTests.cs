using System.Text;
using System.Runtime.CompilerServices;
using Respire.Internal;
using Respire.Json;
using Respire.Protocol;
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

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(31)]
    [Arguments(32)]
    [Arguments(255)]
    [Arguments(256)]
    [Arguments(257)]
    [Arguments(4096)]
    public async Task GetString_SlicedMemoryAndSpanReadOnlyTheirPayload(int length)
    {
        var expected = new string('x', length);
        var storage = new byte[length + 14];
        Array.Fill(storage, (byte)0xff);
        Encoding.ASCII.GetBytes(expected, storage.AsSpan(7, length));
        await Assert.That(Utf8String.GetString(storage.AsSpan(7, length))).IsEqualTo(expected);
        await Assert.That(Utf8String.GetString(storage.AsMemory(7, length))).IsEqualTo(expected);
    }

    [Test]
    [Arguments(0)]
    [Arguments(255)]
    [Arguments(256)]
    [Arguments(257)]
    [Arguments(4096)]
    public async Task GetString_UnicodeAndNullAfterAsciiPrefixMatchRuntime(int prefixLength)
    {
        var expected = new string('x', prefixLength) + (char)0 + "é🎉" + (char)0;
        var bytes = Encoding.UTF8.GetBytes(expected);
        await Assert.That(Utf8String.GetString(bytes.AsSpan())).IsEqualTo(expected);
        await Assert.That(Utf8String.GetString(bytes.AsMemory())).IsEqualTo(expected);
    }

    [Test]
    [Arguments(new byte[] { 0x80 })]
    [Arguments(new byte[] { 0xc0, 0xaf })]
    [Arguments(new byte[] { 0xe2, 0x82 })]
    [Arguments(new byte[] { 0xf0, 0x80, 0x80, 0x80 })]
    [Arguments(new byte[] { 0xed, 0xa0, 0x80 })]
    [Arguments(new byte[] { 0xf4, 0x90, 0x80, 0x80 })]
    public async Task GetString_InvalidUtf8RetainsReplacementFallback(byte[] invalid)
    {
        // Exercise fallback after a long successful ASCII prefix, not only at byte zero.
        var bytes = new byte[4096 + invalid.Length];
        bytes.AsSpan(0, 4096).Fill((byte)'x');
        invalid.CopyTo(bytes, 4096);
        var expected = Encoding.UTF8.GetString(bytes);
        await Assert.That(Utf8String.GetString(bytes.AsSpan())).IsEqualTo(expected);
        await Assert.That(Utf8String.GetString(bytes.AsMemory())).IsEqualTo(expected);
    }

    [Test]
    [Arguments(typeof(RespWriter))]
    [Arguments(typeof(RespireJsonClient))]
    public async Task PackageModulesDeclareSkippedLocalInitialization(Type packageType)
    {
        await Assert.That(packageType.Module.IsDefined(typeof(SkipLocalsInitAttribute), inherit: false)).IsTrue();
    }
}
