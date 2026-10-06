using System.Runtime.CompilerServices;
using System.Reflection;
using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Respire.Protocol;

namespace Respire.Tests.Protocol;

public class AggregateStorageTests
{
    [Test, NotInParallel]
    [Arguments('*', false)]
    [Arguments('~', false)]
    [Arguments('>', false)]
    [Arguments('%', false)]
    [Arguments('|', false)]
    [Arguments('*', true)]
    [Arguments('~', true)]
    [Arguments('>', true)]
    [Arguments('%', true)]
    [Arguments('|', true)]
    public async Task LargeDeclarationDoesNotAllocateDeclaredStorage(char prefix, bool resumable)
    {
        // Above the pool's largest bucket, so the original eager rent cannot be hidden by warmup.
        var header = Encoding.ASCII.GetBytes($"{prefix}66000\r\n");
        Measure(header, resumable, false);
        Measure(header, resumable, true);
        var measured = AllocationMeasurement.WithoutConcurrentGc(() =>
            (Bytes: Measure(header, resumable, false), Control: Measure(header, resumable, true)));
        await Assert.That(measured.Bytes).IsLessThan(64 * 1024L);
        await Assert.That(measured.Control).IsGreaterThanOrEqualTo(128 * 1024L);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure(byte[] header, bool resumable, bool control)
    {
        var start = GC.GetAllocatedBytesForCurrentThread();
        if (control)
        {
            GC.KeepAlive(new byte[128 * 1024]);
            return GC.GetAllocatedBytesForCurrentThread() - start;
        }
        using var parser = resumable ? new RespParseState(int.MaxValue) : null;
        var pos = 0;
        var status = parser is null
            ? RespParser.TryParseValue(header, ref pos, out _)
            : parser.TryParse(header, ref pos, out _, out _);
        if (status != RespParseStatus.NeedMoreData) throw new InvalidOperationException(status.ToString());
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }

    [Test]
    [Arguments("%9223372036854775807\r\n")]
    [Arguments("%-9223372036854775808\r\n")]
    [Arguments("|4611686018427387904\r\n")]
    [Arguments("%1073741824\r\n")]
    [Arguments("*2147483648\r\n")]
    [Arguments("~-2\r\n")]
    [Arguments(">-2\r\n")]
    public async Task InvalidOrOverflowingCountIsRejected(string header)
    {
        var bytes = Encoding.ASCII.GetBytes(header);
        var pos = 0;
        await Assert.That(RespParser.TryParseValue(bytes, ref pos, out _)).IsEqualTo(RespParseStatus.InvalidData);
        await Assert.That(pos).IsEqualTo(0);
        using var parser = new RespParseState(int.MaxValue);
        await Assert.That(parser.TryParseResumable(bytes, ref pos, out _, out _)).IsEqualTo(RespParseStatus.InvalidData);
    }

    [Test]
    [Arguments(128, false)]
    [Arguments(129, false)]
    [Arguments(257, false)]
    [Arguments(512, false)]
    [Arguments(513, false)]
    [Arguments(128, true)]
    [Arguments(129, true)]
    [Arguments(257, true)]
    [Arguments(512, true)]
    [Arguments(513, true)]
    public async Task DepthLimitIncludesEmptyAggregates(int depth, bool resumable)
    {
        // Mix arrays, maps, and sets; each map contributes a scalar key before its child.
        var headers = Enumerable.Range(0, depth - 1).Select(i => i % 3 == 0 ? "%1\r\n:0\r\n" : i % 3 == 1 ? "~1\r\n" : "*1\r\n");
        var bytes = Encoding.ASCII.GetBytes(string.Concat(headers) + "*0\r\n");
        using var parser = new RespParseState(int.MaxValue);
        var pos = 0;
        var status = resumable
            ? parser.TryParseResumable(bytes, ref pos, out var value, out _)
            : RespParser.TryParseValue(bytes, ref pos, out value);
        using (value)
        {
            await Assert.That(status).IsEqualTo(depth <= 512 ? RespParseStatus.Done : RespParseStatus.InvalidData);
            if (status == RespParseStatus.Done)
            {
                await Assert.That(pos).IsEqualTo(bytes.Length);
                using var owned = value.ToOwned();
                await Assert.That(owned.GetOwnedSize()).IsGreaterThan(0);
                var child = owned;
                for (var i = 0; i < depth - 1; i++)
                    child = child.AsArray()[^1];
                await Assert.That(child.Type).IsEqualTo(RespDataType.Array);
                await Assert.That(child.AsArray().Length).IsEqualTo(0);
            }
        }
    }

    [Test]
    public async Task NestedDeclarationsRetainNoElementArraysBeforeChildrenArrive()
    {
        using var parser = new RespParseState(int.MaxValue);
        var header = "*2147483647\r\n"u8.ToArray();
        for (var i = 0; i < 512; i++)
        {
            var pos = 0;
            await Assert.That(parser.TryParse(header, ref pos, out _, out _)).IsEqualTo(RespParseStatus.NeedMoreData);
            await Assert.That(pos).IsEqualTo(header.Length);
        }
        await Assert.That(FrameArrays(parser).Sum(array => array.Length)).IsEqualTo(0);
        var finalPos = 0;
        await Assert.That(parser.TryParse(header, ref finalPos, out _, out _)).IsEqualTo(RespParseStatus.InvalidData);
        parser.Dispose();
        await Assert.That(parser.IsIdle).IsTrue();
        finalPos = 0;
        await Assert.That(parser.TryParse(":42\r\n"u8, ref finalPos, out var value, out _)).IsEqualTo(RespParseStatus.Done);
        await Assert.That(value.AsInteger()).IsEqualTo(42);
    }

    [Test]
    [Arguments('*', false)]
    [Arguments('~', false)]
    [Arguments('>', false)]
    [Arguments('%', false)]
    [Arguments('|', false)]
    [Arguments('*', true)]
    [Arguments('~', true)]
    [Arguments('>', true)]
    [Arguments('%', true)]
    [Arguments('|', true)]
    public async Task LargeValidRepliesPreserveAllChildren(char prefix, bool fragmented)
    {
        const int count = 66000;
        var declared = prefix is '%' or '|' ? count / 2 : count;
        var bytes = Encoding.ASCII.GetBytes($"{prefix}{declared}\r\n" + string.Concat(Enumerable.Repeat(":7\r\n", count))
            + (prefix == '|' ? ":99\r\n" : ""));
        using var parser = new RespParseState(int.MaxValue);
        var pos = 0;
        if (fragmented)
        {
            for (var end = 1; end < bytes.Length; end += 257)
                if (parser.TryParse(bytes.AsSpan(0, end), ref pos, out _, out _) != RespParseStatus.NeedMoreData)
                    throw new InvalidOperationException("An incomplete aggregate completed early.");
        }
        var status = fragmented
            ? parser.TryParse(bytes, ref pos, out var value, out _)
            : RespParser.TryParseValue(bytes, ref pos, out value);
        using (value)
        {
            await Assert.That(status).IsEqualTo(RespParseStatus.Done);
            await Assert.That(pos).IsEqualTo(bytes.Length);
            if (prefix == '|')
                await Assert.That(value.AsInteger()).IsEqualTo(99);
            else
            {
                await Assert.That(value.AsArray().Length).IsEqualTo(count);
                // Read every slot after all growth/copy operations, not only the endpoints.
                await Assert.That(Sum(value)).IsEqualTo(7L * count);
            }
        }
    }

    private static long Sum(RespValue value)
    {
        long total = 0;
        foreach (var child in value.AsArray()) total += child.AsInteger();
        return total;
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NestedAttributesDoNotConsumeParentElementSlots(bool fragmented)
    {
        var bytes = "*2\r\n|1\r\n+k\r\n*2\r\n:1\r\n:2\r\n:42\r\n%1\r\n+k\r\n~1\r\n:7\r\n"u8.ToArray();
        using var parser = new RespParseState(int.MaxValue);
        var pos = 0;
        if (fragmented)
            for (var end = 1; end < bytes.Length; end++)
                if (parser.TryParse(bytes.AsSpan(0, end), ref pos, out _, out _) != RespParseStatus.NeedMoreData)
                    throw new InvalidOperationException("Nested attributes completed the parent early.");
        var status = fragmented
            ? parser.TryParse(bytes, ref pos, out var value, out _)
            : RespParser.TryParseValue(bytes, ref pos, out value);
        using (value)
        {
            await Assert.That(status).IsEqualTo(RespParseStatus.Done);
            await Assert.That(pos).IsEqualTo(bytes.Length);
            await Assert.That(value.AsArray().Length).IsEqualTo(2);
            await Assert.That(value.AsArray()[0].AsInteger()).IsEqualTo(42);
            await Assert.That(value.AsArray()[1].AsArray()[1].AsArray()[0].AsInteger()).IsEqualTo(7);
        }
    }

    [Test, NotInParallel]
    public async Task StatelessRentBudgetIsSharedAcrossNestedDeclarations()
    {
        // Enough wire bytes to justify one large rent, not one rent at each nesting level.
        var bytes = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("*66000\r\n", 3))
            + string.Concat(Enumerable.Repeat("_\r\n", 66000)));
        Measure(bytes, false, false);
        Measure(bytes, false, true);
        var measured = AllocationMeasurement.WithoutConcurrentGc(() =>
            (Bytes: Measure(bytes, false, false), Control: Measure(bytes, false, true)));
        await Assert.That(measured.Bytes).IsLessThan(4 * 1024 * 1024L);
        await Assert.That(measured.Control).IsGreaterThanOrEqualTo(128 * 1024L);
    }

