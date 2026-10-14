using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Unicode;
using Respire.Internal;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ArgumentFormattingTests
{
    private static RespireKey[] Keys() =>
    [
        default, "", "hello £ 😀", "\uD800", new string('x', 4096),
        new RespireKey("hello £ 😀"u8.ToArray()), new RespireKey(new byte[] { 0xff, 0xe2, 0x82 }),
        new RespireKey("\uDE00tail").Prepend(new KeyPrefix("prefix\uD83D")),
        new RespireKey(new byte[] { 0x98, 0x80, 0xff }).Prepend(new KeyPrefix(new byte[] { 0xf0, 0x9f })),
        new RespireKey("\uD800tail").Prepend(new KeyPrefix(new byte[] { 0xff, 0xe2 })),
        new RespireKey("\uD800tail").Prepend(new KeyPrefix("text:")),
        new RespireKey("\uD800tail").Prepend(new KeyPrefix("\uDC00prefix:")),
        new RespireKey(new byte[] { 0xff }).Prepend(new KeyPrefix("text:")),
    ];

    private static RespireValue[] Values() =>
    [
        default, "", "hello £ 😀", long.MinValue, long.MaxValue, ulong.MaxValue,
        float.Epsilon, float.MaxValue, double.Epsilon, double.MaxValue, -0.0,
        double.NaN, double.PositiveInfinity, double.NegativeInfinity, true, false,
        RespireValue.PreEncodedOption("NX", "$2\r\nNX\r\n"u8.ToArray()),
        .. Keys().Select(static key => (RespireValue)key),
    ];

    [Test]
    public async Task FormattingMatchesToStringForEveryRepresentation()
    {
        foreach (var key in Keys()) await Verify(key, key.ToString());
        foreach (var value in Values()) await Verify(value, value.ToString());
    }

    private static async Task Verify<T>(T value, string expected) where T : ISpanFormattable, IUtf8SpanFormattable
    {
        var chars = new char[expected.Length];
        var bytes = new byte[Encoding.UTF8.GetByteCount(expected)];
        var provider = CultureInfo.GetCultureInfo("fr-FR");
        await Assert.That(value.TryFormat(chars, out var charCount, "G", provider)).IsTrue();
        await Assert.That(charCount).IsEqualTo(chars.Length);
        await Assert.That(new string(chars)).IsEqualTo(expected);
        await Assert.That(value.TryFormat(bytes, out var byteCount, "g", provider)).IsTrue();
        await Assert.That(byteCount).IsEqualTo(bytes.Length);
        await Assert.That(bytes.SequenceEqual(Encoding.UTF8.GetBytes(expected))).IsTrue();
        await Assert.That(value.ToString(null, provider)).IsEqualTo(expected);
        await Assert.That(string.Create(provider, $"{value}")).IsEqualTo(expected);

        if (chars.Length > 0)
        {
            await Assert.That(value.TryFormat(chars.AsSpan(0, chars.Length - 1), out charCount, default, null)).IsFalse();
            await Assert.That(charCount).IsEqualTo(0);
        }
        if (bytes.Length > 0)
        {
            await Assert.That(value.TryFormat(bytes.AsSpan(0, bytes.Length - 1), out byteCount, default, null)).IsFalse();
            await Assert.That(byteCount).IsEqualTo(0);
        }
    }

    [Test]
    public async Task BinaryPrefixBoundariesMatchRuntimeReplacementFallback()
    {
        byte[][] payloads =
        [
            "a£€😀z"u8.ToArray(), [0xf0, 0x9f], [0xed, 0xa0, 0x80],
            [0xc0, 0xaf], [0xe2, 0x82, 0x41], [0xf4, 0x90, 0x80, 0x80],
        ];
        foreach (var payload in payloads)
        {
            for (var split = 0; split <= payload.Length; split++)
            {
                var key = new RespireKey(payload.AsMemory(split)).Prepend(new KeyPrefix(payload[..split]));
                await Verify(key, Encoding.UTF8.GetString(payload));
            }
        }
    }

    [Test]
    public async Task UnsupportedFormatsThrowForAllFormattingEntryPoints()
    {
        RespireValue value = 42;
        RespireKey key = "key";
        await Assert.That(() => value.ToString("N2", null)).Throws<FormatException>();
        await Assert.That(() => key.ToString("N2", null)).Throws<FormatException>();
        await Assert.That(() => value.TryFormat(new char[64], out _, "N2")).Throws<FormatException>();
        await Assert.That(() => key.TryFormat(new byte[64], out _, "N2")).Throws<FormatException>();
    }

    [Test]
    public async Task GenericParsingPreservesTextAndOwnsSpanStorage()
    {
        await VerifyParsing<RespireKey>();
        await VerifyParsing<RespireValue>();
        await Assert.That(RespireValue.Parse("", null).IsNull).IsFalse();
        await Assert.That(RespireValue.TryParse((string?)null, null, out var absent)).IsFalse();
        await Assert.That(absent.IsNull).IsTrue();
        await Assert.That(RespireKey.TryParse((string?)null, null, out var empty)).IsFalse();
        await Assert.That(empty.IsEmpty).IsTrue();
    }

    private static async Task VerifyParsing<T>() where T : ISpanParsable<T>
    {
        const string text = " 001.50 £ 😀 ";
        var provider = CultureInfo.GetCultureInfo("fr-FR");
        await Assert.That(T.Parse(text, provider).ToString()).IsEqualTo(text);
        await Assert.That(T.TryParse(text, provider, out var parsed)).IsTrue();
        await Assert.That(parsed!.ToString()).IsEqualTo(text);
        var chars = text.ToCharArray();
        var owned = T.Parse(chars.AsSpan(), provider);
        await Assert.That(T.TryParse(chars.AsSpan(), provider, out var copied)).IsTrue();
        Array.Fill(chars, 'x');
        await Assert.That(owned.ToString()).IsEqualTo(text);
        await Assert.That(copied!.ToString()).IsEqualTo(text);
        await Assert.That(T.TryParse(ReadOnlySpan<char>.Empty, provider, out var empty)).IsTrue();
        await Assert.That(empty!.ToString()).IsEqualTo("");
        // ISpanParsable declares Parse again; its span overload hides IParsable.Parse
        // under a generic constraint. Exercise the string contract through IParsable.
        await Assert.That(() => ParseString<T>(null!, provider)).Throws<ArgumentNullException>();
    }

    private static T ParseString<T>(string text, IFormatProvider? provider) where T : IParsable<T>
        => T.Parse(text, provider);

    [Test]
    [NotInParallel]
    public async Task SpanAndUtf8InterpolationAllocateNothing()
    {
        var start = AsyncFlushSignalTests.CreateProbeStartInfo(Environment.ProcessPath,
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"), typeof(ArgumentFormattingTests).Assembly.Location);
        start.Environment[AllocationProbeMode] = Environment.Version.ToString();
        // The framework's tier-0 interpolation handlers box struct interface checks.
        // Isolate optimized steady state, without depending on background JIT promotion.
        start.Environment["DOTNET_TieredCompilation"] = "0";
        await AsyncFlushSignalTests.RunProbeAsync(start, TimeSpan.FromSeconds(30));
    }

    private const string AllocationProbeMode = "RESPIRE_ARGUMENT_FORMATTING_ALLOCATION_PROBE";

    internal static int? RunIsolatedAllocationProbe()
    {
        var runtime = Environment.GetEnvironmentVariable(AllocationProbeMode);
        if (runtime is null) return null;
        try
        {
            if (runtime != Environment.Version.ToString()) throw new InvalidOperationException("Probe runtime differs from parent.");
            VerifyAllocations();
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void VerifyAllocations()
    {
        var keys = Keys();
        var values = Values();
        _ = Measure(keys, values, false);
        _ = Measure(keys, values, true);
        var allocated = AllocationMeasurement.WithoutConcurrentGc(() => Measure(keys, values, false));
        var control = AllocationMeasurement.WithoutConcurrentGc(() => Measure(keys, values, true));
        Console.WriteLine($"Argument formatting: {allocated} bytes; positive control: {control} bytes.");
        if (allocated != 0 || control < 37L * 100)
            throw new InvalidOperationException($"Expected zero formatting allocations and a positive control; got {allocated} and {control} bytes.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure(RespireKey[] keys, RespireValue[] values, bool allocate)
    {
        Span<char> chars = stackalloc char[8192];
        Span<byte> bytes = stackalloc byte[16384];
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            foreach (var key in keys)
            {
                if (!chars.TryWrite($"key={key}", out _) || !Utf8.TryWrite(bytes, $"key={key}", out _))
                    throw new InvalidOperationException("Key did not fit.");
            }
            foreach (var value in values)
            {
                if (!chars.TryWrite($"value={value}", out _) || !Utf8.TryWrite(bytes, $"value={value}", out _))
                    throw new InvalidOperationException("Value did not fit.");
            }
            if (allocate) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
