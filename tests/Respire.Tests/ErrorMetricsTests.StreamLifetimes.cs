using System.Runtime.CompilerServices;
using System.Text;
using Respire.Internal;
using Respire.Networking;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public partial class ErrorMetricsTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DownloadClusterRedirectsKeepPayloadRetriesThroughCircuit(bool circuit)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var first = new FakeRespServer(8, FakeRespServer.OkReply);
        await using var second = new FakeRespServer(8, FakeRespServer.OkReply);
        await using var target = new FakeRespServer(8, FakeRespServer.OkReply);
        var slot = ClusterHash.GetSlot("key");
        var topology = Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{first.Port}\r\n");
        first.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? topology
            : command == "GET key" ? Encoding.ASCII.GetBytes($"-MOVED {slot} 127.0.0.1:{second.Port}\r\n") : null;
        second.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? topology
            : command == "GET key" ? Encoding.ASCII.GetBytes($"-ASK {slot} 127.0.0.1:{target.Port}\r\n") : null;
        target.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? topology
            : command == "GET key" ? "$100\r\npartial"u8.ToArray() : null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, UseCluster = true,
            ClusterTopologyRefreshInterval = null, Endpoints = [new("127.0.0.1", first.Port)],
            CircuitBreaker = circuit ? new() : null,
        }, deadline.Token);
        using var capture = new Capture(throwOnMeasurement: true);
        using var stream = (await client.Strings.GetStreamAsync("key", deadline.Token))!;
        await stream.ReadExactlyAsync(new byte[7], deadline.Token);
        var handled = capture.Items.Where(item => item.Tags.ContainsKey("db.response.status_code")).ToArray();
        await Assert.That(handled.Length).IsEqualTo(2);
        await Assert.That(handled[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        await Assert.That(handled[0].Tags["server.port"]).IsEqualTo(first.Port);
        await Assert.That(handled[1].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
        await Assert.That(handled[1].Tags["server.port"]).IsEqualTo(second.Port);
        await Assert.That(target.ReceivedCommands).Contains("ASKING");
        await target.DisposeAsync();
        await Assert.That(async () => await stream.ReadAsync(new byte[1], deadline.Token)).Throws<Exception>();
        var final = capture.Items.Single(item => !(bool)item.Tags["redis.client.errors.internal"]!);
        await Assert.That(final.Tags["server.port"]).IsEqualTo(target.Port);
        await Assert.That(final.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SuccessfulPayloadInspectionAllocatesNothingAfterWarmup(bool circuit)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture();
        using var payload = new RespBulkPayloadPipe();
        Stream stream = circuit
            ? new CircuitCompletionStream(payload.ReadStream, new CircuitStreamCompletion(default, default))
            : payload.ReadStream;
        payload.Complete();
        var buffer = new byte[1];
        for (var index = 0; index < 5; index++)
        {
            MeasureSuccessfulPayload(stream, buffer, false);
            MeasureSuccessfulPayload(stream, buffer, true);
        }
        var measurement = AllocationMeasurement.WithoutConcurrentGc(() =>
            (Bytes: MeasureSuccessfulPayload(stream, buffer, false), Control: MeasureSuccessfulPayload(stream, buffer, true)));
        await Assert.That(measurement.Bytes).IsEqualTo(0L);
        await Assert.That(measurement.Control).IsGreaterThanOrEqualTo(37_000L);
        await Assert.That(capture.Items).IsEmpty();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureSuccessfulPayload(Stream stream, byte[] buffer, bool control)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++)
        {
            RespBulkPayloadPipe.SetErrorAttempts(stream, 0);
            var read = stream.ReadAsync(buffer.AsMemory());
            if (!read.IsCompletedSuccessfully || read.GetAwaiter().GetResult() != 0)
                throw new InvalidOperationException("Completed payload did not return EOF.");
            if (control) GC.KeepAlive(new byte[16]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Test]
    [MatrixDataSource]
    public async Task DownloadPayloadKeepsCompletedRetriesAcrossCircuitAndObservationReuse(
        [Matrix(false, true)] bool circuit, [Matrix("array", "span", "byte", "legacy", "memory")] string read)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture(throwOnMeasurement: true);
        using var payload = new RespBulkPayloadPipe();
        Stream result = circuit
            ? new CircuitCompletionStream(payload.ReadStream, new CircuitStreamCompletion(default, default))
            : payload.ReadStream;
        var observation = RespireTelemetry.ErrorObservation.Rent(force: true);
        observation.Handled(new RespireConnectionRetiredException("first", 6379));
        // A nested route retry contributes to the same header result, not a new caller.
        observation.Handled(new RespireConnectionRetiredException("second", 6379));
        var stream = (await RespireTelemetry.ObserveStreamError(new(result), observation))!;
        using (var reused = RespireTelemetry.ErrorObservation.Rent(force: true))
            for (var attempt = 0; attempt < 5; attempt++) reused.Retry();
        var error = new IOException("Original producer failure.");
        payload.Complete(error);
        await Assert.That(capture.Items.Count).IsEqualTo(2);
        await Assert.That(await ReadFailure(stream, read)).IsSameReferenceAs(error);
        await Assert.That(await ReadFailure(stream, read)).IsSameReferenceAs(error);
        var final = capture.Items.Single(item => !(bool)item.Tags["redis.client.errors.internal"]!);
        await Assert.That(final.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DownloadReadCancellationAndProducerFailureKeepIndependentCompletion(bool legacy)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture(throwOnMeasurement: true);
        using var payload = new RespBulkPayloadPipe();
        var stream = payload.ReadStream;
        RespBulkPayloadPipe.SetErrorAttempts(stream, 3);
        var buffer = new byte[1];
        for (var index = 0; index < 2; index++)
        {
            using var cancellation = new CancellationTokenSource();
            var read = legacy ? stream.ReadAsync(buffer, 0, 1, cancellation.Token)
                : stream.ReadAsync(buffer.AsMemory(), cancellation.Token).AsTask();
            await cancellation.CancelAsync();
            OperationCanceledException? failure = null;
            try { await read; }
            catch (OperationCanceledException error) { failure = error; }
            await Assert.That(failure).IsNotNull();
            await Assert.That(failure!.CancellationToken).IsEqualTo(cancellation.Token);
            await Assert.That(read.IsCanceled).IsTrue();
        }
        payload.GetMemory(1).Span[0] = 42;
        payload.Advance(1);
        await payload.FlushAsync();
        await Assert.That(await stream.ReadAsync(buffer)).IsEqualTo(1);
        await Assert.That(buffer[0]).IsEqualTo((byte)42);
        var producerError = new IOException("Later producer failure.");
        var pending = stream.ReadAsync(buffer).AsTask();
        payload.Complete(producerError);
        Exception? observed = null;
        try { await pending; }
        catch (Exception error) { observed = error; }
        await Assert.That(observed).IsSameReferenceAs(producerError);
        await Assert.That(await ReadFailure(stream, "memory")).IsSameReferenceAs(producerError);
        var final = capture.Items.ToArray();
        await Assert.That(final.Length).IsEqualTo(3);
        await Assert.That(final.All(item => !(bool)item.Tags["redis.client.errors.internal"]!)).IsTrue();
        await Assert.That(final.All(item => Equals(item.Tags["redis.client.operation.retry_attempts"], 3))).IsTrue();
    }

    [Test]
    [MatrixDataSource]
    public async Task UploadProducerFailurePreservesIdentityAfterCleanup(
        [Matrix(0, RespireConnection.StreamChunkSize)] int prefixLength, [Matrix(false, true)] bool cancelled)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "PING" ? FakeRespServer.PongReply : null,
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Exception expected = cancelled ? new OperationCanceledException(cancellation.Token)
            : new IOException("Original upload producer failure.");
        using var source = new FailingUploadSource(prefixLength, expected);
        var borrowedAtPublication = -1;
        using var capture = new Capture(throwOnMeasurement: true, onMeasurement: item =>
        {
            if (!(bool)item.Tags["redis.client.errors.internal"]!)
                borrowedAtPublication = client.Core.DedicatedPool.CaptureRetirementState().Borrowed;
        });
        Exception? observed = null;
        try { await client.Strings.SetAsync("key", source, RespireConnection.StreamChunkSize + 1); }
        catch (Exception error) { observed = error; }
        await Assert.That(observed).IsSameReferenceAs(expected);
        if (observed is OperationCanceledException cancelledError)
            await Assert.That(cancelledError.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(source.Disposed).IsFalse();
        var final = capture.Items.Where(item => !(bool)item.Tags["redis.client.errors.internal"]!).ToArray();
        await Assert.That(final.Length).IsEqualTo(1);
        await Assert.That(borrowedAtPublication).IsEqualTo(0);
        await Assert.That(final[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        // Dedicated upload cleanup leaves multiplexed commands usable.
        _ = await client.PingAsync();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UploadReplayResetRunsAfterLeaseReturnAndKeepsServerFailure(bool resetFails)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var first = new FakeRespServer(8, FakeRespServer.OkReply);
        await using var target = new FakeRespServer(8, FakeRespServer.OkReply);
        var slot = ClusterHash.GetSlot("key");
        var topology = Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{first.Port}\r\n");
        first.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? topology
            : command.StartsWith("SET key ", StringComparison.Ordinal)
                ? Encoding.ASCII.GetBytes($"-MOVED {slot} 127.0.0.1:{target.Port}\r\n") : null;
        target.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? topology : null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, UseCluster = true,
            ClusterTopologyRefreshInterval = null, Endpoints = [new("127.0.0.1", first.Port)],
        }, deadline.Token);
        var pool = await client.Core.Cluster!.GetDedicatedPoolAsync(slot, deadline.Token, discovery: null, observation: default);
        var borrowedAtReset = -1;
        using var source = new ReplayUploadSource(() => borrowedAtReset = pool.CaptureRetirementState().Borrowed, resetFails);
        using var capture = new Capture(throwOnMeasurement: true);
        if (resetFails)
        {
            var error = await Assert.That(async () => await client.Strings.SetAsync("key", source, 4,
                cancellationToken: deadline.Token)).Throws<RespireServerException>();
            await Assert.That(error!.Code).IsEqualTo("MOVED");
        }
        else await Assert.That(await client.Strings.SetAsync("key", source, 4, cancellationToken: deadline.Token)).IsTrue();
        await Assert.That(borrowedAtReset).IsEqualTo(0);
        var handled = capture.Items.Where(item => (bool)item.Tags["redis.client.errors.internal"]!).ToArray();
        await Assert.That(handled.Length).IsEqualTo(1);
        await Assert.That(handled[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        var final = capture.Items.Where(item => !(bool)item.Tags["redis.client.errors.internal"]!).ToArray();
        await Assert.That(final.Length).IsEqualTo(resetFails ? 1 : 0);
        if (resetFails) await Assert.That(final[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
        await Assert.That(target.ReceivedCommands.Count(command => command.StartsWith("SET key ", StringComparison.Ordinal)))
            .IsEqualTo(resetFails ? 0 : 1);
    }

    private sealed class ReplayUploadSource(Action resetting, bool resetFails) : MemoryStream("data"u8.ToArray())
    {
        public override long Position
        {
            get => base.Position;
            set
            {
                resetting();
                if (resetFails) throw new IOException("Replay reset failed.");
                base.Position = value;
            }
        }
    }

    private static async Task<Exception?> ReadFailure(Stream stream, string read)
    {
        var buffer = new byte[1];
        try
        {
            switch (read)
            {
                case "array": stream.Read(buffer, 0, 1); break;
                case "span": stream.Read(buffer.AsSpan()); break;
                case "byte": stream.ReadByte(); break;
                case "legacy": await stream.ReadAsync(buffer, 0, 1); break;
                default: await stream.ReadAsync(buffer.AsMemory()); break;
            }
        }
        catch (Exception error) { return error; }
        return null;
    }

    private sealed class FailingUploadSource(int prefixLength, Exception error) : Stream
    {
        private int _remaining = prefixLength;
        internal bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining == 0) return ValueTask.FromException<int>(error);
            var count = Math.Min(buffer.Length, _remaining);
            buffer.Span[..count].Clear();
            _remaining -= count;
            return new(count);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
