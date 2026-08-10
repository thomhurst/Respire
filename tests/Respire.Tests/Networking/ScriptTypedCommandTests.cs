using System.Buffers;
using System.Text;
using Respire.Serialization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ScriptTypedCommandTests
{
    [Test]
    public async Task TypedConveniences_ConvertAndDisposeScriptReplies()
    {
        await using var server = new FakeRespServer(
            "-NOSCRIPT missing\r\n"u8.ToArray(), "$7\r\npayload\r\n"u8.ToArray(),
            "-NOSCRIPT missing\r\n"u8.ToArray(), ":42\r\n"u8.ToArray(),
            "-NOSCRIPT missing\r\n"u8.ToArray(), "$5\r\nhello\r\n"u8.ToArray());
        var serializer = new RecordingSerializer();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Connections = 1,
            Serializer = serializer,
        });
        var script = RespireScript.Create("return ARGV[1]");

        var typed = await client.Scripts.ExecuteAsync<Payload>(script);
        var integer = await client.Scripts.ExecuteIntegerAsync(script);
        var text = await client.Scripts.ExecuteStringAsync(script);

        await Assert.That(typed).IsEqualTo(new Payload("payload"));
        await Assert.That(integer).IsEqualTo(42);
        await Assert.That(text).IsEqualTo("hello");
        await Assert.That(serializer.DeserializeCalls).IsEqualTo(1);
    }

    [Test]
    public async Task SpanOverload_PreservesKeysAndArguments()
    {
        await using var server = new FakeRespServer(
            "-NOSCRIPT missing\r\n"u8.ToArray(), ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var script = RespireScript.Create("return #KEYS + #ARGV");
        RespireKey[] keys = ["key"];
        RespireValue[] args = ["argument"];

        using var result = await client.Scripts.ExecuteSpanAsync(
            script, keys.AsSpan(), args.AsSpan());

        await Assert.That(result.AsInteger()).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands[1]).IsEqualTo(
            "EVAL return #KEYS + #ARGV 1 key argument");
    }

    [Test]
    public async Task DeferredScriptResult_UsesConfiguredSerializer()
    {
        await using var server = new FakeRespServer("$7\r\npayload\r\n"u8.ToArray());
        var serializer = new RecordingSerializer();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Serializer = serializer,
        });
        var script = RespireScript.Create("return ARGV[1]");

        using var batch = client.CreateBatch();
        var pending = batch.Scripts.Evaluate(script, args: ["payload"]);
        await batch.ExecuteAsync();
        using var result = pending.Result;

        await Assert.That(result.As<Payload>()).IsEqualTo(new Payload("payload"));
        await Assert.That(serializer.DeserializeCalls).IsEqualTo(1);
    }

    private sealed record Payload(string Value);

    private sealed class RecordingSerializer : IRespireSerializer
    {
        public int DeserializeCalls { get; private set; }

        public void Serialize<T>(IBufferWriter<byte> destination, T value)
            => throw new NotSupportedException();

        public T? Deserialize<T>(ReadOnlySpan<byte> payload)
        {
            DeserializeCalls++;
            return (T)(object)new Payload(Encoding.UTF8.GetString(payload));
        }
    }
}
