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
    [NotInParallel]
    [Arguments(false, 16)]
    [Arguments(true, 16)]
    [Arguments(false, 65_536)]
    [Arguments(true, 65_536)]
    public async Task EarlierWriterCannotResumeAfterAnotherWriterPublishes(bool complete, int payloadLength)
    {
        var buffer = new WriteBuffer(128);
        try
        {
            buffer.Append("+before\r\n"u8);
            var payload = "x" + new string('é', payloadLength);
            var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
            var expected = System.Text.Encoding.UTF8.GetBytes($"+before\r\n${bytes.Length}\r\n{payload}\r\n");
            await Assert.That(() => ResumeEarlierWriter(buffer, complete, payload))
                .ThrowsExactly<InvalidOperationException>();
            await Assert.That(buffer.Count).IsEqualTo(expected.Length);
            await Assert.That(buffer.WrittenMemory.Span.SequenceEqual(expected)).IsTrue();
            WritePong(buffer, 7);
            await Assert.That(buffer.WrittenMemory.Span.SequenceEqual([.. expected, .. "+PONG\r\n"u8])).IsTrue();
        }
        finally { buffer.Release(); }
    }

    [Test]
    [NotInParallel]
    [Arguments(false, 0)]
    [Arguments(true, 0)]
    [Arguments(false, 1)]
    [Arguments(true, 1)]
    [Arguments(false, 2)]
    [Arguments(true, 2)]
    public async Task DiscardingTheBufferInvalidatesItsExistingWriter(bool complete, int discard)
    {
        var buffer = new WriteBuffer(128);
        try
        {
            await Assert.That(() => ResumeDiscardedWriter(buffer, complete, discard))
                .ThrowsExactly<InvalidOperationException>();
            await Assert.That(buffer.Count).IsEqualTo(0);
            await Assert.That(buffer.WrittenMemory.Length).IsEqualTo(0);
            WritePong(buffer, 7);
            await Assert.That(buffer.WrittenMemory.Span.SequenceEqual("+PONG\r\n"u8)).IsTrue();
        }
        finally { buffer.Release(); }
    }

    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReplacingTheArrayInvalidatesAnEmptyEarlierWriter(bool complete)
    {
        var buffer = new WriteBuffer(128);
        try
        {
            await Assert.That(() => ResumeWriterAfterAnotherReservationGrows(buffer, complete))
                .ThrowsExactly<InvalidOperationException>();
            await Assert.That(buffer.Count).IsEqualTo(0);
            await Assert.That(buffer.WrittenMemory.Length).IsEqualTo(0);
            WritePong(buffer, 7);
            await Assert.That(buffer.WrittenMemory.Span.SequenceEqual("+PONG\r\n"u8)).IsTrue();
        }
        finally { buffer.Release(); }
    }

    private static void ResumeEarlierWriter(WriteBuffer buffer, bool complete, string payload)
    {
        var earlier = new RespWriter(buffer);
        var later = new RespWriter(buffer);
        later.WriteBulkString(payload);
        later.Complete();
        if (complete) earlier.Complete();
        else earlier.WriteRaw("+FAIL\r\n"u8);
    }

    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AppendingPublishedBytesInvalidatesAnEarlierWriter(bool complete)
    {
        var buffer = new WriteBuffer(128);
        try
        {
            await Assert.That(() => ResumeWriterAfterAppend(buffer, complete))
                .ThrowsExactly<InvalidOperationException>();
            await Assert.That(buffer.WrittenMemory.Span.SequenceEqual("+PONG\r\n"u8)).IsTrue();
            WritePong(buffer, 7);
            await Assert.That(buffer.WrittenMemory.Span.SequenceEqual("+PONG\r\n+PONG\r\n"u8)).IsTrue();
        }
        finally { buffer.Release(); }
    }

    private static void ResumeWriterAfterAppend(WriteBuffer buffer, bool complete)
    {
        var writer = new RespWriter(buffer);
        buffer.Append("+PONG\r\n"u8);
        if (complete) writer.Complete();
        else writer.WriteRaw("+FAIL\r\n"u8);
    }

    private static void ResumeDiscardedWriter(WriteBuffer buffer, bool complete, int discard)
    {
        var writer = new RespWriter(buffer);
        writer.WriteRaw("+PONG\r\n"u8);
        if (discard == 0) buffer.TruncateTo(0);
        else if (discard == 1) buffer.Reset();
        else buffer.Release();
        if (complete) writer.Complete();
        else writer.WriteRaw("+FAIL\r\n"u8);
    }

    private static void ResumeWriterAfterAnotherReservationGrows(WriteBuffer buffer, bool complete)
    {
        var earlier = new RespWriter(buffer);
        _ = new RespWriter(buffer, buffer.Capacity + 1);
        if (complete) earlier.Complete();
        else earlier.WriteRaw("+FAIL\r\n"u8);
    }

    [Test]
    [Arguments(16)]
    [Arguments(65_536)]
    public async Task SecondWriterCannotDiscardUnpublishedBytes(int payloadLength)
    {
        var buffer = new WriteBuffer(128);
        try
        {
            buffer.Append("+before\r\n"u8);
            var mark = buffer.Count;
            WritePayload(buffer, payloadLength, complete: false);
            await Assert.That(() => { _ = new RespWriter(buffer, 7); })
                .ThrowsExactly<InvalidOperationException>();
            await Assert.That(buffer.Count).IsEqualTo(mark);
            await Assert.That(() => buffer.WrittenMemory).ThrowsExactly<InvalidOperationException>();
            buffer.TruncateTo(mark);
            WritePong(buffer, 7);
            await Assert.That(buffer.WrittenMemory.Span.SequenceEqual("+before\r\n+PONG\r\n"u8)).IsTrue();
        }
        finally { buffer.Release(); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PreviouslyCreatedWriterCannotChangeAnotherWritersUnpublishedBytes(bool complete)
    {
        var buffer = new WriteBuffer(128);
        try
        {
            buffer.Append("+before\r\n"u8);
            await Assert.That(() => UseCompetingWriter(buffer, complete))
                .ThrowsExactly<InvalidOperationException>();
            await Assert.That(buffer.WrittenMemory.Span.SequenceEqual("+before\r\n+PONG\r\n"u8)).IsTrue();
        }
        finally { buffer.Release(); }
    }

    private static void UseCompetingWriter(WriteBuffer buffer, bool complete)
    {
        var owner = new RespWriter(buffer, 7);
        var competing = new RespWriter(buffer, 7);
        owner.WriteRaw("+PONG\r\n"u8);
        try
        {
            if (complete) competing.Complete();
            else competing.WriteRaw("+FAIL\r\n"u8);
        }
        finally { owner.Complete(); }
    }

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
