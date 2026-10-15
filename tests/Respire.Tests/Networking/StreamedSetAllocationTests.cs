using System.Reflection;
using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public sealed class StreamedSetAllocationTests
{
    [Test]
    [NotInParallel]
    public async Task ExactMemoryStreamReusesCompletedFillTask()
    {
        MeasureMemoryStreamFills(8, false);
        MeasureMemoryStreamFills(8, true);
        var allocation = AllocationMeasurement.WithoutConcurrentGc(() => MeasureMemoryStreamFills(8, false));
        var control = AllocationMeasurement.WithoutConcurrentGc(() => MeasureMemoryStreamFills(8, true));
        Console.WriteLine($"Seven subsequent exact MemoryStream fills: {allocation} B; recreated-task control: {control} B");
        await Assert.That(allocation).IsEqualTo(0);
        await Assert.That(control).IsGreaterThanOrEqualTo(7 * IntPtr.Size);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureMemoryStreamFills(int chunks, bool recreateTaskControl)
    {
        using var source = new MemoryStream(new byte[chunks * RespireConnection.StreamChunkSize]);
        using var reader = new RespireConnection.StreamPayloadReader(source, source.Length);
        // Reader, pooled buffer and the first completed Task are outside this boundary.
        var first = reader.StartRead(CancellationToken.None);
        if (!first.IsCompletedSuccessfully) throw new InvalidOperationException("Exact MemoryStream fill suspended.");
        long allocated = 0;
        for (var index = 1; index < chunks; index++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var fill = reader.StartRead(CancellationToken.None);
            // Recreating the completed Task is also a causal control for the removed site.
            var anchor = recreateTaskControl ? Task.FromResult(fill.Result) : null;
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(anchor);
            if (!fill.IsCompletedSuccessfully || fill.Result.Length != RespireConnection.StreamChunkSize)
                throw new InvalidOperationException("Unexpected exact MemoryStream chunk.");
        }
        return allocated;
    }

    [Test]
    public async Task ExactMemoryStreamFillTaskTracksCurrentBytesAndShortTail()
    {
        var payload = new byte[2 * RespireConnection.StreamChunkSize + 17];
        Array.Fill(payload, (byte)1, 0, RespireConnection.StreamChunkSize);
        Array.Fill(payload, (byte)2, RespireConnection.StreamChunkSize, RespireConnection.StreamChunkSize);
        Array.Fill(payload, (byte)3, 2 * RespireConnection.StreamChunkSize, 17);
        using var source = new MemoryStream(payload);
        using var reader = new RespireConnection.StreamPayloadReader(source, payload.Length);
        var first = reader.StartRead(CancellationToken.None);
        await Assert.That(first.Result.ToArray()).IsEquivalentTo(payload[..RespireConnection.StreamChunkSize]);
        var second = reader.StartRead(CancellationToken.None);
        await Assert.That(ReferenceEquals(first, second)).IsTrue();
        await Assert.That(second.Result.ToArray()).IsEquivalentTo(
            payload[RespireConnection.StreamChunkSize..(2 * RespireConnection.StreamChunkSize)]);
        var tail = reader.StartRead(CancellationToken.None);
        await Assert.That(ReferenceEquals(second, tail)).IsFalse();
        await Assert.That(tail.Result.ToArray()).IsEquivalentTo(payload[(2 * RespireConnection.StreamChunkSize)..]);
        await Assert.That(reader.IsComplete).IsTrue();
        await Assert.That(reader.ConsumedPrefix.Length).IsEqualTo(17);
        await Assert.That(reader.UnknownPositionReadError).IsNull();
    }

    [Test]
    public async Task ExactMemoryStreamFailedFillDoesNotReusePreviousSuccess()
    {
        using var source = new MemoryStream(new byte[RespireConnection.StreamChunkSize + 17]);
        using var reader = new RespireConnection.StreamPayloadReader(source, 2 * RespireConnection.StreamChunkSize);
        var first = reader.StartRead(CancellationToken.None);
        await first;
        var failed = reader.StartRead(CancellationToken.None);
        await Assert.That(ReferenceEquals(first, failed)).IsFalse();
        await Assert.That(async () => await failed).Throws<EndOfStreamException>();
        await Assert.That(reader.ConsumedPrefix.Length).IsEqualTo(17);
        await Assert.That(reader.UnknownPositionReadError).IsNull();
        await Assert.That(reader.IsComplete).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExactMemoryStreamCancellationOrDisposalRetainsFailure(bool disposeSource)
    {
        using var source = new MemoryStream(new byte[2 * RespireConnection.StreamChunkSize]);
        using var reader = new RespireConnection.StreamPayloadReader(source, source.Length);
        var first = reader.StartRead(CancellationToken.None);
        await first;
        using var cancellation = new CancellationTokenSource();
        if (disposeSource) source.Dispose();
        else cancellation.Cancel();
        var failed = reader.StartRead(cancellation.Token);
        await Assert.That(ReferenceEquals(first, failed)).IsFalse();
        if (disposeSource)
        {
            var error = await Assert.That(async () => await failed).Throws<ObjectDisposedException>();
            await Assert.That(ReferenceEquals(error, reader.UnknownPositionReadError)).IsTrue();
        }
        else
        {
            var error = await Assert.That(async () => await failed).Throws<OperationCanceledException>();
            await Assert.That(failed.IsCanceled).IsTrue();
            await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
            await Assert.That(reader.UnknownPositionReadError).IsNull();
            await Assert.That(source.Position).IsEqualTo(RespireConnection.StreamChunkSize);
        }
        await Assert.That(reader.ConsumedPrefix.Length).IsEqualTo(0);
        await Assert.That(reader.IsComplete).IsFalse();
    }

    [Test]
    [NotInParallel]
    public async Task SuspendedFinalFrameWaitReusesItsStateMachine()
    {
        MeasureFinalFrameWaits(64, false);
        MeasureFinalFrameWaits(4, true);
        var allocation = AllocationMeasurement.WithoutConcurrentGc(() => MeasureFinalFrameWaits(16, false));
        var control = AllocationMeasurement.WithoutConcurrentGc(() => MeasureFinalFrameWaits(16, true));
        Console.WriteLine($"Suspended final-frame waits: {allocation / 16} B/op; positive control: {control / 16} B/op");
        await Assert.That(allocation).IsEqualTo(0);
        await Assert.That(control).IsGreaterThanOrEqualTo(16 * 37);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureFinalFrameWaits(int count, bool allocateControl)
    {
        long allocated = 0;
        for (var index = 0; index < count; index++)
        {
            var write = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var before = GC.GetAllocatedBytesForCurrentThread();
            var pending = RespireConnection.WaitForFinalFrameWriteAsync(write.Task, CancellationToken.None);
            var anchor = allocateControl ? new byte[37] : null;
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(anchor);
            if (pending.IsCompleted) throw new InvalidOperationException("The final write wait did not suspend.");
            var awaiter = pending.GetAwaiter();
            awaiter.UnsafeOnCompleted(() => completed.TrySetResult());
            write.SetResult();
            if (!completed.Task.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The final write wait did not complete.");
            // Consume on the measured thread so the pooled box returns to that thread's cache.
            awaiter.GetResult();
        }
        return allocated;
    }

    [Test]
    [NotInParallel]
    public async Task SuspendedUploadReusesItsStateMachine()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new() { CommandTimeout = null });
        var gate = (SemaphoreSlim)typeof(RespireConnection)
            .GetField("_streamingGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
        MeasureStarts(connection, gate, 64, false);
        MeasureStarts(connection, gate, 4, true);
        var allocation = AllocationMeasurement.WithoutConcurrentGc(
            () => MeasureStarts(connection, gate, 16, false));
        var control = AllocationMeasurement.WithoutConcurrentGc(
            () => MeasureStarts(connection, gate, 16, true));
        Console.WriteLine($"Suspended streamed starts: {allocation / 16} B/op; positive control: {control / 16} B/op");
        // The gate and linked cancellation sources still allocate. This ceiling detects the
        // former per-upload state-machine boxes, rather than claiming a zero-allocation upload.
        await Assert.That(allocation).IsLessThanOrEqualTo(16 * 2000);
        await Assert.That(control).IsGreaterThanOrEqualTo(16 * 2048);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureStarts(RespireConnection connection, SemaphoreSlim gate, int count, bool allocateControl)
    {
        long allocated = 0;
        for (var index = 0; index < count; index++)
        {
            using var source = new MemoryStream("a"u8.ToArray());
            var command = new StreamedSetCommand("allocation", source, 1, default, RespireSetWhen.Always);
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!gate.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Upload gate did not become available.");
            var before = GC.GetAllocatedBytesForCurrentThread();
            ValueTask<RespValue> pending;
            try
            {
                pending = connection.SendAsync(in command, commandName: "SET");
                var anchor = allocateControl ? new byte[2048] : null;
                allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                GC.KeepAlive(anchor);
                if (pending.IsCompleted)
                    throw new InvalidOperationException("The upload did not suspend at admission.");
            }
            finally { gate.Release(); }
            // Holding the upload gate forces suspension before source reads and transport work.
            // Completion and GetResult stay outside the counter; GetResult returns the pooled
            // box on this initiating thread instead of via ValueTask.AsTask on a worker.
            var awaiter = pending.GetAwaiter();
            if (!awaiter.IsCompleted)
            {
                awaiter.UnsafeOnCompleted(() => completed.TrySetResult());
                if (!completed.Task.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("The suspended upload did not complete.");
            }
            using var reply = awaiter.GetResult();
            if (reply.AsString() != "OK") throw new InvalidOperationException("Unexpected upload reply.");
        }
        return allocated;
    }
}
