using System.Buffers;
using System.Net.Sockets;
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
        PausedWriteStream? transport = null;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], Protocol = RespProtocol.Resp2, Connections = 1,
            TestingStreamFactory = async (host, port, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(host, port, token);
                    return transport = new PausedWriteStream(new NetworkStream(socket, ownsSocket: true));
                }
                catch { socket.Dispose(); throw; }
            },
        });
        var payload = new Payload(42);
        var pending = new ValueTask<bool>[Count + 2];
        var serialized = client.Serialize(payload);
        var payloadLength = serialized.ToString().Length;
        RespireValue raw = poco ? serialized : 42;
        for (var warmup = 0; warmup < 4; warmup++)
        {
            _ = await Sample(baseline: false, allocate: warmup == 3);
            _ = await Sample(baseline: true, allocate: false);
        }
        var actual = await Sample(baseline: false, allocate: false);
        var baseline = await Sample(baseline: true, allocate: false);
        var rawControl = await Sample(baseline: true, allocate: false);
        var control = await Sample(baseline: false, allocate: true);
        _ = MeasurePayloadArrays(payloadLength);
        var ownedBytes = AllocationMeasurement.WithoutConcurrentGc(() => MeasurePayloadArrays(payloadLength));
        // Compare the same wire payload and transport path. Existing command overhead belongs
        // to the raw baseline; typed serialization adds at most its required owned byte array.
        await Assert.That(actual).IsLessThanOrEqualTo(baseline + (poco ? ownedBytes : 0L));
        await Assert.That(rawControl).IsEqualTo(baseline);
        await Assert.That(control).IsGreaterThanOrEqualTo(actual + 37L * Count);
        await Assert.That(control).IsGreaterThan(baseline + (poco ? ownedBytes : 0L));

        async Task<long> Sample(bool baseline, bool allocate)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var connection = client.Core.Multiplexer.GetConnection();
            while (!connection.IsFlushLoopWaiting) await Task.Delay(1, deadline.Token);
            transport!.Pause();
            long bytes;
            try
            {
                // Suspend the first flush before counting. Socket completion, receive
                // callbacks and subsequent flushes cannot race command admission below.
                pending[0] = client.SetAsync("gate", raw);
                await transport.WriteStarted.Task.WaitAsync(deadline.Token);
                bytes = AllocationMeasurement.WithoutConcurrentGc(() =>
                    MeasureSet(client, payload, poco, raw, baseline, pending, allocate));
                await Assert.That(transport.ResumeWrites.Task.IsCompleted).IsFalse();
                await Assert.That(pending.All(operation => !operation.IsCompleted)).IsTrue();
            }
            finally
            {
                transport.Release();
            }
            await Drain(pending).WaitAsync(deadline.Token);
            // A distinct response type provides a FIFO barrier after SET's receive
            // references have returned to the pool, before the next sample is armed.
            _ = await client.PingAsync().AsTask().WaitAsync(deadline.Token);
            return bytes;
        }
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
        pending[1] = Send();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 2; index < pending.Length; index++)
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

    private sealed class PausedWriteStream(Stream inner) : Stream
    {
        private volatile bool _paused;
        public TaskCompletionSource WriteStarted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResumeWrites { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Pause()
        {
            WriteStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ResumeWrites = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _paused = true;
        }

        public void Release()
        {
            _paused = false;
            ResumeWrites.TrySetResult();
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => _paused ? WritePausedAsync(buffer, cancellationToken) : inner.WriteAsync(buffer, cancellationToken);

        private async ValueTask WritePausedAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            WriteStarted.TrySetResult();
            await ResumeWrites.Task.WaitAsync(cancellationToken);
            await inner.WriteAsync(buffer, cancellationToken);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.ReadAsync(buffer, cancellationToken);
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { Release(); inner.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
