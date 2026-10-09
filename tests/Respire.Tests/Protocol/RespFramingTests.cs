using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Protocol;

public class RespFramingTests
{
    /// <summary>Verifies optimized headers and general fallback counts preserve exact bytes at buffer growth boundaries.</summary>
    [Test]
    [Arguments(int.MinValue)]
    [Arguments(-1)]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(5)]
    [Arguments(6)]
    [Arguments(7)]
    [Arguments(8)]
    [Arguments(9)]
    [Arguments(10)]
    [Arguments(99)]
    [Arguments(int.MaxValue)]
    public async Task ArrayHeadersPreserveAllCountRepresentations(int count)
    {
        var actual = SerializeWithGrowth((buffer) =>
        {
            var writer = new RespWriter(buffer);
            writer.WriteArrayHeader(count);
            writer.Complete();
        });
        await Assert.That(actual.SequenceEqual(Encoding.ASCII.GetBytes("*" + count.ToString(CultureInfo.InvariantCulture) + "\r\n")))
            .IsTrue();
    }

    /// <summary>Verifies signed extrema, every decimal transition and deterministic samples against invariant text framing.</summary>
    [Test]
    public async Task BulkIntegersPreserveSignedDecimalBoundaries()
    {
        var cases = new List<long> { long.MinValue, long.MinValue + 1, long.MaxValue - 1, long.MaxValue, 0 };
        long power = 1;
        for (var exponent = 0; exponent <= 18; exponent++)
        {
            foreach (var value in new[] { power - 1, power, power + 1 })
            {
                cases.Add(value);
                cases.Add(-value);
            }
            if (exponent < 18) power *= 10;
        }
        var random = new Random(1134);
        for (var index = 0; index < 1_000; index++) cases.Add(random.NextInt64(long.MinValue, long.MaxValue));
        foreach (var value in cases)
        {
            var actual = SerializeWithGrowth((buffer) =>
            {
                var writer = new RespWriter(buffer);
                writer.WriteBulkInteger(value);
                writer.Complete();
            });
            var text = value.ToString(CultureInfo.InvariantCulture);
            var expected = Encoding.ASCII.GetBytes($"${text.Length}\r\n{text}\r\n");
            await Assert.That(actual.SequenceEqual(expected)).IsTrue();
        }
    }

    [Test]
    [Arguments(0L)]
    [Arguments(9L)]
    [Arguments(10L)]
    [Arguments(-99_999_999L)]
    [Arguments(-100_000_000L)]
    [Arguments(999_999_999L)]
    [Arguments(1_000_000_000L)]
    [Arguments(long.MinValue)]
    [Arguments(long.MaxValue)]
    public async Task ReservedIntegerFitsExactRemainingCapacity(long value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        var expected = Encoding.ASCII.GetBytes($"${text.Length}\r\n{text}\r\n");
        var buffer = new WriteBuffer(64);
        try
        {
            var capacity = buffer.Capacity;
            var prefix = new byte[capacity - expected.Length];
            Array.Fill(prefix, (byte)0xA5);
            buffer.Append(prefix);
            WriteReservedInteger(buffer, value, expected.Length);
            await Assert.That(buffer.Capacity).IsEqualTo(capacity);
            await Assert.That(buffer.Count).IsEqualTo(capacity);
            await Assert.That(buffer.WrittenMemory.Span[..prefix.Length].SequenceEqual(prefix)).IsTrue();
            await Assert.That(buffer.WrittenMemory.Span[prefix.Length..].SequenceEqual(expected)).IsTrue();
        }
        finally { buffer.Release(); }
    }

    private static void WriteReservedInteger(WriteBuffer buffer, long value, int bound)
    {
        var published = buffer.Count;
        var writer = new RespWriter(buffer, bound);
        writer.WriteBulkInteger(value);
        if (buffer.Count != published)
            throw new InvalidOperationException("Integer serialization published an unfinished frame.");
        writer.Complete();
    }

    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WarmedIntegerPathsAllocateNothing(bool reserve)
    {
        var buffer = new WriteBuffer(256);
        try
        {
            for (var index = 0; index < 32; index++)
            {
                MeasureIntegerFrames(buffer, reserve, false);
                MeasureIntegerFrames(buffer, reserve, true);
            }
            var measured = AllocationMeasurement.WithoutConcurrentGc(() => (
                Actual: MeasureIntegerFrames(buffer, reserve, false),
                Control: MeasureIntegerFrames(buffer, reserve, true)));
            await Assert.That(measured.Actual).IsEqualTo(0);
            await Assert.That(measured.Control).IsGreaterThanOrEqualTo(37_000);
        }
        finally { buffer.Release(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureIntegerFrames(WriteBuffer buffer, bool reserve, bool allocate)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1_000; index++)
        {
            buffer.Reset();
            var writer = new RespWriter(buffer, reserve ? 4 * CommandWriteSizeHint.Bulk(20) : 0);
            writer.WriteBulkInteger(0);
            writer.WriteBulkInteger(1024);
            writer.WriteBulkInteger(long.MaxValue);
            writer.WriteBulkInteger(long.MinValue);
            writer.Complete();
            if (allocate) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>Verifies fixed options serialize once and retain ordinary text identity through cache/routing inspection.</summary>
    [Test]
    [Arguments(0, "PX")]
    [Arguments(1, "PXAT")]
    [Arguments(2, "REV")]
    [Arguments(3, "WITHSCORES")]
    public async Task PreEncodedOptionsRetainLogicalIdentity(int index, string text)
    {
        var option = index switch
        {
            0 => CommandOptionFrames.PXValue,
            1 => CommandOptionFrames.PXATValue,
            2 => CommandOptionFrames.REVValue,
            _ => CommandOptionFrames.WITHSCORESValue,
        };
        var actual = SerializeWithGrowth((buffer) =>
        {
            var writer = new RespWriter(buffer);
            option.WriteTo(ref writer);
            writer.Complete();
        });
        await Assert.That(actual.SequenceEqual(Encoding.ASCII.GetBytes($"${text.Length}\r\n{text}\r\n"))).IsTrue();
        RespireValue ordinary = text;
        RespireValue bytes = Encoding.ASCII.GetBytes(text);
        await Assert.That(option == ordinary && ordinary == option && option == bytes && bytes == option).IsTrue();
        await Assert.That(option.GetHashCode()).IsEqualTo(ordinary.GetHashCode());
        await Assert.That(option.GetHashCode()).IsEqualTo(bytes.GetHashCode());
        await Assert.That(option.ToString()).IsEqualTo(text);
        await Assert.That(option.AsKey()).IsEqualTo((RespireKey)text);
        await Assert.That(option.GetWireLength()).IsEqualTo(text.Length);
        await Assert.That(option.EqualsAsciiIgnoreCase(text.ToLowerInvariant())).IsTrue();
        await Assert.That(option.TryGetInt64(out _)).IsFalse();
        await Assert.That(option.IsEmpty || option.IsNull || option.TryGetByteMemory(out _)).IsFalse();
        option.TryGetClusterSlot(out var slot);
        ordinary.TryGetClusterSlot(out var expectedSlot);
        await Assert.That(slot).IsEqualTo(expectedSlot);
        await Assert.That(option.Snapshot()).IsEqualTo(ordinary);
        var payload = new byte[text.Length];
        await Assert.That(option.WriteWirePayload(payload)).IsEqualTo(text.Length);
        await Assert.That(payload.SequenceEqual(Encoding.ASCII.GetBytes(text))).IsTrue();
        var optimizedCacheKey = new ClientCacheCommandKey("fixed-option-control", "key", option);
        var ordinaryCacheKey = new ClientCacheCommandKey("fixed-option-control", "key", ordinary);
        await Assert.That(optimizedCacheKey).IsEqualTo(ordinaryCacheKey);
        await Assert.That(optimizedCacheKey.GetHashCode()).IsEqualTo(ordinaryCacheKey.GetHashCode());
        await Assert.That(optimizedCacheKey.Snapshot()).IsEqualTo(ordinaryCacheKey);
        await Assert.That(optimizedCacheKey.OwnedSize).IsEqualTo(ordinaryCacheKey.OwnedSize);
    }

    /// <summary>Verifies frame rollback preserves prior bytes and invalid SET admission emits no partial frame.</summary>
    [Test]
    public async Task RollbackAndInvalidSetPreservePriorFrames()
    {
        var buffer = new WriteBuffer(16);
        try
        {
            var actual = WriteRollbackControl(buffer);
            await Assert.That(actual.SequenceEqual("*1\r\n$4\r\nPING\r\n"u8.ToArray())).IsTrue();
        }
        finally { buffer.Release(); }
    }

    private static byte[] WriteRollbackControl(WriteBuffer buffer)
    {
        var writer = new RespWriter(buffer);
        writer.WriteRaw("*1\r\n$4\r\nPING\r\n"u8);
        writer.Complete();
        var mark = buffer.Count;
        writer.WriteBulkInteger(long.MinValue);
        writer.WriteRaw(CommandOptionFrames.WITHSCORES);
        writer.Complete();
        buffer.TruncateTo(mark);
        writer = new RespWriter(buffer);
        try
        {
            new SetCommand("key", "value", RespireExpiry.Persist, SetWhen.Always, false).Write(ref writer);
            throw new InvalidOperationException("Invalid expiry was accepted.");
        }
        catch (ArgumentException) { }
        if (buffer.Count != mark) throw new InvalidOperationException("Rejected SET published bytes.");
        return buffer.WrittenMemory.ToArray();
    }

    /// <summary>Measures warmed headers, integers and fixed options with a deliberate escaping allocation control.</summary>
    [Test]
    [NotInParallel]
    public async Task WarmedFramingAllocatesNothing()
    {
        var buffer = new WriteBuffer(256);
        try
        {
            for (var index = 0; index < 32; index++)
            {
                Measure(buffer, false);
                Measure(buffer, true);
            }
            var measured = AllocationMeasurement.WithoutConcurrentGc(() => (Measure(buffer, false), Measure(buffer, true)));
            await Assert.That(measured.Item1).IsEqualTo(0L);
            await Assert.That(measured.Item2).IsGreaterThanOrEqualTo(37_000L);
        }
        finally { buffer.Release(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure(WriteBuffer buffer, bool allocate)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1_000; index++)
        {
            buffer.Reset();
            var writer = new RespWriter(buffer);
            writer.WriteArrayHeader(4);
            writer.WriteBulkInteger(long.MinValue);
            writer.WriteRaw(CommandOptionFrames.PX);
            CommandOptionFrames.REVValue.WriteTo(ref writer);
            CommandOptionFrames.WITHSCORESValue.WriteTo(ref writer);
            writer.Complete();
            if (allocate) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static byte[] SerializeWithGrowth(Action<WriteBuffer> write)
    {
        var buffer = new WriteBuffer(16);
        try
        {
            var prefix = new byte[buffer.Capacity - 1];
            Array.Fill(prefix, (byte)0xA5);
            buffer.Append(prefix);
            write(buffer);
            if (!buffer.WrittenMemory.Span[..prefix.Length].SequenceEqual(prefix))
                throw new InvalidOperationException("Buffer growth changed previously written bytes.");
            return buffer.WrittenMemory.Span[prefix.Length..].ToArray();
        }
        finally { buffer.Release(); }
    }
}
