using System.Text;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Protocol;

public class AggregateProgressTransferTests
{
    [Test]
    [Arguments("*3\r\n:1\r\n:2\r\n:3\r\n", 3)]
    [Arguments("*2\r\n+saved\r\n$5\r\nhello\r\n", 1)]
    [Arguments("%1\r\n+k\r\n~2\r\n:1\r\n>1\r\n$5\r\nhello\r\n", 2)]
    [Arguments("|1\r\n+k\r\n:9\r\n*2\r\n+saved\r\n$5\r\nhello\r\n", 3)]
    public async Task BufferedReplyKeepsFastPathAndOwnsPayloads(string frame, int expectedScalarReads)
    {
        var input = Encoding.ASCII.GetBytes(":99\r\n" + frame);
        using var parser = new RespParseState(int.MaxValue);
        using var control = new RespParseState(int.MaxValue);
        var position = 5;
        var controlPosition = 5;
        await Assert.That(parser.TryParse(input, ref position, out var actual, out _))
            .IsEqualTo(RespParseStatus.Done);
        await Assert.That(control.TryParseResumable(input, ref controlPosition, out var expected, out _))
            .IsEqualTo(RespParseStatus.Done);
        using (actual)
        using (expected)
        {
            await Assert.That(position).IsEqualTo(input.Length);
            await Assert.That(controlPosition).IsEqualTo(position);
            await Assert.That(parser.IsIdle).IsTrue();
#if DEBUG
            // A forced-resumable positive control proves the counter observes scalar work.
            await Assert.That(parser.ResumedScalarCountForTests).IsEqualTo(0);
            await Assert.That(control.ResumedScalarCountForTests).IsEqualTo(expectedScalarReads);
#else
            _ = expectedScalarReads;
#endif
            input.AsSpan().Fill(0);
            position = 0;
            await Assert.That(parser.TryParse("*1\r\n+fresh\r\n"u8, ref position, out var following, out _))
                .IsEqualTo(RespParseStatus.Done);
            using (following)
            {
                await Assert.That(following.AsArray()[0].AsString()).IsEqualTo("fresh");
                await Assert.That(actual.Equals(expected)).IsTrue();
            }
        }
    }

    [Test]
    [Arguments("*3\r\n:1\r\n:2\r\n$5\r\nhe", 2, false)]
    [Arguments("*3\r\n:1\r\n:2\r\n$5\r\nhe", 2, true)]
    [Arguments("~3\r\n:1\r\n:2\r\n$5\r\nhe", 2, false)]
    [Arguments("~3\r\n:1\r\n:2\r\n$5\r\nhe", 2, true)]
    [Arguments(">3\r\n:1\r\n:2\r\n$5\r\nhe", 2, false)]
    [Arguments(">3\r\n:1\r\n:2\r\n$5\r\nhe", 2, true)]
    [Arguments("%2\r\n:1\r\n:2\r\n:3\r\n$5\r\nhe", 3, false)]
    [Arguments("%2\r\n:1\r\n:2\r\n:3\r\n$5\r\nhe", 3, true)]
    [Arguments("*2\r\n*2\r\n:1\r\n:2\r\n$5\r\nhe", 2, false)]
    [Arguments("*2\r\n*2\r\n:1\r\n:2\r\n$5\r\nhe", 2, true)]
    [Arguments("*1\r\n*3\r\n:1\r\n:2\r\n$5\r\nhe", 2, false)]
    [Arguments("*1\r\n*3\r\n:1\r\n:2\r\n$5\r\nhe", 2, true)]
    [Arguments("*2\r\n:1\r\n*2\r\n:2\r\n$5\r\nhe", 2, false)]
    [Arguments("*2\r\n:1\r\n*2\r\n:2\r\n$5\r\nhe", 2, true)]
    [Arguments("|1\r\n+k\r\n:9\r\n*3\r\n:1\r\n:2\r\n$5\r\nhe", 4, false)]
    [Arguments("|1\r\n+k\r\n:9\r\n*3\r\n:1\r\n:2\r\n$5\r\nhe", 4, true)]
    [Arguments("*2\r\n|1\r\n+k\r\n:9\r\n:1\r\n$5\r\nhe", 3, false)]
    [Arguments("*2\r\n|1\r\n+k\r\n:9\r\n:1\r\n$5\r\nhe", 3, true)]
    [Arguments("*2\r\n+saved\r\n$5\r\nhe", 1, false)]
    [Arguments("*2\r\n+saved\r\n$5\r\nhe", 1, true)]
    [Arguments("*2\r\n*2\r\n+saved\r\n$3\r\nold\r\n$5\r\nhe", 1, false)]
    [Arguments("*2\r\n*2\r\n+saved\r\n$3\r\nold\r\n$5\r\nhe", 1, true)]
    public async Task CompletedPrefixSurvivesCompactionWithoutResumedScalarReads(
        string prefix, int expectedScalarReads, bool forceResumable)
    {
        // Exercise absolute deferred offsets and a nonzero receive-buffer start.
        var input = Encoding.ASCII.GetBytes(":99\r\n" + prefix);
        var complete = Encoding.ASCII.GetBytes(prefix + "llo\r\n");
        var expectedPosition = 0;
        await Assert.That(RespParser.TryParseValue(complete, ref expectedPosition, out var expected))
            .IsEqualTo(RespParseStatus.Done);
        using (expected)
        using (var parser = new RespParseState(int.MaxValue))
        {
            var position = 5;
            var status = forceResumable
                ? parser.TryParseResumable(input, ref position, out _, out _)
                : parser.TryParse(input, ref position, out _, out _);
            await Assert.That(status).IsEqualTo(RespParseStatus.NeedMoreData);
#if DEBUG
            // The forced path is a positive control: the counter must observe every scalar read.
            await Assert.That(parser.ResumedScalarCountForTests).IsEqualTo(forceResumable ? expectedScalarReads : 0);
#endif
            var remaining = input.AsSpan(position).ToArray();
            input.AsSpan().Fill(0);
            byte[] next = [.. remaining, .. "llo\r\n"u8];
            position = 0;
            await Assert.That(parser.TryParse(next, ref position, out var actual, out _)).IsEqualTo(RespParseStatus.Done);
            using (actual)
            using (var owned = actual.ToOwned())
            {
                await Assert.That(position).IsEqualTo(next.Length);
                await Assert.That(parser.IsIdle).IsTrue();
                await Assert.That(actual.Equals(expected)).IsTrue();
                await Assert.That(actual.Equals(owned)).IsTrue();
                await Assert.That(actual.GetHashCode()).IsEqualTo(owned.GetHashCode());
                await Assert.That(actual.GetOwnedSize()).IsEqualTo(owned.GetOwnedSize());
            }
        }
    }

