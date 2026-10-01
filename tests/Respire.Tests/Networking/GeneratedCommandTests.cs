using System.Text;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

[RespireCommands]
internal interface IGeneratedModule
{
    [RespireCommand("json.get")]
    ValueTask<string?> JsonGet(RespireKey key, string path = "$", CancellationToken token = default);
    [RespireCommand("CUSTOM.ARRAY")]
    Task<string?[]> Strings(byte[] key, params RespireValue[] values);
    [RespireCommand("CUSTOM.BYTES")]
    ValueTask<byte[]?[]> Bytes();
    [RespireCommand("CUSTOM.NUMBER")]
    ValueTask<double?> Number();
    [RespireCommand("CUSTOM.BOOL")]
    Task<bool> Boolean();
    [RespireCommand("CUSTOM.INT")]
    ValueTask<int> Integer();
    [RespireCommand("CUSTOM.RAW")]
    ValueTask<RespireResult> Raw(RespireCommandFlags flags = RespireCommandFlags.None);
    [RespireCommand("CUSTOM.RAW")]
    Task<RespireResult> RawTask();
    [RespireCommand("CUSTOM.RAW")]
    ValueTask<RespireResult> RawValues(RespireValue[] values, CancellationToken token = default);
    [RespireCommand("CUSTOM.WRITE")]
    ValueTask Write(RespireKey key, RespireValue value);
    [RespireCommand("CUSTOM.ERROR")]
    Task Error();
    [RespireCommand("AUTH")]
    ValueTask Authenticate(string password);
    [RespireCommand("MGET")]
    ValueTask<string?[]> MultiGet(params RespireKey[] keys);
    [RespireCommand("CUSTOM.RAW")]
    ValueTask<RespireResult> RawLetter(char letter);
}

#nullable disable
[RespireCommands]
internal interface IObliviousGeneratedModule
{
    [RespireCommand("CUSTOM.STRING")]
    Task<string> Text();
    [RespireCommand("CUSTOM.BYTES")]
    ValueTask<byte[]> Bytes();
    [RespireCommand("CUSTOM.ARRAY")]
    ValueTask<string[]> Strings();
}
#nullable restore

public class GeneratedCommandTests
{
    private static FakeRespServer Server(Func<string, byte[]> reply)
        => new(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("HELLO ", StringComparison.Ordinal)
                ? "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray()
                : command.StartsWith("CLIENT ", StringComparison.Ordinal) ? FakeRespServer.OkReply : reply(command),
        };

