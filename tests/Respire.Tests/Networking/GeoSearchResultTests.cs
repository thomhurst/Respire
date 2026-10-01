using System.Runtime.CompilerServices;
using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class GeoSearchResultTests
{
    [Test]
    [NotInParallel] // The shared no-GC measurement boundary is process-wide.
    public async Task RawMember_AllocatesOnlyDecodedTextAndOwnedBytes()
    {
        byte[] member = Encoding.UTF8.GetBytes(new string('x', 4096));
        _ = MeasureExpected(member, out _);
        _ = MeasureResult(member, out _);

        // Concurrent GC can perturb the thread allocation counter. See docs/ALLOCATION_MEASUREMENT.md.
        var (expected, actual, text, result) = AllocationMeasurement.WithoutConcurrentGc(() =>
        {
            var expectedBytes = MeasureExpected(member, out var decoded);
            var actualBytes = MeasureResult(member, out var created);
            return (expectedBytes, actualBytes, decoded, created);
        });

        // Allow small runtime bookkeeping differences, but never the extra 4 KB member array.
        await Assert.That(actual).IsLessThanOrEqualTo(expected + 128);
        await Assert.That(result.Member).IsEqualTo(text);
        await Assert.That(result.MemberBytes.Span.SequenceEqual(member)).IsTrue();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureExpected(byte[] member, out string text)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        text = Encoding.UTF8.GetString(member);
        var bytes = member.AsSpan().ToArray();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(bytes);
        return allocated;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureResult(byte[] member, out GeoSearchResult result)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        result = new GeoSearchResult(member.AsSpan());
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Test]
    public async Task RawMember_OwnsBytesAndPreservesDetailsAndDeconstruction()
    {
        byte[] member = [0xff, 0x00];
        var position = new GeoPosition(2.5, 3.5);
        var result = new GeoSearchResult(member.AsSpan(), 1.5, 123, position);
        member[0] = 0;
        var (text, distance, hash, coordinates) = result;

        await Assert.That(text).IsEqualTo("\ufffd\0");
        await Assert.That(distance).IsEqualTo(1.5);
        await Assert.That(hash).IsEqualTo(123);
        await Assert.That(coordinates).IsEqualTo(position);
        await Assert.That(result.MemberBytes.Span.SequenceEqual(new byte[] { 0xff, 0x00 })).IsTrue();
        await Assert.That(result).IsNotEqualTo(new GeoSearchResult(text, distance, hash, coordinates));
    }

    [Test]
    public async Task TextAndRawMembers_HaveMatchingValueSemantics()
    {
        var text = new GeoSearchResult(Member: "café", Distance: 1.5, Hash: 123, Position: new(2.5, 3.5));
        var raw = new GeoSearchResult("café"u8, 1.5, 123, new(2.5, 3.5));
        text.Deconstruct(Member: out var member, Distance: out var distance, Hash: out var hash, Position: out var position);

        await Assert.That(raw).IsEqualTo(text);
        await Assert.That(new GeoSearchResult(member, distance, hash, position)).IsEqualTo(text);
        await Assert.That(raw.GetHashCode()).IsEqualTo(text.GetHashCode());
        await Assert.That((raw with { Member = "new" }).MemberBytes.Span.SequenceEqual("new"u8)).IsTrue();
        await Assert.That((raw with { Distance = 2 }).MemberBytes.Span.SequenceEqual("café"u8)).IsTrue();
        await Assert.That(default(GeoSearchResult).MemberBytes.IsEmpty).IsTrue();
    }
}
