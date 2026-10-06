using System.Text;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Protocol;

public class ParserContextTests
{
    [Test]
    [Arguments(512, false)]
    [Arguments(513, false)]
    [Arguments(512, true)]
    [Arguments(513, true)]
    public async Task SiblingBranchesKeepIndependentDepth(int depth, bool resumable)
    {
        var branch = string.Concat(Enumerable.Repeat("*1\r\n", depth - 2)) + "*0\r\n";
        var bytes = Encoding.ASCII.GetBytes("*3\r\n" + branch + branch + "|0\r\n:42\r\n");
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
                await Assert.That(value.AsArray().Length).IsEqualTo(3);
                await Assert.That(value.AsArray()[2].AsInteger()).IsEqualTo(42);
            }
        }
    }

    [Test]
    public async Task RestartedNestedAttributesCommitOnlyCompleteReplies()
    {
        var bytes = "*2\r\n|1\r\n+k\r\n*2\r\n:1\r\n:2\r\n:42\r\n%1\r\n+k\r\n~1\r\n:7\r\n"u8.ToArray();
        for (var end = 0; end < bytes.Length; end++)
        {
            var pos = 0;
            var status = RespParser.TryParseValue(bytes.AsSpan(0, end), ref pos, out var value);
            using (value)
            {
                await Assert.That(status).IsEqualTo(RespParseStatus.NeedMoreData);
                await Assert.That(pos).IsEqualTo(0);
            }
        }
        var finalPos = 0;
        await Assert.That(RespParser.TryParseValue(bytes, ref finalPos, out var complete)).IsEqualTo(RespParseStatus.Done);
        using (complete)
        {
            await Assert.That(finalPos).IsEqualTo(bytes.Length);
            await Assert.That(complete.AsArray()[0].AsInteger()).IsEqualTo(42);
            await Assert.That(complete.AsArray()[1].AsArray()[1].AsArray()[0].AsInteger()).IsEqualTo(7);
        }
    }
}
