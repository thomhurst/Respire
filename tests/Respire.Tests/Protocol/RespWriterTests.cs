using System.Text;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Protocol;

public class RespWriterTests
{
    [Test]
    [Arguments("Hello, World!")]
    [Arguments("£ sterling")]
    [Arguments("𐍈")]
    [Arguments("\uD800")]
    public async Task StringBulkValue_MatchesUtf8Encoding(string value)
    {
        var buffer = new WriteBuffer(64);
        var writer = new RespWriter(buffer);

        writer.WriteBulkString(value);
        writer.Complete();

        var payload = Encoding.UTF8.GetBytes(value);
        var expected = Encoding.UTF8.GetBytes($"${payload.Length}\r\n{Encoding.UTF8.GetString(payload)}\r\n");
        var actual = buffer.WrittenMemory.ToArray();
        buffer.Release();
        await Assert.That(actual.SequenceEqual(expected)).IsTrue();
    }

    public static IEnumerable<Func<(RespireValue Value, string Payload)>> FormattedScalars()
    {
        yield return () => (0d, "0");
        yield return () => (5d, "5");
        yield return () => (1.5d, "1.5");
        yield return () => (-0d, "-0");
        yield return () => (double.NaN, "NaN");
        yield return () => (double.PositiveInfinity, "Infinity");
        yield return () => (double.NegativeInfinity, "-Infinity");
        yield return () => (1e300, "1E+300");
        yield return () => (double.Epsilon, "5E-324");
        yield return () => (2.2250738585072009E-308, "2.225073858507201E-308");
        yield return () => (0.1 + 0.2, "0.30000000000000004");
        yield return () => (123456789d, "123456789");
        yield return () => (1234567890d, "1234567890");
        yield return () => (double.MaxValue, "1.7976931348623157E+308");
        yield return () => (-double.MaxValue, "-1.7976931348623157E+308");
        yield return () => (-double.Epsilon, "-5E-324");
        yield return () => (0f, "0");
        yield return () => (-0f, "-0");
        yield return () => (0.1f, "0.1");
        yield return () => (1.1f, "1.1");
        yield return () => (float.NaN, "NaN");
        yield return () => (float.PositiveInfinity, "Infinity");
        yield return () => (float.NegativeInfinity, "-Infinity");
        yield return () => (float.Epsilon, "1E-45");
        yield return () => (16777216f, "16777216");
        yield return () => (float.MaxValue, "3.4028235E+38");
        yield return () => (-float.MaxValue, "-3.4028235E+38");
        yield return () => (0UL, "0");
        yield return () => (9UL, "9");
        yield return () => (10UL, "10");
        yield return () => (999_999_999UL, "999999999");
        yield return () => (1_000_000_000UL, "1000000000");
        yield return () => ((ulong)long.MaxValue + 1, "9223372036854775808");
        yield return () => (ulong.MaxValue, "18446744073709551615");
    }

    [Test]
    [MethodDataSource(nameof(FormattedScalars))]
    public async Task FormattedScalarsKeepTheirExactInvariantWireBytes((RespireValue Value, string Payload) scalar)
    {
        var expected = Encoding.ASCII.GetBytes($"${scalar.Payload.Length}\r\n{scalar.Payload}\r\n");
        await Assert.That(Write(scalar.Value, sizeHint: 0).SequenceEqual(expected)).IsTrue();
        await Assert.That(Write(scalar.Value, scalar.Value.GetWriteSizeHint()).SequenceEqual(expected)).IsTrue();
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(9)]
    [Arguments(10)]
    [Arguments(99)]
    [Arguments(100)]
    [Arguments(999)]
    [Arguments(1000)]
    [Arguments(9999)]
    [Arguments(10000)]
    [Arguments(123456)]
    public async Task BulkHeadersEncodeEveryLengthWidth(int length)
    {
        var text = new string('x', length);
        var expected = Encoding.ASCII.GetBytes($"${length}\r\n{text}\r\n");
        var prefixed = Encoding.ASCII.GetBytes($"${length + 2}\r\np:{text}\r\n");
        var prefix = new KeyPrefix("p:");

        await Assert.That(Write(text, sizeHint: 0).SequenceEqual(expected)).IsTrue();
        await Assert.That(Write(Encoding.ASCII.GetBytes(text), sizeHint: 0).SequenceEqual(expected)).IsTrue();
        await Assert.That(Write(RespireValue.Prefixed(prefix, text, default), sizeHint: 0).SequenceEqual(prefixed)).IsTrue();
        await Assert.That(Write(RespireValue.Prefixed(prefix, null, Encoding.ASCII.GetBytes(text)), sizeHint: 0)
            .SequenceEqual(prefixed)).IsTrue();
        await Assert.That(Write(RespireValue.Prefixed(prefix, "é" + text, default), sizeHint: 0)
            .SequenceEqual(Encoding.UTF8.GetBytes($"${length + 4}\r\np:é{text}\r\n"))).IsTrue();
    }

    private static byte[] Write(RespireValue value, int sizeHint)
    {
        var buffer = new WriteBuffer(16);
        try
        {
            var writer = new RespWriter(buffer, sizeHint);
            value.WriteTo(ref writer);
            writer.Complete();
            return buffer.WrittenMemory.ToArray();
        }
        finally { buffer.Release(); }
    }
}
