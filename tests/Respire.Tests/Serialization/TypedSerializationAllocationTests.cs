using System.Buffers;
using System.Runtime.CompilerServices;
using Respire.Serialization;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Serialization;

[NotInParallel]
public class TypedSerializationAllocationTests
{
    private const int Count = 128;
    private static object? _escape;

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GenericJsonInt32AvoidsBoxingAndWriterAllocations(bool generated)
    {
        var serializer = generated ? SystemTextJsonSerializer.FromContext(TestJsonContext.Default) : new SystemTextJsonSerializer();
        var destination = new ArrayBufferWriter<byte>(256);
        _ = MeasureJson(serializer, destination, false);
        _ = MeasureJson(serializer, destination, true);
        var measured = AllocationMeasurement.WithoutConcurrentGc(() =>
            (Actual: MeasureJson(serializer, destination, false), Control: MeasureJson(serializer, destination, true)));
        await Assert.That(measured.Actual).IsEqualTo(0L);
        await Assert.That(measured.Control).IsGreaterThanOrEqualTo(37L * Count);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TypedSetAddsOnlyOwnedPocoPayloadAboveRawSet(bool poco)
    {
        await using var server = new FakeRespServer("+OK\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], Protocol = RespProtocol.Resp2, Connections = 1,
        });
        var payload = new Payload(42);
        var pending = new ValueTask<bool>[Count + 1];
        // A completion can resume this test before releasing its receive reference. Warm
        // one additional source so that reference cannot force a rental during measurement.
        var warmPending = new ValueTask<bool>[Count + 2];
        var serialized = client.Serialize(payload);
        var payloadLength = serialized.ToString().Length;
        RespireValue raw = poco ? serialized : 42;
        for (var warmup = 0; warmup < 4; warmup++)
        {
            _ = MeasureSet(client, payload, poco, raw, false, warmPending, allocate: warmup == 3);
            await Drain(warmPending);
            _ = MeasureSet(client, payload, poco, raw, true, warmPending, false);
            await Drain(warmPending);
        }
        var actual = AllocationMeasurement.WithoutConcurrentGc(() => MeasureSet(client, payload, poco, raw, false, pending, false));
        await Drain(pending);
        var baseline = AllocationMeasurement.WithoutConcurrentGc(() => MeasureSet(client, payload, poco, raw, true, pending, false));
        await Drain(pending);
        var control = AllocationMeasurement.WithoutConcurrentGc(() => MeasureSet(client, payload, poco, raw, false, pending, true));
        await Drain(pending);
        _ = MeasurePayloadArrays(payloadLength);
        var ownedBytes = AllocationMeasurement.WithoutConcurrentGc(() => MeasurePayloadArrays(payloadLength));
        // Compare the same wire payload and transport path. Existing command overhead belongs
        // to the raw baseline; typed serialization adds at most its required owned byte array.
        await Assert.That(actual).IsLessThanOrEqualTo(baseline + (poco ? ownedBytes : 0L));
        await Assert.That(control).IsGreaterThanOrEqualTo(actual + 37L * Count);
    }

    private static async Task Drain(ValueTask<bool>[] pending)
    {
        foreach (var operation in pending)
            if (!await operation) throw new InvalidOperationException("SET did not succeed.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureJson(SystemTextJsonSerializer serializer, ArrayBufferWriter<byte> destination, bool allocate)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < Count; index++)
        {
            destination.Clear();
            serializer.Serialize(destination, 42);
            if (serializer.Deserialize<int>(destination.WrittenSpan) != 42) throw new InvalidOperationException();
            if (allocate) Volatile.Write(ref _escape, new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureSet(RespireClient client, Payload payload, bool poco, RespireValue raw,
        bool baseline, ValueTask<bool>[] pending, bool allocate)
    {
        // Warm this thread's serialization/write scratch before counting; no await in the region.
        pending[0] = Send();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 1; index < pending.Length; index++)
        {
            pending[index] = Send();
            if (allocate) Volatile.Write(ref _escape, new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;

        ValueTask<bool> Send()
        {
            if (baseline) return client.SetAsync(poco ? "payload" : "number", raw);
            return poco ? client.SetAsync("payload", payload) : client.SetAsync<int>("number", 42);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasurePayloadArrays(int length)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < Count; index++) Volatile.Write(ref _escape, new byte[length]);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    public sealed record Payload(int Value);
}
