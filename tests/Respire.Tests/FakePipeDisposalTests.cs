using System.Buffers;
using System.IO.Pipelines;
using System.Reflection;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class FakePipeDisposalTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    [Test]
    public async Task DisposalDoesNotCompleteReaderBeforeItsResultIsAdvanced()
    {
        var input = new Pipe();
        var output = new Pipe();
        using var release = new ManualResetEventSlim();
        var reader = new HeldAdvanceReader(input.Reader, release);
        using var stream = CreateStream(reader, output.Writer);
        var buffer = new byte[1];
        var read = Task.Run(async () => await stream.ReadAsync(buffer));
        try
        {
            await input.Writer.WriteAsync(new byte[] { 42 });
            await reader.Entered.Task.WaitAsync(Limit);
            stream.Dispose();
        }
        finally
        {
            release.Set();
        }
        try
        {
            // The read already owns a non-cancelled result; only AdvanceTo is held.
            await Assert.That(await read.WaitAsync(Limit)).IsEqualTo(1);
            await Assert.That(buffer[0]).IsEqualTo((byte)42);
            await reader.Completed.Task.WaitAsync(Limit);
            await Assert.That(async () => await stream.ReadAsync(buffer)).ThrowsExactly<ObjectDisposedException>();
        }
        finally
        {
            await input.Writer.CompleteAsync();
            await output.Reader.CompleteAsync();
        }
    }

    [Test]
    public async Task DisposalDoesNotCompleteWriterDuringAnAcceptedWrite()
    {
        var input = new Pipe();
        var output = new Pipe();
        using var release = new ManualResetEventSlim();
        var writer = new HeldMemoryWriter(output.Writer, release);
        using var stream = CreateStream(input.Reader, writer);
        var write = Task.Run(async () => await stream.WriteAsync(new byte[] { 42 }));
        try
        {
            await writer.Entered.Task.WaitAsync(Limit);
            stream.Dispose();
        }
        finally
        {
            release.Set();
        }
        try
        {
            try { await write.WaitAsync(Limit); }
            catch (OperationCanceledException) { } // Disposal may cancel the pending flush.
            await writer.Completed.Task.WaitAsync(Limit);
            await Assert.That(async () => await stream.WriteAsync(new byte[] { 43 })).ThrowsExactly<ObjectDisposedException>();
        }
        finally
        {
            await input.Writer.CompleteAsync();
            await output.Reader.CompleteAsync();
        }
    }

    private static Stream CreateStream(PipeReader reader, PipeWriter writer)
        => (Stream)Activator.CreateInstance(typeof(RespireFakeServer).Assembly.GetType("Respire.Testing.DuplexPipeStream")!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [reader, writer, null], null)!;

    private sealed class HeldAdvanceReader(PipeReader inner, ManualResetEventSlim release) : PipeReader
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
            Entered.TrySetResult();
            if (!release.Wait(Limit)) throw new TimeoutException("The controlled read boundary was not released.");
            inner.AdvanceTo(consumed, examined);
        }
        public override void CancelPendingRead() => inner.CancelPendingRead();
        public override void Complete(Exception? exception = null)
        {
            inner.Complete(exception);
            Completed.TrySetResult();
        }
        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default) => inner.ReadAsync(cancellationToken);
        public override bool TryRead(out ReadResult result) => inner.TryRead(out result);
    }

    private sealed class HeldMemoryWriter(PipeWriter inner, ManualResetEventSlim release) : PipeWriter
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void Advance(int bytes) => inner.Advance(bytes);
        public override void CancelPendingFlush() => inner.CancelPendingFlush();
        public override void Complete(Exception? exception = null)
        {
            inner.Complete(exception);
            Completed.TrySetResult();
        }
        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);
        public override Memory<byte> GetMemory(int sizeHint = 0)
        {
            Hold();
            return inner.GetMemory(sizeHint);
        }
        public override Span<byte> GetSpan(int sizeHint = 0)
        {
            Hold();
            return inner.GetSpan(sizeHint);
        }
        private void Hold()
        {
            Entered.TrySetResult();
            if (!release.Wait(Limit)) throw new TimeoutException("The controlled write boundary was not released.");
        }
    }
}
