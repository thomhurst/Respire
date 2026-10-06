using System.Text;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ValkeyClusterScanParserTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task JoinedSurrogateKeysKeepDistinctOwnedIdentity(bool binaryTail)
    {
        const string prefix = "tenant:\uD83D";
        var tail = binaryTail ? (byte[])[255, 0, 128] : ":x"u8.ToArray();
        byte[] first = [.. Encoding.UTF8.GetBytes(prefix + "\uDE00"), .. tail];
        byte[] second = [.. Encoding.UTF8.GetBytes(prefix + "\uDE01"), .. tail];
        using var reply = RespValue.Array(RespValue.BulkString("0"), RespValue.Array(
            RespValue.BulkString(first), RespValue.BulkString(second)));
        var page = ValkeyClusterScanParser.Parse(in reply, prefix);
        var repeated = ValkeyClusterScanParser.Parse(in reply, prefix);
        await Assert.That(page.Keys.Count).IsEqualTo(2);
        await Assert.That(page.Keys[0] == page.Keys[1]).IsFalse();
        await Assert.That(page.Keys[1] == page.Keys[0]).IsFalse();
        await Assert.That(new HashSet<RespireKey>(page.Keys).Count).IsEqualTo(2);
        RespireKey replacement = new((byte[])[.. Encoding.UTF8.GetBytes("\uDE00"), .. tail]);
        await Assert.That(page.Keys[0] == replacement).IsFalse();
        await Assert.That(replacement == page.Keys[0]).IsFalse();
        await Assert.That(new HashSet<RespireKey>(page.Keys.Concat(repeated.Keys).Append(replacement)).Count)
            .IsEqualTo(3);
        const string otherPrefix = "other:\uD83D";
        using var otherReply = RespValue.Array(RespValue.BulkString("0"), RespValue.Array(
            RespValue.BulkString((byte[])[.. Encoding.UTF8.GetBytes(otherPrefix + "\uDE00"), .. tail])));
        await Assert.That(page.Keys[0] == ValkeyClusterScanParser.Parse(in otherReply, otherPrefix).Keys[0])
            .IsFalse();
        for (var index = 0; index < page.Keys.Count; index++)
        {
            var original = page.Keys[index];
            var duplicate = repeated.Keys[index].Snapshot();
            await Assert.That(original.Equals(duplicate)).IsTrue();
            await Assert.That(original.Equals((object)duplicate)).IsTrue();
            await Assert.That(original.GetHashCode()).IsEqualTo(duplicate.GetHashCode());
            await Assert.That(original.Prepend(new Respire.Internal.KeyPrefix(prefix)).ToBytes()
                .SequenceEqual(index == 0 ? first : second)).IsTrue();
        }
    }

    [Test]
    [Arguments(null)]
    [Arguments("tenant:*:é:")]
    public async Task OwnsOpaqueCursorAndBinaryKeysAndStripsOnlyLiteralPrefix(string? prefix)
    {
        var cursor = Encoding.UTF8.GetBytes("future-{route}-opaque");
        var prefixBytes = Encoding.UTF8.GetBytes(prefix ?? "");
        byte[] key = [.. prefixBytes, 255, 0, 128];
        var keys = prefix is null ? RespValue.Array(RespValue.BulkString(key), RespValue.BulkString(""))
            : RespValue.Array(RespValue.BulkString(key), RespValue.BulkString(prefixBytes), RespValue.BulkString("outside"));
        var reply = RespValue.Array(RespValue.BulkString(cursor), keys);
        var page = ValkeyClusterScanParser.Parse(in reply, prefix);
        reply.Dispose();
        cursor.AsSpan().Clear(); key.AsSpan().Clear(); prefixBytes.AsSpan().Clear();
        await Assert.That(page.Cursor).IsEqualTo("future-{route}-opaque");
        await Assert.That(page.IsComplete).IsFalse();
        await Assert.That(page.Keys.Count).IsEqualTo(2);
        await Assert.That(page.Keys[0]).IsEqualTo(new RespireKey((byte[])[255, 0, 128]));
        await Assert.That(page.Keys[1].IsEmpty).IsTrue();
    }

    [Test]
    public async Task OnlyZeroCursorMarksCompletion()
    {
        using var complete = RespValue.Array(RespValue.BulkString("0"), RespValue.Array());
        using var pending = RespValue.Array(RespValue.BulkString("0-{route}-0"), RespValue.Array());
        await Assert.That(ValkeyClusterScanParser.Parse(in complete, null).IsComplete).IsTrue();
        await Assert.That(ValkeyClusterScanParser.Parse(in pending, null).IsComplete).IsFalse();
    }

    [Test]
    [Arguments("outer")]
    [Arguments("length")]
    [Arguments("cursor-null")]
    [Arguments("cursor-integer")]
    [Arguments("cursor-empty")]
    [Arguments("cursor-utf8")]
    [Arguments("keys-null")]
    [Arguments("key-integer")]
    [Arguments("key-null")]
    public async Task RejectsMalformedReplies(string shape)
    {
        using var reply = shape switch
        {
            "outer" => RespValue.Integer(0),
            "length" => RespValue.Array(RespValue.BulkString("0")),
            "cursor-null" => RespValue.Array(RespValue.Null, RespValue.Array()),
            "cursor-integer" => RespValue.Array(RespValue.Integer(0), RespValue.Array()),
            "cursor-empty" => RespValue.Array(RespValue.BulkString(""), RespValue.Array()),
            "cursor-utf8" => RespValue.Array(RespValue.BulkString((byte[])[255]), RespValue.Array()),
            "keys-null" => RespValue.Array(RespValue.BulkString("0"), RespValue.Null),
            "key-integer" => RespValue.Array(RespValue.BulkString("0"), RespValue.Array(RespValue.Integer(1))),
            _ => RespValue.Array(RespValue.BulkString("0"), RespValue.Array(RespValue.Null)),
        };
        await Assert.That(() => ValkeyClusterScanParser.Parse(in reply, null)).ThrowsExactly<RespireProtocolException>();
    }
}