    [Test]
    [Arguments("*3\r\n+saved\r\n*2\r\n:1\r\n$3\r\nold\r\n$5\r\nhello\r\n")]
    [Arguments("%2\r\n+k\r\n~2\r\n:1\r\n:2\r\n+v\r\n>1\r\n$5\r\nhello\r\n")]
    [Arguments("|1\r\n+k\r\n:9\r\n*2\r\n+saved\r\n$5\r\nhello\r\n")]
    [Arguments("*2\r\n+saved\r\n|1\r\n+k\r\n*2\r\n:8\r\n:9\r\n$5\r\nhello\r\n")]
    [Arguments("*2\r\n+saved\r\n|1\r\n+k\r\n:9\r\n*2\r\n:1\r\n$5\r\nhello\r\n")]
    public async Task EverySplitPreservesOwnedChildrenAndFollowingReply(string frame)
    {
        var complete = Encoding.ASCII.GetBytes(frame);
        var expectedPosition = 0;
        RespParser.TryParseValue(complete, ref expectedPosition, out var expected);
        using (expected)
        {
            for (var split = 1; split < complete.Length; split++)
            {
                using var parser = new RespParseState(int.MaxValue);
                var input = complete.AsSpan(0, split).ToArray();
                var position = 0;
                await Assert.That(parser.TryParse(input, ref position, out _, out _))
                    .IsEqualTo(RespParseStatus.NeedMoreData);
                byte[] next = [.. input.AsSpan(position), .. complete.AsSpan(split), .. ":99\r\n"u8];
                input.AsSpan().Fill(0);
                position = 0;
                await Assert.That(parser.TryParse(next, ref position, out var actual, out _))
                    .IsEqualTo(RespParseStatus.Done);
                using (actual)
                {
                    await Assert.That(actual.Equals(expected)).IsTrue();
                    await Assert.That(position).IsEqualTo(next.Length - 5);
                }
                await Assert.That(parser.TryParse(next, ref position, out var following, out _))
                    .IsEqualTo(RespParseStatus.Done);
                using (following) await Assert.That(following.AsInteger()).IsEqualTo(99);
                await Assert.That(position).IsEqualTo(next.Length);
                await Assert.That(parser.IsIdle).IsTrue();
            }
        }
    }

