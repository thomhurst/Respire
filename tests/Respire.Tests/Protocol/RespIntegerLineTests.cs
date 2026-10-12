using System.Globalization;
using System.Text;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Protocol;

/// <summary>
/// The single-pass integer header reader must agree with scanning for CRLF and then
/// parsing the line with Utf8Parser, for every status, value and consumed length.
/// </summary>
public class RespIntegerLineTests
{
    private static readonly string[] EdgeLines =
    [
        "0", "-0", "00", "-00", "007", "-007", "1", "-1", "9", "-9", "10", "-10",
        "123456789012345678", "-123456789012345678",
        "999999999999999999", "-999999999999999999",
        "1000000000000000000", "-1000000000000000000",
        "000000000000000000", "0000000000000000000", "00000000000000000001",
        "9223372036854775807", "-9223372036854775808",
        "9223372036854775808", "-9223372036854775809",
        "99999999999999999999", "-99999999999999999999",
        "", "-", "+", "+0", "+1", "+-1", "--1", "-+1", "1-", " 1", "1 ", "1a", "a1",
        "1.0", "1e3", "0x10", "١", "1\r", "1\n", "-\r",
    ];

    private static (RespParseStatus Status, long Value, int Pos) Expected(byte[] buffer, int start)
    {
        var pos = start;
        if (!RespParser.TryReadLine(buffer, ref pos, out var line))
            return (RespParseStatus.NeedMoreData, 0, start);
        if (!RespParser.TryParseInt64(line, out var value))
            return (RespParseStatus.InvalidData, 0, start);
        return (RespParseStatus.Done, value, pos);
    }

    private static (RespParseStatus Status, long Value, int Pos) Actual(byte[] buffer, int start)
    {
        var pos = start;
        var status = RespParser.TryReadInt64Line(buffer, ref pos, out var value);
        return (status, status == RespParseStatus.Done ? value : 0, pos);
    }

    // Collects mismatches so large input sweeps assert once.
    private static void Check(byte[] buffer, int start, List<string> mismatches)
    {
        var expected = Expected(buffer, start);
        var actual = Actual(buffer, start);
        if (actual != expected)
        {
            mismatches.Add($"[{Convert.ToHexString(buffer)}] from {start}: expected {expected}, got {actual}");
        }
    }

    // Every prefix exercises NeedMoreData at each split position; the trailing reply
    // checks that only the header line is consumed.
    private static void CheckAllSplits(string line, List<string> mismatches)
    {
        var full = Encoding.UTF8.GetBytes(":" + line + "\r\n:7\r\n");
        for (var length = 1; length <= full.Length; length++)
        {
            Check(full[..length], 1, mismatches);
        }
    }

    [Test]
    public async Task EdgeLines_MatchScanThenParse_AtEverySplit()
    {
        var mismatches = new List<string>();
        foreach (var line in EdgeLines)
        {
            CheckAllSplits(line, mismatches);
        }

        await Assert.That(mismatches).IsEmpty();
    }

    [Test]
    public async Task Int64Boundaries_ParseExactly()
    {
        long[] values =
        [
            0, 1, -1, long.MaxValue, long.MinValue, long.MaxValue - 1, long.MinValue + 1,
            999_999_999_999_999_999, -999_999_999_999_999_999,
            1_000_000_000_000_000_000, -1_000_000_000_000_000_000,
        ];
        foreach (var expected in values)
        {
            var buffer = Encoding.ASCII.GetBytes(expected.ToString(CultureInfo.InvariantCulture) + "\r\n");
            await Assert.That(Actual(buffer, 0)).IsEqualTo((RespParseStatus.Done, expected, buffer.Length));
        }
    }

    [Test]
    public async Task StartAtEndOfBuffer_NeedsMoreData()
    {
        var buffer = "*"u8.ToArray();
        await Assert.That(Actual(buffer, 1)).IsEqualTo((RespParseStatus.NeedMoreData, 0L, 1));
    }

