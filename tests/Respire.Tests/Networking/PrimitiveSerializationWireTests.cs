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
        await Assert.That(server.ReceivedCommands[2]).IsEqualTo("SET enabled true");
        await Assert.That(server.ReceivedCommands[3]).IsEqualTo("GET enabled");
        await Assert.That(count).IsEqualTo(42);
        await Assert.That(enabled).IsTrue();
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
    public async Task SerializerCancellation_IsNotReportedAsCommandTimeout()
    {
        await using var server = new FakeRespServer("$2\r\n{}\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Connections = 1,
            CommandTimeout = TimeSpan.FromSeconds(10),
            Serializer = new CancelingDeserializer()
        });

        await Assert.That(async () => await client.GetAsync<Payload>("key"))
            .ThrowsExactly<OperationCanceledException>();
    }

    [Test]
    public async Task NullRawValue_IsRejectedBeforeSending()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var exception = await Assert.That(
                async () => await client.SetAsync("key", RespireValue.Null))
            .Throws<ArgumentException>();

        await Assert.That(exception!.Message).Contains("null value cannot be sent as a Redis argument");
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
            => throw new OperationCanceledException("Serializer canceled conversion.");
    }
}