    [Test]
    [Arguments('$', RespDataType.BulkString)]
    [Arguments('=', RespDataType.VerbatimString)]
    [Arguments('!', RespDataType.BulkError)]
    public async Task TransferredNestedPrefixCompletesWithDirectFill(char marker, RespDataType type)
    {
        using var parser = new RespParseState(8);
        var input = Encoding.ASCII.GetBytes($"*2\r\n+saved\r\n*2\r\n:1\r\n{marker}10\r\n");
        var position = 0;
        await Assert.That(parser.TryParse(input, ref position, out _, out var request))
            .IsEqualTo(RespParseStatus.NeedDirectFill);
        await Assert.That(position).IsEqualTo(input.Length);
        await Assert.That(request).IsEqualTo(new RespDirectFillRequest(type, 10));
#if DEBUG
        await Assert.That(parser.ResumedScalarCountForTests).IsEqualTo(0);
#endif
        input.AsSpan().Fill(0);
        var filled = RespParser.CopyToPooled(type, "txt:abcdef"u8);
        // SupplyDirectFill transfers ownership; only the returned root disposes filled.
        await Assert.That(parser.SupplyDirectFill(in filled, out var value)).IsTrue();
        using (value)
        {
            var saved = value.AsArray()[0].AsString();
            var integer = value.AsArray()[1].AsArray()[0].AsInteger();
            var payload = Encoding.ASCII.GetString(value.AsArray()[1].AsArray()[1].AsSpan());
            await Assert.That(saved).IsEqualTo("saved");
            await Assert.That(integer).IsEqualTo(1);
            await Assert.That(payload).IsEqualTo(marker == '=' ? "abcdef" : "txt:abcdef");
        }
        await Assert.That(parser.IsIdle).IsTrue();
    }

    [Test]
    [Arguments("$5\r\nhello!\n")]
    [Arguments("?bad\r\n")]
    [Arguments("%1073741824\r\n")]
    public async Task InvalidContinuationReleasesTransferredChildrenAndAllowsReuse(string continuation)
    {
        using var parser = new RespParseState(int.MaxValue);
        var input = "*2\r\n*2\r\n+saved\r\n$3\r\nold\r\n"u8.ToArray();
        var position = 0;
        await Assert.That(parser.TryParse(input, ref position, out _, out _)).IsEqualTo(RespParseStatus.NeedMoreData);
        await Assert.That(position).IsEqualTo(input.Length);
        input.AsSpan().Fill(0);
        position = 0;
        await Assert.That(parser.TryParse(Encoding.ASCII.GetBytes(continuation), ref position, out _, out _))
            .IsEqualTo(RespParseStatus.InvalidData);
        parser.Dispose();
        parser.Dispose();
        await Assert.That(parser.IsIdle).IsTrue();
        position = 0;
        await Assert.That(parser.TryParse("*1\r\n+fresh\r\n"u8, ref position, out var fresh, out _))
            .IsEqualTo(RespParseStatus.Done);
        using (fresh)
        {
            var text = fresh.AsArray()[0].AsString();
            await Assert.That(text).IsEqualTo("fresh");
        }
    }

    [Test]
    [Arguments(512)]
    [Arguments(513)]
    public async Task TransferPreservesDepthLimit(int depth)
    {
        using var parser = new RespParseState(int.MaxValue);
        var input = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("*1\r\n", depth - 1))
            + "*2\r\n+saved\r\n$5\r\nhe");
        var position = 0;
        var status = parser.TryParse(input, ref position, out _, out _);
        await Assert.That(status).IsEqualTo(depth == 512 ? RespParseStatus.NeedMoreData : RespParseStatus.InvalidData);
        if (depth == 513)
        {
            await Assert.That(position).IsEqualTo(0);
            await Assert.That(parser.IsIdle).IsTrue();
            return;
        }
#if DEBUG
        await Assert.That(parser.ResumedScalarCountForTests).IsEqualTo(0);
#endif
        byte[] next = [.. input.AsSpan(position), .. "llo\r\n"u8];
        input.AsSpan().Fill(0);
        position = 0;
        await Assert.That(parser.TryParse(next, ref position, out var value, out _)).IsEqualTo(RespParseStatus.Done);
        using (value)
        {
            var child = value;
            for (var i = 1; i < depth; i++) child = child.AsArray()[0];
            var saved = child.AsArray()[0].AsString();
            var text = child.AsArray()[1].AsString();
            await Assert.That(saved).IsEqualTo("saved");
            await Assert.That(text).IsEqualTo("hello");
        }
        await Assert.That(parser.IsIdle).IsTrue();
    }

    [Test]
    public async Task BudgetRejectedHeaderStillUsesIncrementalStorage()
    {
        using var parser = new RespParseState(int.MaxValue);
        var position = 0;
        await Assert.That(parser.TryParse("*2147483647\r\n+saved\r\n"u8, ref position, out _, out _))
            .IsEqualTo(RespParseStatus.NeedMoreData);
        await Assert.That(position).IsEqualTo(21);
#if DEBUG
        // No stateless child was parsed: normal resumable parsing remains necessary.
        await Assert.That(parser.ResumedScalarCountForTests).IsEqualTo(1);
#endif
        parser.Dispose();
        await Assert.That(parser.IsIdle).IsTrue();
    }
}