    [Test]
    public async Task RandomNumbers_MatchScanThenParse()
    {
        var mismatches = new List<string>();
        var random = new Random(0x5EED);
        for (var i = 0; i < 20_000; i++)
        {
            var digits = random.Next(1, 22);
            var builder = new StringBuilder();
            if (random.Next(3) == 0)
                builder.Append('-');
            for (var d = 0; d < digits; d++)
                builder.Append((char)('0' + random.Next(10)));
            var bytes = Encoding.ASCII.GetBytes(builder.Append("\r\n").ToString());
            Check(bytes, 0, mismatches);
            Check(bytes[..random.Next(bytes.Length)], 0, mismatches);
        }

        await Assert.That(mismatches).IsEmpty();
    }

    [Test]
    public async Task RandomBytes_MatchScanThenParse()
    {
        // A small alphabet keeps CR/LF, signs and digits frequent enough to reach every branch.
        var alphabet = "0123456789-+\r\n x"u8.ToArray();
        var mismatches = new List<string>();
        var random = new Random(0xF00D);
        for (var i = 0; i < 50_000; i++)
        {
            var bytes = new byte[random.Next(0, 24)];
            for (var b = 0; b < bytes.Length; b++)
                bytes[b] = alphabet[random.Next(alphabet.Length)];
            Check(bytes, 0, mismatches);
            Check(bytes, random.Next(bytes.Length + 1), mismatches);
        }

        await Assert.That(mismatches).IsEmpty();
    }

    [Test]
    [Arguments(":-0\r\n", 0L)]
    [Arguments(":007\r\n", 7L)]
    [Arguments(":+5\r\n", 5L)]
    [Arguments(":9223372036854775807\r\n", long.MaxValue)]
    [Arguments(":-9223372036854775808\r\n", long.MinValue)]
    public async Task IntegerReply_ParsesThroughParser(string wire, long expected)
    {
        var buffer = Encoding.ASCII.GetBytes(wire);
        var pos = 0;
        var status = RespParser.TryParseValue(buffer, ref pos, out var value);
        await Assert.That(status).IsEqualTo(RespParseStatus.Done);
        await Assert.That(value.AsInteger()).IsEqualTo(expected);
        await Assert.That(pos).IsEqualTo(buffer.Length);
    }

    [Test]
    [Arguments(":9223372036854775808\r\n")]
    [Arguments(":-\r\n")]
    [Arguments(":\r\n")]
    [Arguments("$1x\r\nab\r\n")]
    [Arguments("*--1\r\n")]
    public async Task MalformedHeaders_AreInvalid(string wire)
    {
        var buffer = Encoding.ASCII.GetBytes(wire);
        var pos = 0;
        var status = RespParser.TryParseValue(buffer, ref pos, out _);
        await Assert.That(status).IsEqualTo(RespParseStatus.InvalidData);
        await Assert.That(pos).IsEqualTo(0);
    }

    [Test]
    [Arguments(",1.5\r\n", 1.5)]
    [Arguments(",-0.25\r\n", -0.25)]
    [Arguments(",inf\r\n", double.PositiveInfinity)]
    [Arguments(",-inf\r\n", double.NegativeInfinity)]
    public async Task DoubleReply_ParsesFiniteAndSpecialSpellings(string wire, double expected)
    {
        var buffer = Encoding.ASCII.GetBytes(wire);
        var pos = 0;
        var status = RespParser.TryParseValue(buffer, ref pos, out var value);
        await Assert.That(status).IsEqualTo(RespParseStatus.Done);
        await Assert.That(value.AsDouble()).IsEqualTo(expected);
    }

    [Test]
    public async Task DoubleReply_ParsesNaN_AndRejectsOtherWords()
    {
        var pos = 0;
        var status = RespParser.TryParseValue(",nan\r\n"u8, ref pos, out var value);
        await Assert.That(status).IsEqualTo(RespParseStatus.Done);
        await Assert.That(double.IsNaN(value.AsDouble())).IsTrue();

        pos = 0;
        status = RespParser.TryParseValue(",infinite\r\n"u8, ref pos, out _);
        await Assert.That(status).IsEqualTo(RespParseStatus.InvalidData);
    }
}
