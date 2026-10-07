using System.Runtime.CompilerServices;
using System.Text;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Protocol;

public class Utf8SuffixWriterTests
{
    public static IEnumerable<(string Value, int Prefix, int Padding)> TextCases()
    {
        string[] values = ["", "ASCII", "éfirst", "midéend", "lasté", "😀", "a😀z",
            "\uD800", "\uDC00", "a\uD800z", "a\uDC00z", "\uDC00tail", "a\uD800\uD800\uDC00",
            new string('a', 8) + "é", new string('a', 98) + "é", new string('a', 998) + "é",
            new string('a', 63) + new string('é', 64), new string('a', 4095) + "😀"];
        foreach (var value in values)
        foreach (var prefix in Enumerable.Range(0, 5))
        foreach (var padding in new[] { 0, 31, 123, 509 })
            yield return (value, prefix, padding);
    }

    [Test]
    [MethodDataSource(nameof(TextCases))]
    public async Task CorrectedFramesPreserveUtf8AndAdjacentBytes(string value, int prefixKind, int padding)
    {
        var prefix = CreatePrefix(prefixKind);
        var expectedPayload = prefix is null ? Encoding.UTF8.GetBytes(value)
            : prefix.Text is { } text ? Encoding.UTF8.GetBytes(text + value)
            : prefix.Bytes.Concat(Encoding.UTF8.GetBytes(value)).ToArray();
        var leading = Enumerable.Repeat((byte)0xA5, padding).ToArray();
        var buffer = new WriteBuffer(32);
        byte[] actual;
        try
        {
            var writer = new RespWriter(buffer);
            writer.WriteRaw(leading);
            if (prefix is null) writer.WriteBulkString(value);
            else writer.WritePrefixedKey(prefix, value, default);
            writer.WriteRaw("+NEXT\r\n"u8);
            writer.Complete();
            actual = buffer.WrittenMemory.ToArray();
        }
        finally { buffer.Release(); }
        byte[] expected = [.. leading, .. Encoding.ASCII.GetBytes($"${expectedPayload.Length}\r\n"),
            .. expectedPayload, 13, 10, .. "+NEXT\r\n"u8];
        await Assert.That(actual.SequenceEqual(expected)).IsTrue();
        await Assert.That(buffer.Count).IsEqualTo(0);
        await Assert.That(buffer.Capacity).IsEqualTo(0);
    }

    [Test]
    [Arguments(0)]
    [Arguments(3)]
    public async Task GrowthAfterAsciiPassRetainsCommittedPrefix(int prefixKind)
    {
        var prefix = CreatePrefix(prefixKind);
        var value = new string('a', 98) + "é";
        var payload = prefix is null ? Encoding.UTF8.GetBytes(value)
            : prefix.Bytes.Concat(Encoding.UTF8.GetBytes(value)).ToArray();
        var speculativeLength = (prefix?.Bytes.Length ?? 0) + value.Length;
        var speculativeFrameLength = Encoding.ASCII.GetByteCount($"${speculativeLength}\r\n") + speculativeLength + 2;
        var buffer = new WriteBuffer(256);
        var capacity = buffer.Capacity;
        var leading = Enumerable.Repeat((byte)0xA5, capacity - speculativeFrameLength).ToArray();
        byte[] actual;
        try
        {
            var writer = new RespWriter(buffer);
            writer.WriteRaw(leading);
            if (prefix is null) writer.WriteBulkString(value);
            else writer.WritePrefixedKey(prefix, value, default);
            writer.Complete();
            actual = buffer.WrittenMemory.ToArray();
            await Assert.That(buffer.Capacity).IsGreaterThan(capacity);
        }
        finally { buffer.Release(); }
        byte[] expected = [.. leading, .. Encoding.ASCII.GetBytes($"${payload.Length}\r\n"), .. payload, 13, 10];
        await Assert.That(actual.SequenceEqual(expected)).IsTrue();
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    public async Task BinarySuffixPreservesPrefixBoundary(int prefixKind)
    {
        var prefix = CreatePrefix(prefixKind)!;
        byte[] suffix = [0, 0xFF, 0xED, 0xA0, 0x80, 13, 10];
        var payload = prefix.Bytes.Concat(suffix).ToArray();
        var buffer = new WriteBuffer(1);
        byte[] actual;
        try
        {
            var writer = new RespWriter(buffer);
            writer.WritePrefixedKey(prefix, null, suffix);
            writer.Complete();
            actual = buffer.WrittenMemory.ToArray();
        }
        finally { buffer.Release(); }
        byte[] expected = [.. Encoding.ASCII.GetBytes($"${payload.Length}\r\n"), .. payload, 13, 10];
        await Assert.That(actual.SequenceEqual(expected)).IsTrue();
    }

    [Test, NotInParallel]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(3)]
    public async Task WarmMixedWritesAllocateNothing(int prefixKind)
    {
        var prefix = CreatePrefix(prefixKind);
        var value = new string('a', 998) + "é😀";
        var buffer = new WriteBuffer(4096);
        try
        {
            _ = Measure(buffer, value, prefix, false);
            _ = Measure(buffer, value, prefix, true);
            var measured = AllocationMeasurement.WithoutConcurrentGc(() => (
                Actual: Measure(buffer, value, prefix, false), Control: Measure(buffer, value, prefix, true)));
            await Assert.That(measured.Actual).IsEqualTo(0);
            await Assert.That(measured.Control).IsGreaterThanOrEqualTo(37_000);
        }
        finally { buffer.Release(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure(WriteBuffer buffer, string value, KeyPrefix? prefix, bool control)
    {
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++)
        {
            buffer.Reset();
            var writer = new RespWriter(buffer);
            if (prefix is null) writer.WriteBulkString(value);
            else writer.WritePrefixedKey(prefix, value, default);
            writer.Complete();
            if (control) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FailedCommandRollsBackCorrectedBulkFrame(bool prefixed)
    {
        await using var server = new FakeRespServer("$2\r\nok\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var failure = new InvalidOperationException("intentional writer failure");
        await Assert.That(async () =>
        {
            using var reply = await client.SendAsync("GET", new FailingCommand(failure, prefixed), deadline.Token);
        }).Throws<InvalidOperationException>();
        await Assert.That(await client.GetAsync<string>("after", cancellationToken: deadline.Token)).IsEqualTo("ok");
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["GET after"]);
    }

    private readonly struct FailingCommand(Exception error, bool prefixed) : IRespCommand
    {
        public ReadCommandKind ReadKind => ReadCommandKind.None;
        public void Write(ref RespWriter writer)
        {
            writer.WriteArrayHeader(2);
            writer.WriteBulkString("GET"u8);
            var value = new string('a', 4095) + new string('é', 4096);
            if (prefixed) writer.WritePrefixedKey(new KeyPrefix("tenant:"), value, default);
            else writer.WriteBulkString(value);
            throw error;
        }
    }

    private static KeyPrefix? CreatePrefix(int kind) => kind switch
    {
        0 => null,
        1 => new("tenant:"),
        2 => new("租户:"),
        3 => new(new byte[] { 0xFF, 0, (byte)':' }),
        4 => new("tenant:\uD800"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