    [Test, NotInParallel]
    public async Task GrowthAndDisposalClearReturnedArraysWithoutDisposingTransferredChildren()
    {
        using var parser = new RespParseState(int.MaxValue);
        var initial = Encoding.ASCII.GetBytes("*33\r\n" + string.Concat(Enumerable.Repeat("*1\r\n$7\r\npayload\r\n", 16)));
        var pos = 0;
        await Assert.That(parser.TryParse(initial, ref pos, out _, out _)).IsEqualTo(RespParseStatus.NeedMoreData);
        var old = FrameArrays(parser).Single(array => array.Length != 0);
        var nested = (RespValue[])typeof(RespValue).GetField("_elements", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(old[0])!;
        pos = 0;
        await Assert.That(parser.TryParse(":17\r\n"u8, ref pos, out _, out _)).IsEqualTo(RespParseStatus.NeedMoreData);
        var grown = FrameArrays(parser).Single(array => array.Length != 0);
        await Assert.That(grown.Length).IsEqualTo(32);
        await Assert.That(old.All(value => value.Type == default)).IsTrue();
        await Assert.That(grown[0].AsArray()[0].AsString()).IsEqualTo("payload");
        pos = 0;
        await Assert.That(parser.TryParse("?\r\n"u8, ref pos, out _, out _)).IsEqualTo(RespParseStatus.InvalidData);
        parser.Dispose();
        await Assert.That(grown.All(value => value.Type == default)).IsTrue();
        await Assert.That(nested.All(value => value.Type == default)).IsTrue();
    }

    private static IEnumerable<RespValue[]> FrameArrays(RespParseState parser)
    {
        var frames = (Array)typeof(RespParseState).GetField("_frames", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(parser)!;
        foreach (var frame in frames)
            if (frame!.GetType().GetField("Elements")!.GetValue(frame) is RespValue[] elements)
                yield return elements;
    }
}
