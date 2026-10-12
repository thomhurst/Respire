using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

[NotInParallel]
public class ImmediateRetryStateTests
{
    [Test]
    public async Task SnapshotOwnsBytesAndSlotUntilReturn()
    {
        var key = "{old}key"u8.ToArray();
        var command = new Cmd1(Verbs.StrLen, (RespireValue)key);
        command.TryGetClusterSlot(out var expectedSlot);
        var state = ImmediateRetryState.Rent(in command);
        try
        {
            var frame = state.Frame.ToArray();
            "{new}key"u8.CopyTo(key);
            await Assert.That(state.Frame.ToArray()).IsEquivalentTo(frame);
            await Assert.That(state.TryGetClusterSlot(out var slot)).IsTrue();
            await Assert.That(slot).IsEqualTo(expectedSlot);
            state.Accepted();
            await Assert.That(state.Retries).IsEqualTo(0);
            state.IsRetry = true;
            state.Accepted();
            await Assert.That(state.Retries).IsEqualTo(1);
        }
        finally { state.Return(); }
    }

    [Test]
    [Arguments(8)]
    [Arguments(100_000)]
    public async Task ReturnClearsPayloadAndState(int payloadSize)
    {
        var payload = new byte[payloadSize];
        Array.Fill(payload, (byte)'x');
        var command = new Cmd2(Verbs.Set, "key", (RespireValue)payload);
        var state = ImmediateRetryState.Rent(in command);
        var frame = state.Frame;
        state.IsRetry = true;
        state.Accepted();
        state.Return();
        await Assert.That(frame.Span.IndexOfAnyExcept((byte)0)).IsEqualTo(-1);
        await Assert.That(state.Frame.Length).IsEqualTo(0);
        await Assert.That(state.IsRetry).IsFalse();
        await Assert.That(state.AcceptedAttempt).IsFalse();
        await Assert.That(state.Retries).IsEqualTo(0);
        await Assert.That(state.TryGetClusterSlot(out _)).IsFalse();
    }

    [Test]
    public async Task SerializationFailureDoesNotPoisonTheNextRental()
    {
        await Assert.That(() => ImmediateRetryState.Rent(new FailingCommand())).ThrowsExactly<InvalidOperationException>();
        var command = new Cmd1(Verbs.StrLen, "key");
        var state = ImmediateRetryState.Rent(in command);
        try
        {
            await Assert.That(System.Text.Encoding.ASCII.GetString(state.Frame.Span))
                .IsEqualTo("*2\r\n$6\r\nSTRLEN\r\n$3\r\nkey\r\n");
        }
        finally { state.Return(); }
    }

    [Test]
    public async Task ConcurrentRentalsHaveIndependentFramesAndCounters()
    {
        var first = ImmediateRetryState.Rent(new Cmd1(Verbs.StrLen, "first"));
        var second = ImmediateRetryState.Rent(new Cmd1(Verbs.StrLen, "second"));
        try
        {
            first.IsRetry = true;
            first.Accepted();
            await Assert.That(ReferenceEquals(first, second)).IsFalse();
            await Assert.That(second.Retries).IsEqualTo(0);
            await Assert.That(System.Text.Encoding.ASCII.GetString(first.Frame.Span)).Contains("first");
            await Assert.That(System.Text.Encoding.ASCII.GetString(second.Frame.Span)).Contains("second");
        }
        finally { first.Return(); second.Return(); }
    }

    [Test]
    public async Task WarmedSnapshotOwnershipAllocatesNothing()
    {
        var command = new Cmd1(Verbs.StrLen, "key");
        Measure(in command, false);
        Measure(in command, true);
        MeasurePreviousSnapshot(in command);
        var measured = AllocationMeasurement.WithoutConcurrentGc(() =>
            (Bytes: Measure(in command, false), Control: Measure(in command, true),
                Previous: MeasurePreviousSnapshot(in command)));
        Console.WriteLine($"Retry snapshot ownership: {measured}");
        await Assert.That(measured.Bytes).IsEqualTo(0L);
        await Assert.That(measured.Control).IsGreaterThanOrEqualTo(37_000L);
        await Assert.That(measured.Previous).IsGreaterThan(0L);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure(in Cmd1 command, bool control)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            var state = ImmediateRetryState.Rent(in command);
            state.Return();
            if (control) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasurePreviousSnapshot(in Cmd1 command)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            var frame = SnapshotCommand.Create(in command, captureCacheMetadata: false);
            if (frame.GetWriteSizeHint() == 0) throw new InvalidOperationException("Missing snapshot.");
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private readonly struct FailingCommand : IRespCommand
    {
        public ReadCommandKind ReadKind => ReadCommandKind.None;
        public void Write(ref RespWriter writer)
        {
            writer.WriteRaw("secret"u8);
            throw new InvalidOperationException("Serialization failed.");
        }
    }
}
