using System.Buffers;
using Respire.Serialization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class PrimitiveSerializationWireTests
{
    [Test]
    public async Task GenericPrimitives_RoundTripWithoutObjectSerializerEncoding()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply,
            "$2\r\n42\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            "$1\r\n1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await client.SetAsync("count", 42);
        var count = await client.GetAsync<int>("count");
        await client.SetAsync("enabled", true);
        var enabled = await client.GetAsync<bool>("enabled");

        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("SET count 42");
        await Assert.That(server.ReceivedCommands[1]).IsEqualTo("GET count");
        await Assert.That(server.ReceivedCommands[2]).IsEqualTo("SET enabled 1");
        await Assert.That(server.ReceivedCommands[3]).IsEqualTo("GET enabled");
        await Assert.That(count).IsEqualTo(42);
        await Assert.That(enabled).IsTrue();
    }

    [Test]
    public async Task BooleanWrites_UseSameEncodingAcrossOverloadShapes()
    {
        await using var server = new FakeRespServer(
            ":1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        object boxed = true;

        await client.Hashes.SetAsync("flags", "generic", true);
        await client.Hashes.SetAsync("flags", ("tuple", true));
        await client.Sets.ContainsAsync("flags", boxed);

        await Assert.That(server.ReceivedCommands).IsEquivalentTo([
            "HSET flags generic 1",
            "HSET flags tuple 1",
            "SISMEMBER flags 1",
        ]);
    }

    [Test]
    public async Task CharacterWrites_UseBareTextAcrossOverloadShapes()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply, FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        RespireValue value = '£';

        await client.SetAsync("generic", '£');
        await client.SetAsync("value", value);

        await Assert.That(server.ReceivedCommands).IsEquivalentTo([
            "SET generic £",
            "SET value £",
        ]);
    }

    [Test]
    public async Task NullablePrimitives_PreserveJsonNullEncoding()
    {
        await using var server = new FakeRespServer("$4\r\nnull\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var result = await client.GetAsync<int?>("count");

        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("GET count");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task SerializerCancellation_AfterResponseTimeoutWindow_IsNotCommandTimeout()
    {
        await using var server = new FakeRespServer("$2\r\n{}\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Connections = 1,
            CommandTimeout = TimeSpan.FromMilliseconds(250),
            Serializer = new CancelingDeserializer()
        });

        await Assert.That(async () => await client.GetAsync<Payload>("key"))
            .ThrowsExactly<OperationCanceledException>();
    }

    [Test]
    public async Task NullRawStringValues_AreRejectedBeforeSending()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var setException = await Assert.That(
                async () => await client.Strings.SetAsync("key", RespireValue.Null))
            .ThrowsExactly<ArgumentNullException>();
        var getAndSetException = await Assert.That(
                async () => await client.Strings.GetAndSetAsync("key", RespireValue.Null))
            .ThrowsExactly<ArgumentNullException>();
        var appendException = await Assert.That(
                async () => await client.Strings.AppendAsync("key", RespireValue.Null))
            .ThrowsExactly<ArgumentNullException>();

        await Assert.That(setException!.ParamName).IsEqualTo("value");
        await Assert.That(getAndSetException!.ParamName).IsEqualTo("value");
        await Assert.That(appendException!.ParamName).IsEqualTo("value");
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task NullGenericValues_AreRejectedBeforeSending()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        string? nullString = null;
        byte[]? nullBytes = null;

        await Assert.That(async () => await client.SetAsync("string", nullString))
            .Throws<ArgumentNullException>();
        await Assert.That(async () => await client.SetAsync("bytes", nullBytes))
            .Throws<ArgumentNullException>();

        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    private sealed record Payload;

    private sealed class CancelingDeserializer : IRespireSerializer
    {
        public void Serialize<T>(IBufferWriter<byte> destination, T value)
            => throw new NotSupportedException();

        public T? Deserialize<T>(ReadOnlySpan<byte> payload)
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(500));
            throw new OperationCanceledException("Serializer canceled conversion.");
        }
    }
}
