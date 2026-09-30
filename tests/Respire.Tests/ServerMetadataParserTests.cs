using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ServerMetadataParserTests
{
    [Test]
    public async Task InfoRetainsLegacyNullNestedAndFutureFieldsInOwnedStorage()
    {
        byte[] bytes = [255, 0, 128];
        var legacy = RespValue.Array(Text("get"), RespValue.Integer(2), RespValue.Array(Text("readonly")),
            RespValue.Integer(1), RespValue.Integer(1), RespValue.Integer(1));
        var modern = RespValue.Array(Text("future"), RespValue.Integer(-2), RespValue.Array(),
            RespValue.Integer(0), RespValue.Integer(-1), RespValue.Integer(0), RespValue.Array(Text("@read")),
            RespValue.Array(Text("nondeterministic_output")), RespValue.Array(RespValue.Array(Text("future"), RespValue.BulkString(bytes))),
            RespValue.Array(legacy), RespValue.BulkString(bytes));
        var reply = RespValue.Array(legacy, RespValue.Null, modern);
        var result = CommandMetadataParser.Info(in reply);
        reply.Dispose();
        bytes.AsSpan().Clear();
        await Assert.That(result[0]!.KeySpecifications).IsEmpty();
        await Assert.That(result[0]!.AclCategories).IsEmpty();
        await Assert.That(result[1]).IsNull();
        await Assert.That(result[2]!.Arity).IsEqualTo(-2);
        await Assert.That(result[2]!.LastKey).IsEqualTo(-1);
        await Assert.That(result[2]!.Subcommands.Single().Name).IsEqualTo("get");
        await Assert.That(result[2]!.KeySpecifications[0][1].AsBytes()).IsEquivalentTo((byte[])[255, 0, 128]);
        await Assert.That(result[2]!.AdditionalElements[0].AsBytes()).IsEquivalentTo((byte[])[255, 0, 128]);
    }

    [Test]
    public async Task DocumentationPreservesRecursiveArgumentsHistorySubcommandsAndUnknownFields()
    {
        byte[] bytes = [255, 0];
        var key = RespValue.Array(Text("name"), Text("key"), Text("type"), Text("key"),
            Text("key_spec_index"), RespValue.Integer(0), Text("future"), RespValue.BulkString(bytes));
        var block = RespValue.Array(Text("name"), Text("block"), Text("type"), Text("block"),
            Text("flags"), RespValue.Array(Text("optional")), Text("arguments"), RespValue.Array(key));
        var body = RespValue.Array(Text("summary"), Text("description"), Text("since"), Text("7.0"),
            Text("doc_flags"), RespValue.Array(Text("future-flag")), Text("history"), RespValue.Array(RespValue.Array(Text("7.1"), Text("change"))),
            Text("arguments"), RespValue.Array(block), Text("subcommands"), RespValue.Array(Text("command|sub"), RespValue.Array()),
            Text("future"), RespValue.BulkString(bytes));
        var reply = RespValue.Array(Text("command"), body);
        var result = CommandMetadataParser.Docs(in reply).Single();
        reply.Dispose();
        bytes.AsSpan().Clear();
        await Assert.That(result.Name).IsEqualTo("command");
        await Assert.That(result.History.Single()).IsEqualTo(new RespireCommandHistory("7.1", "change"));
        await Assert.That(result.Flags).IsEquivalentTo(["future-flag"]);
        await Assert.That(result.Arguments[0].Arguments[0].KeySpecificationIndex).IsEqualTo(0);
        await Assert.That(result.Arguments[0].Arguments[0].AdditionalFields["future"].AsBytes()).IsEquivalentTo((byte[])[255, 0]);
        await Assert.That(result.Subcommands.Single().Name).IsEqualTo("command|sub");
        await Assert.That(result.AdditionalFields["future"].AsBytes()).IsEquivalentTo((byte[])[255, 0]);
    }

    [Test]
    public async Task ModulePathsArgumentsAndUnknownFieldsRetainBinaryBytes()
    {
        byte[] bytes = [255, 0, 32];
        var reply = RespValue.Array(RespValue.Array(Text("name"), Text("module"), Text("ver"), RespValue.Integer(100),
            Text("path"), RespValue.BulkString(bytes), Text("args"), RespValue.Array(RespValue.BulkString(bytes)),
            Text("future"), RespValue.Array(RespValue.BulkString(bytes))),
            RespValue.Array(Text("name"), Text("legacy"), Text("ver"), RespValue.Integer(1)));
        var result = CommandMetadataParser.Modules(in reply);
        reply.Dispose();
        bytes.AsSpan().Clear();
        await Assert.That(result[0].Path!).IsEquivalentTo((byte[])[255, 0, 32]);
        await Assert.That(result[0].Arguments![0]).IsEquivalentTo((byte[])[255, 0, 32]);
        await Assert.That(result[0].AdditionalFields["future"][0].AsBytes()).IsEquivalentTo((byte[])[255, 0, 32]);
        await Assert.That(result[1].Path).IsNull();
        await Assert.That(result[1].Arguments).IsNull();
    }

    [Test]
    [Arguments("Background saving started", RespireBackgroundPersistenceState.Started)]
    [Arguments("Background saving scheduled", RespireBackgroundPersistenceState.Scheduled)]
    [Arguments("Background append only file rewriting started", RespireBackgroundPersistenceState.Started)]
    [Arguments("Background append only file rewriting scheduled", RespireBackgroundPersistenceState.Scheduled)]
    [Arguments("future response", RespireBackgroundPersistenceState.Unknown)]
    public async Task BackgroundRepliesPreserveAcceptanceMessage(string message, RespireBackgroundPersistenceState expected)
    {
        var result = CommandMetadataParser.Background(Text(message));
        await Assert.That(result.State).IsEqualTo(expected);
        await Assert.That(result.Message).IsEqualTo(message);
    }

    [Test]
    public async Task RepeatedDocumentationNamesRemainSeparateEntries()
    {
        var reply = RespValue.Array(Text("get"), RespValue.Array(), Text("get"), RespValue.Array());
        await Assert.That(CommandMetadataParser.Docs(in reply).Select(x => x.Name)).IsEquivalentTo(["get", "get"]);
    }

    [Test]
    public async Task InvalidShapesAreRejected()
    {
        await Assert.That(() => CommandMetadataParser.Info(RespValue.Array(RespValue.Array(Text("short"))))).ThrowsExactly<RespireProtocolException>();
        await Assert.That(() => CommandMetadataParser.Docs(RespValue.Array(Text("get")))).ThrowsExactly<RespireProtocolException>();
        await Assert.That(() => CommandMetadataParser.Docs(RespValue.Array(Text("get"), RespValue.Array(Text("since"), Text("1"), Text("since"), Text("2"))))).ThrowsExactly<RespireProtocolException>();
        await Assert.That(() => CommandMetadataParser.Modules(RespValue.Array(RespValue.Array(Text("name"), Text("missing-version"))))).ThrowsExactly<RespireProtocolException>();
        await Assert.That(() => CommandMetadataParser.Keys(RespValue.Array(RespValue.Integer(1)))).ThrowsExactly<RespireProtocolException>();
        await Assert.That(() => CommandMetadataParser.Ok(Text("OK"))).ThrowsExactly<RespireProtocolException>();
        await Assert.That(() => CommandMetadataParser.Background(RespValue.Integer(1))).ThrowsExactly<RespireProtocolException>();
    }

    private static RespValue Text(string value) => RespValue.BulkString(value);
}
