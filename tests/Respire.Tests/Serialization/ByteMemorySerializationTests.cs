using System.Buffers;
using Respire.Protocol;
using Respire.Serialization;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Serialization;

public class ByteMemorySerializationTests
{
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
