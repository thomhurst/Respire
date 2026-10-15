using System.Buffers;
using Respire.Protocol;
using Respire.Serialization;
using Respire.Testing;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Serialization;

public class ByteMemorySerializationTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TypedScriptAndFunctionReadsPreserveBinaryWrites(bool readOnly)
    {
        await CheckScriptAndFunctionAsync<ReadOnlyMemory<byte>>(readOnly);
        await CheckScriptAndFunctionAsync<Memory<byte>>(readOnly);
        await CheckScriptAndFunctionAsync<ArraySegment<byte>>(readOnly);
    }

    private static async Task CheckScriptAndFunctionAsync<T>(bool readOnly)
    {
        byte[] expected = [0, 255, 128, 13, 10];
        byte[] reply = [.. "$5\r\n"u8, .. expected, .. "\r\n"u8];
        await using var server = new FakeRespServer(FakeRespServer.OkReply, reply, reply);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespireProtocol.Resp2,
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Connections = 1,
            Serializer = new RejectingSerializer(),
        });
        object value = typeof(T) == typeof(ReadOnlyMemory<byte>) ? (object)new ReadOnlyMemory<byte>(expected)
            : typeof(T) == typeof(Memory<byte>) ? (object)new Memory<byte>(expected)
            : new ArraySegment<byte>(expected);
        await client.SetAsync("binary", (T)value);
        var scriptValue = await client.Scripts.ExecuteAsync<T>(
            RespireScript.Create("return redis.call('GET', KEYS[1])", readOnly), ["binary"]);
        var functionValue = await client.Functions.ExecuteAsync<T>(RespireFunction.Create("read_binary", readOnly), ["binary"]);
        await client.DisposeAsync();

        await AssertBytesAsync(server.ReceivedArguments[0][2], expected);
        await AssertBytesAsync(ToMemory(scriptValue).ToArray(), expected);
        await AssertBytesAsync(ToMemory(functionValue).ToArray(), expected);
    }

    [Test]
    [Arguments(0)]
    [Arguments(5)]
    public async Task ResultAndMessageReadsCopyBinaryPayloadWithoutSerializer(int length)
    {
        byte[] buffer = [99, 0, 255, 128, 13, 10, 98];
        var payload = buffer.AsMemory(1, length);
        var expected = payload.ToArray();
        var serializer = new RejectingSerializer();
        var response = RespValue.BulkString(payload);
        using var result = new RespireResult(in response, serializer);
        var message = new RespireMessage("binary", null, payload, serializer);

        var resultReadOnly = result.As<ReadOnlyMemory<byte>>();
        var resultWritable = result.As<Memory<byte>>();
        var resultSegment = result.As<ArraySegment<byte>>();
        var messageReadOnly = message.As<ReadOnlyMemory<byte>>();
        var messageWritable = message.As<Memory<byte>>();
        var messageSegment = message.As<ArraySegment<byte>>();
        var resultBytes = result.As<byte[]>();
        var messageBytes = message.As<byte[]>();

        payload.Span.Fill(42);
        result.Dispose();
        await AssertBytesAsync(resultReadOnly.ToArray(), expected);
        await AssertBytesAsync(resultWritable.ToArray(), expected);
        await AssertBytesAsync(resultSegment.ToArray(), expected);
        await AssertBytesAsync(messageReadOnly.ToArray(), expected);
        await AssertBytesAsync(messageWritable.ToArray(), expected);
        await AssertBytesAsync(messageSegment.ToArray(), expected);
        await AssertBytesAsync(resultBytes, expected);
        await AssertBytesAsync(messageBytes, expected);
        resultWritable.Span.Fill(99);
        messageWritable.Span.Fill(99);
        await AssertBytesAsync(resultReadOnly.ToArray(), expected);
        await AssertBytesAsync(resultSegment.ToArray(), expected);
        await AssertBytesAsync(messageReadOnly.ToArray(), expected);
        await AssertBytesAsync(messageSegment.ToArray(), expected);
    }

    [Test]
    public async Task TypedReadsCopyBorrowedResponseStorage()
    {
        await using var client = RespireClient.Create("localhost");
        byte[] payload = [0, 255, 128, 13, 10];
        var expected = payload.ToArray();
        using var response = RespValue.BulkString(payload);
        var readOnly = client.DeserializeBorrowed<ReadOnlyMemory<byte>>(in response);
        var writable = client.DeserializeBorrowed<Memory<byte>>(in response);
        var segment = client.DeserializeBorrowed<ArraySegment<byte>>(in response);
        payload.AsSpan().Fill(42);
        await AssertBytesAsync(readOnly.ToArray(), expected);
        await AssertBytesAsync(writable.ToArray(), expected);
        await AssertBytesAsync(segment.ToArray(), expected);
        writable.Span[0] = 99;
        await AssertBytesAsync(readOnly.ToArray(), expected);
        await AssertBytesAsync(segment.ToArray(), expected);
    }

    [Test]
    [Arguments(-1, false)]
    [Arguments(0, false)]
    [Arguments(5, false)]
    [Arguments(-1, true)]
    [Arguments(0, true)]
    [Arguments(5, true)]
    public async Task GenericWritersPreserveBinarySlices(int length, bool customSerializer)
    {
        await using var server = new RespireFakeServer();
        var options = server.CreateOptions();
        if (customSerializer) options = options with { Serializer = new RejectingSerializer() };
        await using var client = await RespireClient.ConnectAsync(options);
        byte[] buffer = [99, 0, 255, 128, 13, 10, 98];
        var memory = length < 0 ? default : buffer.AsMemory(1, length);
        var segment = length < 0 ? default : new ArraySegment<byte>(buffer, 1, length);
        var expected = memory.ToArray();

        await CheckWritersAsync(client, (ReadOnlyMemory<byte>)memory, expected);
        await CheckWritersAsync(client, memory, expected);
        await CheckWritersAsync(client, segment, expected);
    }

    private static async Task CheckWritersAsync<T>(IRespireClient client, T value, byte[] expected)
    {
        var ttl = TimeSpan.FromHours(1);
        await client.SetAsync("root", value, ttl);
        await client.Strings.SetAsync("facet", value, ttl);
        await client.Hashes.SetAsync("hash", "field", value);
        await AssertBytesAsync(await client.GetBytesAsync("root"), expected);
        await AssertBytesAsync(await client.GetBytesAsync("facet"), expected);
        await AssertBytesAsync(await client.Hashes.GetAsync<byte[]>("hash", "field"), expected);
        await AssertBytesAsync(ToMemory(await client.GetAsync<T>("root")).ToArray(), expected);
        var previous = await client.Strings.GetAndSetAsync("root", value, ttl);
        await AssertBytesAsync(ToMemory(previous).ToArray(), expected);

        using var batch = client.CreateBatch();
        var batchSet = batch.Set("batch", value, ttl);
        var facetSet = batch.Strings.Set("batch-facet", value, ttl);
        var batchRead = batch.Get<T>("batch");
        await batch.ExecuteAsync();
        await Assert.That(batchSet.Result).IsTrue();
        await Assert.That(facetSet.Result).IsTrue();
        await AssertBytesAsync(await client.GetBytesAsync("batch"), expected);
        await AssertBytesAsync(await client.GetBytesAsync("batch-facet"), expected);
        await AssertBytesAsync(ToMemory(batchRead.Result).ToArray(), expected);

        await using var transaction = client.CreateTransaction();
        var transactionSet = transaction.Set("transaction", value, ttl);
        var transactionRead = transaction.Get<T>("transaction");
        await transaction.CommitAsync();
        await Assert.That(transactionSet.Result).IsTrue();
        await AssertBytesAsync(await client.GetBytesAsync("transaction"), expected);
        await AssertBytesAsync(ToMemory(transactionRead.Result).ToArray(), expected);
    }

    internal static ReadOnlyMemory<byte> ToMemory<T>(T value) => value switch
    {
        ReadOnlyMemory<byte> memory => memory,
        Memory<byte> memory => memory,
        ArraySegment<byte> segment => segment.AsMemory(),
        _ => throw new InvalidOperationException("Expected a byte-memory value."),
    };

    private static async Task AssertBytesAsync(byte[]? actual, byte[] expected)
    {
        await Assert.That(actual).IsNotNull();
        await Assert.That(actual!.AsSpan().SequenceEqual(expected)).IsTrue();
    }

    private sealed class RejectingSerializer : IRespireSerializer
    {
        public void Serialize<T>(IBufferWriter<byte> destination, T value) => throw new InvalidOperationException("Serializer must be bypassed.");
        public void Serialize(IBufferWriter<byte> destination, Type type, object? value) => throw new InvalidOperationException("Serializer must be bypassed.");
        public T? Deserialize<T>(ReadOnlySpan<byte> payload) => throw new InvalidOperationException("Serializer must be bypassed.");
        public object? Deserialize(Type type, ReadOnlySpan<byte> payload) => throw new InvalidOperationException("Serializer must be bypassed.");
    }
}
