using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ServerAclParserTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UserOwnsBinaryPatternsSelectorsAndUnknownNestedFields(bool legacy)
    {
        byte[] pattern = [126, 255, 0, 32];
        var patternValue = RespValue.BulkString(pattern);
        var selector = RespValue.Array(Text("commands"), Text("+get"), Text("keys"), patternValue,
            Text("channels"), Text("&*"), Text("future-selector"), RespValue.Array(patternValue));
        var reply = RespValue.Array(Text("flags"), RespValue.Array(Text("on")), Text("passwords"), RespValue.Array(Text("hash")),
            Text("commands"), Text("-@all +get"), Text("keys"), legacy ? RespValue.Array(patternValue) : patternValue,
            Text("channels"), legacy ? RespValue.Array(Text("*")) : Text("&*"),
            Text("selectors"), RespValue.Array(selector), Text("future"), RespValue.Array(patternValue, RespValue.Integer(42)));
        var user = AclParser.User(in reply)!;
        reply.Dispose();
        pattern.AsSpan().Clear();
        await Assert.That(user.Flags).IsEquivalentTo(["on"]);
        await Assert.That(user.PasswordHashes).IsEquivalentTo(["hash"]);
        await Assert.That(user.Commands).IsEqualTo("-@all +get");
        var actual = legacy ? user.Keys.LegacyPatterns![0] : user.Keys.RuleExpression!;
        await Assert.That(actual).IsEquivalentTo((byte[])[126, 255, 0, 32]);
        await Assert.That(user.Selectors[0].Keys.RuleExpression!).IsEquivalentTo((byte[])[126, 255, 0, 32]);
        await Assert.That(user.AdditionalFields["future"][0].AsBytes()).IsEquivalentTo((byte[])[126, 255, 0, 32]);
        await Assert.That(user.Selectors[0].AdditionalFields["future-selector"][0].AsBytes()).IsEquivalentTo((byte[])[126, 255, 0, 32]);
    }

    [Test]
    public async Task RedisSixUserCanOmitChannelsAndSelectors()
    {
        var reply = RespValue.Array(Text("flags"), RespValue.Array(), Text("passwords"), RespValue.Array(),
            Text("commands"), Text("-@all"), Text("keys"), RespValue.Array());
        var user = AclParser.User(in reply)!;
        await Assert.That(user.Channels).IsNull();
        await Assert.That(user.Selectors).IsEmpty();
        await Assert.That(user.Keys.LegacyPatterns!).IsEmpty();
        await Assert.That(user.Keys.RuleExpression).IsNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LogOwnsFieldsAndHandlesOptionalMetadata(bool modern)
    {
        byte[] resource = [255, 0];
        var fields = new List<RespValue>
        {
            Text("count"), RespValue.Integer(2), Text("reason"), Text("future-reason"), Text("context"), Text("module"),
            Text("object"), RespValue.BulkString(resource), Text("username"), Text("user"),
            Text("age-seconds"), modern ? RespValue.Double(1.25) : Text("1.25"), Text("client-info"), Text("id=1"),
            Text("future"), RespValue.BulkString(resource),
        };
        if (modern) fields.AddRange([Text("entry-id"), RespValue.Integer(7), Text("timestamp-created"), RespValue.Integer(1000), Text("timestamp-last-updated"), RespValue.Integer(2000)]);
        var reply = RespValue.Array(RespValue.Array(fields.ToArray()));
        var entry = AclParser.Log(in reply).Single();
        resource.AsSpan().Clear();
        reply.Dispose();
        await Assert.That(entry.Count).IsEqualTo(2);
        await Assert.That(entry.AgeSeconds).IsEqualTo(1.25);
        await Assert.That(entry.Reason).IsEqualTo("future-reason");
        await Assert.That(entry.Object).IsEquivalentTo((byte[])[255, 0]);
        await Assert.That(entry.AdditionalFields["future"].AsBytes()).IsEquivalentTo((byte[])[255, 0]);
        await Assert.That(entry.EntryId).IsEqualTo(modern ? (long?)7 : null);
        await Assert.That(entry.TimestampCreated).IsEqualTo(modern ? (long?)1000 : null);
        await Assert.That(entry.TimestampLastUpdated).IsEqualTo(modern ? (long?)2000 : null);
    }

    [Test]
    public async Task MalformedStructuresAndScalarRepliesAreRejected()
    {
        await Assert.That(() => AclParser.User(RespValue.Array(Text("flags")))).ThrowsExactly<RespireProtocolException>();
        await Assert.That(() => AclParser.User(RespValue.Array(Text("flags"), RespValue.Array(), Text("flags"), RespValue.Array()))).ThrowsExactly<RespireProtocolException>();
        await Assert.That(() => AclParser.User(RespValue.Array())).ThrowsExactly<RespireProtocolException>();
        await Assert.That(() => AclParser.User(Text("user"))).ThrowsExactly<RespireProtocolException>();
        await Assert.That(() => AclParser.ByteStrings(RespValue.Array(RespValue.Integer(1)))).ThrowsExactly<RespireProtocolException>();
        await Assert.That(() => AclParser.NonnegativeInteger(RespValue.Integer(-1))).ThrowsExactly<RespireProtocolException>();
        await Assert.That(() => AclParser.DryRun(RespValue.SimpleString("denied"))).ThrowsExactly<RespireProtocolException>();
        await Assert.That(() => AclParser.Ok(Text("OK"))).ThrowsExactly<RespireProtocolException>();
    }

    private static RespValue Text(string value) => RespValue.BulkString(value);
}
