using System.Buffers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Respire.Commands;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

[NotInParallel]
public class ReceiveBufferTests
{
    [Test]
    [Arguments(1024)]
    [Arguments(1031)]
    public async Task GrownReceiveStorageSurvivesCollectionWhileTheNextReadIsPending(int bufferSize)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var text = new string('ø', 8192);
        await using var server = new FakeRespServer(Encoding.UTF8.GetBytes($"+{text}\r\n"));
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new RespireConnectionOptions { ReceiveBufferSize = bufferSize }, cancellationToken: deadline.Token);
        using (var response = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame), deadline.Token))
            await Assert.That(response.AsString()).IsEqualTo(text);

        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.SuppressReply = _ =>
        {
            received.TrySetResult();
            return true;
        };
        var next = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame), deadline.Token).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        await server.SendRawAsync(FakeRespServer.PongReply);
        using var final = await next.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(final.AsString()).IsEqualTo("PONG");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GrowthPreservesTheStoragePinningPolicy(bool pinned)
    {
        var original = ReceiveBuffer.Rent(4096, pinned);
        var grown = original.Grow(8192);
        try
        {
            await Assert.That(grown.Array.Length).IsGreaterThanOrEqualTo(8192);
            await Assert.That(grown.Array).IsNotSameReferenceAs(original.Array);
            using var handle = grown.Memory[17..].Pin();
            await Assert.That(GetNativeHandle(handle).IsAllocated).IsEqualTo(!pinned);
        }
        finally
        {
            original.Return();
            grown.Return();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SlicedMemoryHasTheCorrectNativePinLifetime(bool pinned)
    {
        var buffer = ReceiveBuffer.Rent(4096, pinned);
        try
        {
            buffer.Array[39] = 42;
            using var handle = buffer.Memory.Slice(32, 64).Slice(7, 1).Pin();
            // Inspect the BCL handle, not production owner state. The ordinary-memory case
            // is a positive control: a real native GCHandle must remain allocated until disposal.
            var nativeHandle = GetNativeHandle(handle);
            await Assert.That(nativeHandle.IsAllocated).IsEqualTo(!pinned);
            await Assert.That(ReadPinnedByte(handle)).IsEqualTo((byte)42);
        }
        finally { buffer.Return(); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReturnRecyclesOnlyPoolOwnedArrays(bool pinned)
    {
        var buffer = ReceiveBuffer.Rent(4096, pinned);
        buffer.Return();
        var pooled = RespirePools.ResponsePayloads.Rent(4096);
        try
        {
            if (pinned) await Assert.That(pooled).IsNotSameReferenceAs(buffer.Array);
            else await Assert.That(pooled).IsSameReferenceAs(buffer.Array);
        }
        finally { RespirePools.ResponsePayloads.Return(pooled); }
    }

    private static GCHandle GetNativeHandle(MemoryHandle handle)
        => (GCHandle)typeof(MemoryHandle).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(field => field.FieldType == typeof(GCHandle)).GetValue(handle)!;

    private static unsafe byte ReadPinnedByte(MemoryHandle handle) => *(byte*)handle.Pointer;
}
