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
            var command = new StreamedSetCommand("allocation", source, 1, default, SetWhen.Always);
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
