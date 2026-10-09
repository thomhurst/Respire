using System.Runtime.CompilerServices;
using System.Text;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public partial class ErrorMetricsTests
{
    [Test]
    public async Task SuccessfulPayloadReadsPreserveAllocationContract()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture();
        using var pipe = new RespBulkPayloadPipe();
        pipe.GetMemory(8_000).Span[..8_000].Clear();
        pipe.Advance(8_000);
        await pipe.FlushAsync();
        pipe.Complete();
        var buffer = new byte[1];
        _ = MeasurePayloadReads(pipe.ReadStream, buffer, false);
        _ = MeasurePayloadReads(pipe.ReadStream, buffer, true);
        var measured = AllocationMeasurement.WithoutConcurrentGc(() =>
            (Bytes: MeasurePayloadReads(pipe.ReadStream, buffer, false),
                Control: MeasurePayloadReads(pipe.ReadStream, buffer, true)));
        await Assert.That(measured.Bytes).IsEqualTo(0L);
        await Assert.That(measured.Control).IsGreaterThanOrEqualTo(32_000L);
        await Assert.That(capture.Items).IsEmpty();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasurePayloadReads(Stream stream, byte[] buffer, bool allocate)
    {
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1_000; index++)
        {
            _ = stream.Read(buffer, 0, 1);
            if (allocate) GC.KeepAlive(new byte[32]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }

    [Test]
    [MatrixDataSource]
    public async Task UploadCleanupRecordsCompletedErrorReplyOnce(
        [Matrix(false, true)] bool throwOnError, [Matrix(false, true)] bool deferred,
        [Matrix(0, 3)] int attempts)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture(throwOnMeasurement: true);
        var source = new PendingResponseSource();
        source.Configure(throwOnError, "SET");
        source.PrepareForUse();
        source.ErrorAttempts = attempts;
        var cleanup = RespireConnection.ObserveStreamedSetResponseAsync(source);
        var reply = RespValue.Error("NOPERM private-payload"u8.ToArray());
        if (deferred)
        {
            var scheduler = new CompletionScheduler();
            scheduler.Add(source, in reply);
            scheduler.Flush();
            await scheduler.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        else
        {
            source.TrySetResult(in reply);
            source.ReleaseRef();
        }
        await cleanup;
        var item = capture.Items.Single();
        await Assert.That((bool)item.Tags["redis.client.errors.internal"]!).IsTrue();
        await Assert.That(item.Tags["db.response.status_code"]).IsEqualTo("NOPERM");
        await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(attempts);
    }

    [Test]
    [MatrixDataSource]
    public async Task PayloadReadCancellationKeepsLaterFailureIdentityAndAttempts(
        [Matrix(false, true)] bool legacyRead, [Matrix(0, 3)] int attempts)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture(throwOnMeasurement: true);
        using var pipe = new RespBulkPayloadPipe();
        var stream = pipe.ReadStream;
        RespBulkPayloadPipe.SetErrorAttempts(stream, attempts);
        using var cancellation = new CancellationTokenSource();
        var buffer = new byte[1];
        var read = legacyRead ? stream.ReadAsync(buffer, 0, 1, cancellation.Token)
            : stream.ReadAsync(buffer.AsMemory(), cancellation.Token).AsTask();
        cancellation.Cancel();
        var cancelled = await Assert.That(async () => await read).Throws<OperationCanceledException>();
        await Assert.That(cancelled!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(read.IsCanceled).IsTrue();
        var failure = new RespireProtocolException("broken payload");
        pipe.Complete(failure);
        for (var index = 0; index < 2; index++)
        {
            var observed = await Assert.That(async () => await stream.ReadAsync(buffer)).Throws<RespireProtocolException>();
            await Assert.That(ReferenceEquals(observed, failure)).IsTrue();
        }
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(2);
        await Assert.That(items.All(item => !(bool)item.Tags["redis.client.errors.internal"]!)).IsTrue();
        await Assert.That(items.All(item => Equals(item.Tags["redis.client.operation.retry_attempts"], attempts))).IsTrue();
    }

    [Test]
    [MatrixDataSource]
    public async Task PayloadPrefixCancellationDrainsBothErrorsWithCopiedAttempts(
        [Matrix(false, true)] bool deferred, [Matrix(false, true)] bool cancelBeforePrefix,
        [Matrix(0, 3)] int attempts)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture(throwOnMeasurement: true);
        var source = new BulkStreamPendingResponseSource("GET", true, null) { ErrorAttempts = attempts };
        using var cancellation = new CancellationTokenSource();
        var owner = DispatchResponseSource<Stream?>.Start();
        owner.Observation.SetAttempts(attempts);
        var pending = owner.Attach(source.Task).AsTask();
        var scheduler = new CompletionScheduler();
        async Task Deliver(string code)
        {
            var reply = RespValue.Error(Encoding.ASCII.GetBytes(code + " private-key"));
            if (code == "WRONGTYPE") source.ObservePrefix(in reply);
            if (deferred)
            {
                scheduler.Add(source, in reply);
                scheduler.Flush();
                await scheduler.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            else { source.TrySetResult(in reply); source.ReleaseRef(); }
        }
        if (cancelBeforePrefix) source.TrySetCanceled(cancellation.Token);
        await Deliver("WRONGTYPE");
        if (!cancelBeforePrefix) source.TrySetCanceled(cancellation.Token);
        var error = await Assert.That(async () => await pending).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(pending.IsCanceled).IsTrue();
        await Deliver("NOPERM");
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(3);
        await Assert.That(items.Count(item => !(bool)item.Tags["redis.client.errors.internal"]!)).IsEqualTo(1);
        await Assert.That(items.Count(item => Equals(item.Tags.GetValueOrDefault("db.response.status_code"), "WRONGTYPE"))).IsEqualTo(1);
        await Assert.That(items.Count(item => Equals(item.Tags.GetValueOrDefault("db.response.status_code"), "NOPERM"))).IsEqualTo(1);
        await Assert.That(items.All(item => Equals(item.Tags["redis.client.operation.retry_attempts"], attempts))).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UploadSourceFailurePublishesAfterLeaseCleanup(bool laterChunk)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(8, FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var failure = new RespireProtocolException("source failure");
        using var payload = new FailingUploadStream(failure, laterChunk);
        using var capture = new Capture(throwOnMeasurement: true);
        var error = await Assert.That(async () => await client.Strings.SetAsync("key", payload, RespireConnection.StreamChunkSize + 1))
            .Throws<RespireProtocolException>();
        await Assert.That(ReferenceEquals(error, failure)).IsTrue();
        var items = capture.Items.Where(item => !(bool)item.Tags["redis.client.errors.internal"]!).ToArray();
        await Assert.That(items.Length).IsEqualTo(1);
        await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        // A subsequent upload must not receive the discarded connection or inherit its failure.
        using var next = new MemoryStream("ok"u8.ToArray());
        await Assert.That(await client.Strings.SetAsync("next", next, next.Length)).IsTrue();
    }

    private sealed class FailingUploadStream(Exception failure, bool laterChunk) : Stream
    {
        private bool _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!laterChunk || _read) return ValueTask.FromException<int>(failure);
            _read = true;
            buffer.Span.Clear();
            return new(buffer.Length);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
