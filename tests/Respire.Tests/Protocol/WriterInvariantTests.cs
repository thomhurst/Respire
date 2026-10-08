using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Protocol;

public class WriterInvariantTests
{
#if DEBUG
    [Test]
    [Arguments(16)]
    [Arguments(65_536)]
    public async Task UncompletedWriterCannotBeConsumedEvenAfterGrowth(int payloadLength)
    {
        var buffer = new WriteBuffer(128);
        try
        {
            buffer.Append("+before\r\n"u8);
            var mark = buffer.Count;
            WritePayload(buffer, payloadLength, complete: false);
            await Assert.That(buffer.Count).IsEqualTo(mark);
            await Assert.That(() => buffer.WrittenMemory).ThrowsExactly<InvalidOperationException>();
            buffer.TruncateTo(mark);
            await Assert.That(buffer.WrittenMemory.Span.SequenceEqual("+before\r\n"u8)).IsTrue();
            WritePayload(buffer, payloadLength, complete: true);
            await Assert.That(buffer.WrittenMemory.Length).IsGreaterThan(mark);
        }
        finally { buffer.Release(); }
    }

    [Test]
    [Arguments(1)]
    [Arguments(6)]
    public async Task UnderestimatedHintIsRejectedDespiteSpareCapacity(int bound)
    {
        var buffer = new WriteBuffer(512);
        try
        {
            buffer.Append("+before\r\n"u8);
            var mark = buffer.Count;
            await Assert.That(buffer.Capacity - mark).IsGreaterThan(7);
            await Assert.That(() => WritePong(buffer, bound)).ThrowsExactly<InvalidOperationException>();
            await Assert.That(buffer.Count).IsEqualTo(mark);
            buffer.TruncateTo(mark);
            WritePong(buffer, 7);
            await Assert.That(buffer.WrittenMemory.Span.SequenceEqual("+before\r\n+PONG\r\n"u8)).IsTrue();
        }
        finally { buffer.Release(); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ResetAndReleaseDiscardUnpublishedWriterState(bool release)
    {
        var buffer = new WriteBuffer(128);
        try
        {
            WritePayload(buffer, 16, complete: false);
            if (release) buffer.Release();
            else buffer.Reset();
            await Assert.That(buffer.WrittenMemory.Length).IsEqualTo(0);
        }
        finally { buffer.Release(); }
    }

    [Test]
    public async Task CombinedBoundCannotBeReusedAfterCompletion()
    {
        var buffer = new WriteBuffer(128);
        try
        {
            buffer.Append("+before\r\n"u8);
            var mark = buffer.Count;
            await Assert.That(() => WriteTwoPongs(buffer, 7)).ThrowsExactly<InvalidOperationException>();
            await Assert.That(buffer.Count).IsEqualTo(mark + 7);
            await Assert.That(() => buffer.WrittenMemory).ThrowsExactly<InvalidOperationException>();
            buffer.TruncateTo(mark + 7);
            await Assert.That(buffer.WrittenMemory.Span.SequenceEqual("+before\r\n+PONG\r\n"u8)).IsTrue();
        }
        finally { buffer.Release(); }
    }
#endif

    [Test]
    [Arguments(0)]
    [Arguments(14)]
    public async Task RepeatedCompletionPublishesEachFrameWithinTheCombinedBound(int bound)
    {
        var buffer = new WriteBuffer(128);
        try
        {
            WriteTwoPongs(buffer, bound);
            await Assert.That(buffer.WrittenMemory.Span.SequenceEqual("+PONG\r\n+PONG\r\n"u8)).IsTrue();
        }
        finally { buffer.Release(); }
    }

    [Test]
    public async Task EmptyWriterLeavesPublishedBytesReadable()
    {
        var buffer = new WriteBuffer(128);
        try
        {
            buffer.Append("+before\r\n"u8);
            _ = new RespWriter(buffer, 7);
            await Assert.That(buffer.WrittenMemory.Span.SequenceEqual("+before\r\n"u8)).IsTrue();
        }
        finally { buffer.Release(); }
    }

    private static void WritePayload(WriteBuffer buffer, int length, bool complete)
    {
        var writer = new RespWriter(buffer);
        // Start ASCII, then expand during suffix rewriting. Large payloads grow again after
        // the first reservation, exercising preservation of the unfinished-writer state.
        writer.WriteBulkString("x" + new string('é', length));
        if (complete) writer.Complete();
    }

    private static void WritePong(WriteBuffer buffer, int bound)
    {
        var writer = new RespWriter(buffer, bound);
        writer.WriteRaw("+PONG\r\n"u8);
        writer.Complete();
    }

    private static void WriteTwoPongs(WriteBuffer buffer, int bound)
    {
        var writer = new RespWriter(buffer, bound);
        writer.WriteRaw("+PONG\r\n"u8);
        writer.Complete();
        writer.Complete();
        writer.WriteRaw("+PONG\r\n"u8);
        writer.Complete();
    }
}
