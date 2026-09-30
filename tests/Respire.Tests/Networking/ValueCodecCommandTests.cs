using System.Buffers;
using System.Text;
using Respire.Compression;
using Respire.Serialization;
using Respire.Tests.Serialization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ValueCodecCommandTests
{
    [Test]
    [Arguments("brotli")]
    [Arguments("deflate")]
    [Arguments("lz4")]
    [Arguments("zstd")]
    public async Task TypedValuesUseCodecWhilePrimitiveAndRawPathsStayUnchanged(string algorithm)
    {
        var codec = ValueCodecTests.Create(algorithm);
        var serializer = new RespireValueCodecSerializer(RespireSerializer.Default, codec);
        var value = new Payload(new string('x', 4096));
        var buffer = new ArrayBufferWriter<byte>();
        serializer.Serialize(buffer, value);
        var frame = buffer.WrittenSpan.ToArray();
        byte[] raw = [0, 255, 128];
        await using var server = new FakeRespServer(FakeRespServer.OkReply, Bulk(frame),
            FakeRespServer.OkReply, Bulk("42"u8.ToArray()), FakeRespServer.OkReply,
            FakeRespServer.OkReply, FakeRespServer.OkReply, Bulk(frame), "$-1\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", server.Port)], Connections = 1, Serializer = serializer,
        });
        var prefixed = client.WithKeyPrefix("tenant:");
        await prefixed.SetAsync("typed", value);
        await Assert.That(await prefixed.GetAsync<Payload>("typed")).IsEqualTo(value);
        await prefixed.SetAsync("count", 42);
        await Assert.That(await prefixed.GetAsync<int>("count")).IsEqualTo(42);
        await prefixed.SetAsync<string>("text", "plain");
        await prefixed.SetAsync<byte[]>("bytes", raw);
        using (var sent = await client.ExecuteAsync(RespireCommands.String.SET, "raw", raw)) { }
        using var result = await client.ExecuteAsync(RespireCommands.String.GET, "typed");
        var decoded = result.As<Payload>();
        await Assert.That(result.AsBytes()).IsEquivalentTo(frame);
        await Assert.That(await client.GetAsync<Payload>("missing")).IsNull();
        await client.DisposeAsync();
        await Assert.That(decoded).IsEqualTo(value);
        await Assert.That(server.ReceivedArguments[0][1]).IsEquivalentTo("tenant:typed"u8.ToArray());
        await Assert.That(server.ReceivedArguments[0][2]).IsEquivalentTo(frame);
        await Assert.That(server.ReceivedCommands[2]).IsEqualTo("SET tenant:count 42");
        await Assert.That(server.ReceivedCommands[4]).IsEqualTo("SET tenant:text plain");
        await Assert.That(server.ReceivedArguments[5][2]).IsEquivalentTo(raw);
        await Assert.That(server.ReceivedArguments[6][2]).IsEquivalentTo(raw);
    }

    [Test]
    [Arguments("brotli", false)]
    [Arguments("brotli", true)]
    [Arguments("deflate", false)]
    [Arguments("deflate", true)]
    [Arguments("lz4", false)]
    [Arguments("lz4", true)]
    [Arguments("zstd", false)]
    [Arguments("zstd", true)]
    public async Task DeferredValuesOwnTheirEncodedSnapshot(string algorithm, bool transaction)
    {
        var codec = ValueCodecTests.Create(algorithm);
        var serializer = new RespireValueCodecSerializer(RespireSerializer.Default, codec);
        var value = new MutablePayload { Text = new string('x', 4096) };
        var expectedText = value.Text;
        var bytes = new ArrayBufferWriter<byte>();
        serializer.Serialize(bytes, value);
        var frame = bytes.WrittenSpan.ToArray();
        byte[][] replies = [FakeRespServer.OkReply, Bulk(frame)];
        if (transaction) replies = [FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), "+QUEUED\r\n"u8.ToArray(),
            [.. "*2\r\n"u8, .. replies.SelectMany(reply => reply)]];
        await using var server = new FakeRespServer(replies);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", server.Port)], Connections = 1, Serializer = serializer,
        });
        if (transaction)
        {
            await using var queue = client.CreateTransaction();
            _ = queue.Strings.Set("value", value);
            var read = queue.Strings.Get<MutablePayload>("value");
            value.Text = "mutated after enqueue";
            await queue.CommitAsync();
            await Assert.That(read.Result!.Text).IsEqualTo(expectedText);
        }
        else
        {
            using var queue = client.CreateBatch();
            _ = queue.Strings.Set("value", value);
            var read = queue.Strings.Get<MutablePayload>("value");
            value.Text = "mutated after enqueue";
            (await queue.ExecuteAsync()).ThrowIfAnyFailed();
            await Assert.That(read.Result!.Text).IsEqualTo(expectedText);
        }
        var set = server.ReceivedArguments.Single(args => args[0].AsSpan().SequenceEqual("SET"u8));
        await Assert.That(set[2]).IsEquivalentTo(frame);
    }

    [Test]
    public async Task DefaultSerializerWireFormatRemainsUnframed()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await client.SetAsync("plain", new Payload("unchanged"));
        await Assert.That(server.ReceivedArguments.Single()[2]).IsEquivalentTo("{\"Text\":\"unchanged\"}"u8.ToArray());
    }

    private static byte[] Bulk(byte[] value) => [.. Encoding.ASCII.GetBytes($"${value.Length}\r\n"), .. value, 13, 10];
    public sealed record Payload(string Text);
    public sealed class MutablePayload { public string Text { get; set; } = ""; }
}