    private static ValueTask<RespireClient> Connect(FakeRespServer server, RespProtocol protocol, bool cache = false)
        => RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], Connections = 1, Protocol = protocol,
            ClientSideCache = cache ? new() : null, CommandTimeout = TimeSpan.FromSeconds(5),
        });

    [Test]
    [Arguments(RespProtocol.Resp2)]
    [Arguments(RespProtocol.Resp3)]
    public async Task GeneratedArgumentsPreserveOrderBinaryValuesAndDefaults(RespProtocol protocol)
    {
        await using var server = Server(command => command.StartsWith("JSON.GET", StringComparison.Ordinal)
            ? "$7\r\n{\"x\":1}\r\n"u8.ToArray() : "*3\r\n$1\r\na\r\n$-1\r\n$1\r\nb\r\n"u8.ToArray());
        await using var client = await Connect(server, protocol);
        var module = new IGeneratedModuleImplementation(client);
        await Assert.That(await module.JsonGet("key with spaces")).IsEqualTo("{\"x\":1}");
        byte[] binary = [0xff, 0, 0x80];
        var result = await module.Strings(binary, 42, "two words", binary);
        await Assert.That(result).IsEquivalentTo(new string?[] { "a", null, "b" });
        await Assert.That(server.ReceivedCommands).Contains("JSON.GET key with spaces $");
        var arguments = server.ReceivedArguments[^1];
        await Assert.That(arguments.Length).IsEqualTo(5);
        await Assert.That(arguments[1]).IsEquivalentTo(binary);
        await Assert.That(Encoding.UTF8.GetString(arguments[2])).IsEqualTo("42");
        await Assert.That(Encoding.UTF8.GetString(arguments[3])).IsEqualTo("two words");
        await Assert.That(arguments[4]).IsEquivalentTo(binary);
        await Assert.That(async () => await module.Strings(binary, null!)).Throws<ArgumentNullException>();
    }

    [Test]
    [Arguments(RespProtocol.Resp2)]
    [Arguments(RespProtocol.Resp3)]
    public async Task ScalarRepliesHandleBothProtocolsAndCheckedOverflow(RespProtocol protocol)
    {
        await using var server = Server(command => command switch
        {
            "CUSTOM.NUMBER" => protocol == RespProtocol.Resp3 ? ",1.25\r\n"u8.ToArray() : "$4\r\n1.25\r\n"u8.ToArray(),
            "CUSTOM.BOOL" => protocol == RespProtocol.Resp3 ? "#t\r\n"u8.ToArray() : ":1\r\n"u8.ToArray(),
            _ => ":2147483648\r\n"u8.ToArray(),
        });
        await using var client = await Connect(server, protocol);
        var module = new IGeneratedModuleImplementation(client);
        await Assert.That(await module.Number()).IsEqualTo(1.25);
        await Assert.That(await module.Boolean()).IsTrue();
        await Assert.That(async () => await module.Integer()).Throws<OverflowException>();
        // Conversion failures release their root reply without corrupting the next response.
        await Assert.That(await module.Number()).IsEqualTo(1.25);
    }

    [Test]
    [Arguments(RespProtocol.Resp2)]
    [Arguments(RespProtocol.Resp3)]
    public async Task NullableRepliesAndCopiedByteArraysSurviveLaterCommands(RespProtocol protocol)
    {
        await using var server = Server(command => command == "CUSTOM.BYTES"
            ? "*2\r\n$3\r\nabc\r\n$-1\r\n"u8.ToArray()
            : protocol == RespProtocol.Resp3 ? "_\r\n"u8.ToArray() : "$-1\r\n"u8.ToArray());
        await using var client = await Connect(server, protocol);
        var module = new IGeneratedModuleImplementation(client);
        var bytes = await module.Bytes();
        await Assert.That(await module.JsonGet("key")).IsNull();
        await Assert.That(await module.Number()).IsNull();
        await Assert.That(async () => await module.Integer()).Throws<InvalidOperationException>();
        await Assert.That(bytes[0]).IsEquivalentTo("abc"u8.ToArray());
        await Assert.That(bytes[1]).IsNull();
    }

    [Test]
    [Arguments(RespProtocol.Resp2)]
    [Arguments(RespProtocol.Resp3)]
    public async Task RawResultsTransferOwnershipAndExposeProtocolShapes(RespProtocol protocol)
    {
        await using var server = Server(_ => protocol == RespProtocol.Resp3
            ? "%1\r\n$1\r\nk\r\n:7\r\n"u8.ToArray() : "*2\r\n$1\r\nk\r\n:7\r\n"u8.ToArray());
        await using var client = await Connect(server, protocol);
        var module = new IGeneratedModuleImplementation(client);
        var result = await module.Raw();
        await Assert.That(result.IsDisposed).IsFalse();
        await Assert.That(result.Type).IsEqualTo(protocol == RespProtocol.Resp3 ? RespDataType.Map : RespDataType.Array);
        var nested = result[1];
        await Assert.That(nested.AsInteger()).IsEqualTo(7);
        result.Dispose();
        await Assert.That(nested.IsDisposed).IsTrue();
        using var taskResult = await module.RawTask();
        await Assert.That(taskResult[0].AsString()).IsEqualTo("k");
    }

    [Test]
    [Arguments(RespProtocol.Resp2)]
    [Arguments(RespProtocol.Resp3)]
    public async Task NullableObliviousDeclarationsAcceptNullReplies(RespProtocol protocol)
    {
        var nullReply = protocol == RespProtocol.Resp3 ? "_\r\n"u8.ToArray() : "$-1\r\n"u8.ToArray();
        await using var server = Server(command => command == "CUSTOM.ARRAY"
            ? "*2\r\n$1\r\na\r\n$-1\r\n"u8.ToArray() : nullReply);
        await using var client = await Connect(server, protocol);
        var module = new IObliviousGeneratedModuleImplementation(client);
        await Assert.That(await module.Text()).IsNull();
        await Assert.That(await module.Bytes()).IsNull();
        await Assert.That(await module.Strings()).IsEquivalentTo(new[] { "a", null });
    }

    [Test]
    public async Task SafetyChecksRejectAffinityPrefixAndUnsupportedFlagsBeforeWriting()
    {
        await using var server = Server(_ => FakeRespServer.OkReply);
        await using var client = await Connect(server, RespProtocol.Resp2);
        var module = new IGeneratedModuleImplementation(client);
        await Assert.That(async () => await module.Authenticate("secret")).Throws<NotSupportedException>();
        await Assert.That(async () => { using var result = await module.Raw((RespireCommandFlags)128); }).Throws<ArgumentOutOfRangeException>();
        var prefixed = new IGeneratedModuleImplementation(client.WithKeyPrefix("prefix:"));
        await prefixed.JsonGet("key");
        await Assert.That(server.ReceivedCommands).Contains("JSON.GET prefix:key $");
    }

    [Test]
    public async Task ErrorsAndCancellationPropagate()
    {
        await using var server = Server(_ => "-ERR module failure\r\n"u8.ToArray());
        await using var client = await Connect(server, RespProtocol.Resp2);
        var module = new IGeneratedModuleImplementation(client);
        await Assert.That(async () => await module.Error()).Throws<RespireServerException>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var count = server.CommandsSeen;
        await Assert.That(async () => await module.JsonGet("key", token: cancellation.Token)).Throws<OperationCanceledException>();
        // The non-async raw shape reports failures through its task, like every other return shape.
        var canceled = module.RawValues([], cancellation.Token);
        await Assert.That(canceled.IsCanceled).IsTrue();
        await Assert.That(async () => { using var result = await canceled; }).Throws<OperationCanceledException>();
        var missing = module.RawValues(null!);
        await Assert.That(async () => { using var result = await missing; }).Throws<ArgumentNullException>();
        // Argument conversion failures, such as an isolated surrogate, also fault the task instead of throwing.
        var unencodable = module.RawLetter('\ud800');
        await Assert.That(unencodable.IsFaulted).IsTrue();
        await Assert.That(async () => { using var result = await unencodable; }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(count);
    }

    [Test]
    public async Task UnknownModuleMutationsInvalidateExistingClientCacheEntries()
    {
        var reads = 0;
        await using var server = Server(command => command == "GET key"
            ? Encoding.UTF8.GetBytes($"$1\r\n{Interlocked.Increment(ref reads)}\r\n") : FakeRespServer.OkReply);
        await using var client = await Connect(server, RespProtocol.Resp3, cache: true);
        var module = new IGeneratedModuleImplementation(client);
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("1");
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("1");
        await module.Write("key", "value");
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("2");
    }

    [Test]
    [Arguments("")]
    [Arguments("AUTH user")]
    [Arguments("GET\r\n")]
    [Arguments("\u00e9")]
    public async Task PreencodedDescriptorsRejectInvalidCommandTokens(string name)
        => await Assert.That(() => RespireCommand.Create(name)).Throws<ArgumentException>();

    [Test]
    public async Task CustomDescriptorEncodesOnceWithoutClaimingOfficialProvenance()
    {
        var command = RespireCommand.Create("my.module");
        await Assert.That(command.Name).IsEqualTo("MY.MODULE");
        await Assert.That(command.Sources).IsEqualTo(RespireCommandSource.None);
        await Assert.That(command.IsCallerSupplied).IsFalse();
        await Assert.That(Encoding.ASCII.GetString(command.Verb.Bulk)).IsEqualTo("$9\r\nMY.MODULE\r\n");
        var copied = command;
        await Assert.That(ReferenceEquals(command.Verb.Bulk, copied.Verb.Bulk)).IsTrue();
        RespireCommand raw = "my.module";
        await Assert.That(raw.IsCallerSupplied).IsTrue();
    }
}
