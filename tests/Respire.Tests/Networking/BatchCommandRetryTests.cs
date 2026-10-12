using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net.Sockets;
using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

[NotInParallel]
public class BatchCommandRetryTests
{
    private static readonly byte[] TryAgain = "-TRYAGAIN busy\r\n"u8.ToArray();

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SafeBatchReplaysEveryMemberAndPublishesOnlyFinalReplies(bool circuits)
    {
        var reads = 0;
        await using var server = Server(command => command switch
        {
            "GET first" => Encoding.ASCII.GetBytes($"${(reads == 0 ? 3 : 5)}\r\n{(reads == 0 ? "old" : "final")}\r\n"),
            "GET second" => Interlocked.Increment(ref reads) == 1 ? TryAgain : "$5\r\nfinal\r\n"u8.ToArray(),
            _ => null,
        });
        await using var client = await RespireClient.ConnectAsync(Options(server) with { CircuitBreaker = circuits ? new() : null });
        using var batch = client.CreateBatch();
        var first = batch.GetString("first");
        var second = batch.GetString("second");
        await batch.ExecuteAsync();
        await Assert.That(first.Result).IsEqualTo("final");
        await Assert.That(second.Result).IsEqualTo("final");
        await Assert.That(server.ReceivedCommands.Where(command => command.StartsWith("GET ")).ToArray())
            .IsEquivalentTo(new[] { "GET first", "GET second", "GET first", "GET second" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        // Exercise returned native response sources after discarded attempt replies.
        await Assert.That(await client.GetStringAsync("first")).IsEqualTo("final");
    }

    [Test]
    [Arguments(RespireCommandRetryCategory.ReadOnly, 1)]
    [Arguments(RespireCommandRetryCategory.WriteAccumulating, 2)]
    public async Task MixedBatchRequiresPermissionForSuccessfulAccumulatingMember(RespireCommandRetryCategory maximum, int effects)
    {
        var increments = 0;
        var reads = 0;
        await using var server = Server(command => command switch
        {
            "INCR counter" => Encoding.ASCII.GetBytes($":{Interlocked.Increment(ref increments)}\r\n"),
            "GET key" => Interlocked.Increment(ref reads) == 1 ? TryAgain : "$5\r\nvalue\r\n"u8.ToArray(),
            _ => null,
        });
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        { RetryPolicy = new() { InitialBackoff = TimeSpan.Zero, MaxCommandRetryCategory = maximum } });
        using var batch = client.CreateBatch();
        var increment = batch.Increment("counter");
        var read = batch.GetString("key");
        var result = await batch.TryExecuteAsync();
        await Assert.That(increments).IsEqualTo(effects);
        await Assert.That(increment.Result).IsEqualTo((long)effects);
        await Assert.That(read.HasResult).IsEqualTo(effects == 2);
        await Assert.That(result.Failures.Count).IsEqualTo(effects == 2 ? 0 : 1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LostRepliesReplayOnlySafeCompleteUnits(bool unsafeMember)
    {
        var reads = 0;
        await using var server = Server(_ => null);
        server.ReplyOverride = (_, command) =>
        {
            if (command == "GET first") return "$3\r\nold\r\n"u8.ToArray();
            if (command != "GET second") return command == "INCR counter" ? ":1\r\n"u8.ToArray() : null;
            if (Interlocked.Increment(ref reads) == 1) { server.CloseConnections(); return null; }
            return "$5\r\nfinal\r\n"u8.ToArray();
        };
        await using var client = await RespireClient.ConnectAsync(Options(server));
        using var batch = client.CreateBatch();
        var first = batch.GetString("first");
        if (unsafeMember) _ = batch.Increment("counter");
        var second = batch.GetString("second");
        var result = await batch.TryExecuteAsync();
        await Assert.That(reads).IsEqualTo(unsafeMember ? 1 : 2);
        await Assert.That(result.FailureCount == 0).IsEqualTo(!unsafeMember);
        if (!unsafeMember)
        {
            await Assert.That(first.Result).IsEqualTo("old");
            await Assert.That(second.Result).IsEqualTo("final");
        }
        await Assert.That(server.ReceivedCommands.Count(command => command == "GET first")).IsEqualTo(unsafeMember ? 1 : 2);
    }

    [Test]
    public async Task AcquisitionFailureBeforeAnySendAllowsMixedUnitUnderReadOnlyPolicy()
    {
        var dials = 0;
        await using var server = Server(command => command == "INCR counter" ? ":1\r\n"u8.ToArray() : "$5\r\nvalue\r\n"u8.ToArray());
        await using var client = RespireClient.Create(Options(server) with
        {
            TestingStreamFactory = async (host, port, token) =>
            {
                if (Interlocked.Increment(ref dials) == 1) throw new RespireConnectionException("controlled acquisition failure");
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(host, port, token);
                return new NetworkStream(socket, ownsSocket: true);
            },
        });
        using var batch = client.CreateBatch();
        var increment = batch.Increment("counter");
        var read = batch.GetString("key");
        await batch.ExecuteAsync();
        await Assert.That(increment.Result).IsEqualTo(1L);
        await Assert.That(read.Result).IsEqualTo("value");
        await Assert.That(server.ReceivedCommands.Count(command => command == "INCR counter")).IsEqualTo(1);
        await Assert.That(dials).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PartialTransportWriteNeverGrantsUnsafeReplay(bool unsafeMember)
    {
        PartialWriteStream? transport = null;
        await using var server = Server(command => command == "INCR counter" ? ":1\r\n"u8.ToArray() : "$5\r\nvalue\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            TestingStreamFactory = async (host, port, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(host, port, token);
                return transport = new PartialWriteStream(new NetworkStream(socket, ownsSocket: true));
            },
        });
        transport!.FailNextWrite = true;
        using var batch = client.CreateBatch();
        var read = batch.GetString("key");
        if (unsafeMember) _ = batch.Increment("counter");
        var result = await batch.TryExecuteAsync();
        await Assert.That(result.FailureCount == 0).IsEqualTo(!unsafeMember);
        if (!unsafeMember) await Assert.That(read.Result).IsEqualTo("value");
        await Assert.That(server.ReceivedCommands.Count(command => command == "INCR counter")).IsEqualTo(0);
    }

    [Test]
    public async Task SnapshotAndMutationFenceSpanBackoffAndPublication()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = HandledListener(handled);
        var payload = "old"u8.ToArray();
        var reads = 0;
        var writes = 0;
        var releaseRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = Server(_ => null);
        server.ReplyOverride = (_, command) => command switch
        {
            "CLIENT ID" => ":1\r\n"u8.ToArray(),
            "SET key old" => GateRetry(),
            "GET key" => Interlocked.Increment(ref reads) == 1 ? TryAgain : "$5\r\nfinal\r\n"u8.ToArray(),
            _ when command.StartsWith("HELLO") => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
            _ => null,
        };
        byte[] GateRetry()
        {
            if (Interlocked.Increment(ref writes) == 1) server.GateCommand("SET key old", releaseRetry.Task);
            return FakeRespServer.OkReply;
        }
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            Protocol = RespProtocol.Resp3, ClientSideCache = new(),
            RetryPolicy = new() { InitialBackoff = TimeSpan.Zero,
                MaxCommandRetryCategory = RespireCommandRetryCategory.WriteLastWins },
        });
        using var batch = client.CreateBatch();
        var write = batch.Set("key", payload);
        var read = batch.GetString("key");
        var execution = batch.ExecuteAsync().AsTask();
        await handled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(write.Status).IsEqualTo(RespirePendingStatus.Pending);
        await Assert.That(read.Status).IsEqualTo(RespirePendingStatus.Pending);
        await Assert.That(client.Core.ClientCache!.InspectForTests().ActiveMutationCount).IsEqualTo(1);
        "new"u8.CopyTo(payload);
        releaseRetry.TrySetResult();
        await execution;
        await Assert.That(write.Result).IsTrue();
        await Assert.That(read.Result).IsEqualTo("final");
        await Assert.That(server.ReceivedCommands.Count(command => command == "SET key old")).IsEqualTo(2);
        await Assert.That(client.Core.ClientCache!.InspectForTests().ActiveMutationCount).IsEqualTo(0);
    }

    [Test]
    [Arguments("before")]
    [Arguments("reply")]
    [Arguments("backoff")]
    public async Task CallerCancellationStopsWholeUnitAndPreservesToken(string phase)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var caller = new CancellationTokenSource();
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = HandledListener(signal);
        await using var server = Server(_ => null);
        server.ReplyOverride = (_, command) => command.StartsWith("GET ") ? TryAgain : null;
        if (phase == "reply") server.SuppressReply = command =>
        {
            if (command == "GET second") signal.TrySetResult();
            return command.StartsWith("GET ");
        };
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        { RetryPolicy = new() { InitialBackoff = TimeSpan.FromSeconds(1), Jitter = 0 } });
        using var batch = client.CreateBatch();
        var first = batch.GetString("first");
        var second = batch.GetString("second");
        if (phase == "before") caller.Cancel();
        var execution = batch.TryExecuteAsync(caller.Token).AsTask();
        if (phase != "before") { await signal.Task.WaitAsync(TimeSpan.FromSeconds(5)); caller.Cancel(); }
        var result = await execution;
        await Assert.That(result.FailureCount).IsEqualTo(2);
        await Assert.That(first.Error).IsTypeOf<OperationCanceledException>();
        await Assert.That(second.Error).IsTypeOf<OperationCanceledException>();
        await Assert.That(((OperationCanceledException)first.Error!).CancellationToken).IsEqualTo(caller.Token);
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("GET "))).IsEqualTo(phase == "before" ? 0 : 2);
    }

    [Test]
    public async Task OneDeadlineIncludesFirstAttemptAndLateRetryReplies()
    {
        var reads = 0;
        await using var server = Server(command => command == "GET key"
            ? Interlocked.Increment(ref reads) == 1 ? TryAgain : "$5\r\nvalue\r\n"u8.ToArray() : null);
        server.DelayCommand("GET key", 150);
        await using var client = await RespireClient.ConnectAsync(Options(server) with { CommandTimeout = TimeSpan.FromMilliseconds(250) });
        using var batch = client.CreateBatch();
        var read = batch.GetString("key");
        var result = await batch.TryExecuteAsync();
        await Assert.That(result.FailureCount).IsEqualTo(1);
        await Assert.That(read.Error).IsTypeOf<RespireTimeoutException>();
        using var barrier = await client.ExecuteAsync(RespireCommands.Connection.PING);
        await Assert.That(barrier.AsString()).IsEqualTo("OK");
        await Assert.That(reads).IsEqualTo(2);
    }

    [Test]
    public async Task ExhaustionReportsOncePerUnitAcrossReusedResponseSources()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        var events = new ConcurrentDictionary<string, long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = static (instrument, meter) =>
        { if (instrument.Name == "respire.command.retries") meter.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "respire.command.retry.outcome") events.AddOrUpdate((string)tag.Value!, value, (_, count) => count + value);
        });
        listener.Start();
        await using var server = Server(command => command.StartsWith("GET ") ? TryAgain : null);
        await using var client = await RespireClient.ConnectAsync(Options(server) with { RetryPolicy = new() { MaxAttempts = 2, InitialBackoff = TimeSpan.Zero } });
        for (var iteration = 0; iteration < 32; iteration++)
        {
            using var batch = client.CreateBatch();
            var first = batch.GetString("first");
            var second = batch.GetString("second");
            var result = await batch.TryExecuteAsync();
            await Assert.That(result.FailureCount).IsEqualTo(2);
            await Assert.That(first.Error).IsTypeOf<RespireServerException>();
            await Assert.That(second.Error).IsTypeOf<RespireServerException>();
        }
        await Assert.That(events["attempted"]).IsEqualTo(32L);
        await Assert.That(events["exhausted"]).IsEqualTo(32L);
        await Assert.That(events.ContainsKey("succeeded")).IsFalse();
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("GET "))).IsEqualTo(128);
        var reads = 0;
        server.ReplyOverride = (_, command) => command.StartsWith("GET ")
            ? Interlocked.Increment(ref reads) <= 2 ? TryAgain : "$5\r\nvalue\r\n"u8.ToArray() : null;
        using var success = client.CreateBatch();
        var successfulFirst = success.GetString("first");
        var successfulSecond = success.GetString("second");
        await success.ExecuteAsync();
        await Assert.That(successfulFirst.Result).IsEqualTo("value");
        await Assert.That(successfulSecond.Result).IsEqualTo("value");
        await Assert.That(events["attempted"]).IsEqualTo(33L);
        await Assert.That(events["exhausted"]).IsEqualTo(32L);
        await Assert.That(events["succeeded"]).IsEqualTo(1L);
    }

    [Test]
    public async Task ServerCursorContinuationExcludesWholeBatchFromRetry()
    {
        await using var server = Server(command => command.StartsWith("HSCAN ") ? TryAgain : "$5\r\nvalue\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(Options(server));
        using var batch = client.CreateBatch();
        _ = batch.Hashes.ScanFieldsPage("hash", cursor: 7);
        var read = batch.GetString("key");
        var result = await batch.TryExecuteAsync();
        await Assert.That(result.FailureCount).IsEqualTo(1);
        await Assert.That(read.Result).IsEqualTo("value");
        await Assert.That(server.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
    }

    [Test]
    [Arguments("LOADING", true)]
    [Arguments("TRYAGAIN", true)]
    [Arguments("MASTERDOWN", true)]
    [Arguments("READONLY", true)]
    [Arguments("BUSY", false)]
    public async Task BatchHonorsSelectedTransientFailure(string code, bool retries)
    {
        var reads = 0;
        await using var server = Server(command => command == "GET key"
            ? Interlocked.Increment(ref reads) == 1 ? Encoding.ASCII.GetBytes($"-{code} busy\r\n") : "$5\r\nvalue\r\n"u8.ToArray() : null);
        await using var client = await RespireClient.ConnectAsync(Options(server));
        using var batch = client.CreateBatch();
        var read = batch.GetString("key");
        var result = await batch.TryExecuteAsync();
        await Assert.That(reads).IsEqualTo(retries ? 2 : 1);
        await Assert.That(result.FailureCount).IsEqualTo(retries ? 0 : 1);
        await Assert.That(read.HasResult).IsEqualTo(retries);
    }

    [Test]
    [Arguments("absent")]
    [Arguments("one-attempt")]
    [Arguments("unselected")]
    [Arguments("never")]
    public async Task DisabledOrUnselectedPolicyKeepsOneSend(string mode)
    {
        await using var server = Server(command => command == "GET key" ? TryAgain : null);
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            RetryPolicy = mode switch
            {
                "absent" => null,
                "one-attempt" => new() { MaxAttempts = 1 },
                "unselected" => new() { RetryableErrors = RespireRetryableErrors.Loading },
                _ => new() { MaxCommandRetryCategory = RespireCommandRetryCategory.Never },
            },
        });
        using var batch = client.CreateBatch();
        _ = batch.GetString("key");
        await Assert.That((await batch.TryExecuteAsync()).FailureCount).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
    }

    [Test]
    public async Task HashImportSessionDoesNotReplayPinnedMembers()
    {
        await using var server = Server(command => command.StartsWith("HIMPORT PREPARE ") ? TryAgain : null);
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        { RetryPolicy = new() { InitialBackoff = TimeSpan.Zero, MaxCommandRetryCategory = RespireCommandRetryCategory.ServerAdmin } });
        await using var session = await client.Hashes.CreateImportSessionAsync();
        using var batch = session.CreateBatch();
        var prepare = batch.Hashes.PrepareImport("schema", "field");
        var result = await batch.TryExecuteAsync();
        await Assert.That(result.FailureCount).IsEqualTo(1);
        await Assert.That(prepare.Error).IsTypeOf<RespireServerException>();
        await Assert.That(server.ReceivedCommands.Count(command => command == "HIMPORT PREPARE schema field")).IsEqualTo(1);
    }

    [Test]
    public async Task CanceledAttemptRetainsNativeFenceAcrossSnapshotAndSourceReuse()
    {
        var writeSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = Server(command => command.StartsWith("HELLO") ? "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray() : null);
        server.SuppressReply = command =>
        {
            if (command == "SET key first") { writeSeen.TrySetResult(); return true; }
            if (command == "GET second") { readSeen.TrySetResult(); return true; }
            return false;
        };
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        { Protocol = RespProtocol.Resp3, ClientSideCache = new() });
        using var caller = new CancellationTokenSource();
        using var firstBatch = client.CreateBatch();
        var write = firstBatch.Set("key", "first");
        var first = firstBatch.TryExecuteAsync(caller.Token).AsTask();
        await writeSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();
        await first;
        await Assert.That(write.Error).IsTypeOf<OperationCanceledException>();
        await Assert.That(client.Core.ClientCache!.InspectForTests().ActiveMutationCount).IsEqualTo(1);
        using var secondBatch = client.CreateBatch();
        var read = secondBatch.GetString("second");
        var second = secondBatch.ExecuteAsync().AsTask();
        await readSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await server.SendRawAsync("+OK\r\n$5\r\nvalue\r\n"u8.ToArray(), server.ReceivedConnectionIds[^1]);
        await second;
        await Assert.That(read.Result).IsEqualTo("value");
        await Assert.That(client.Core.ClientCache!.InspectForTests().ActiveMutationCount).IsEqualTo(0);
        await Assert.That(server.ReceivedCommands.Count(command => command == "SET key first")).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConversionRunsOnlyAfterFinalRetryDecision(bool transientFailure)
    {
        var rounds = 0;
        await using var server = Server(command => command switch
        {
            "GET number" => rounds == 0 ? "$3\r\nbad\r\n"u8.ToArray() : "$2\r\n42\r\n"u8.ToArray(),
            "GET key" => Interlocked.Increment(ref rounds) == 1 && transientFailure ? TryAgain : "$5\r\nvalue\r\n"u8.ToArray(),
            _ => null,
        });
        await using var client = await RespireClient.ConnectAsync(Options(server));
        using var batch = client.CreateBatch();
        var number = batch.Get<int>("number");
        var read = batch.GetString("key");
        var result = await batch.TryExecuteAsync();
        await Assert.That(rounds).IsEqualTo(transientFailure ? 2 : 1);
        await Assert.That(result.FailureCount).IsEqualTo(transientFailure ? 0 : 1);
        await Assert.That(read.Result).IsEqualTo("value");
        if (transientFailure) await Assert.That(number.Result).IsEqualTo(42);
        else await Assert.That(number.Error is not null).IsTrue();
    }

    [Test]
    public async Task NeverMemberPreventsWholeUnitReplay()
    {
        await using var server = Server(command => command == "GET first" ? TryAgain : "$5\r\nvalue\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        { RetryPolicy = new() { InitialBackoff = TimeSpan.Zero, MaxCommandRetryCategory = RespireCommandRetryCategory.ServerAdmin } });
        using var batch = client.CreateBatch();
        _ = batch.GetString("first");
        var second = batch.Execute(RespireCommands.String.GET.WithRetryCategory(RespireCommandRetryCategory.Never), "second");
        await Assert.That((await batch.TryExecuteAsync()).FailureCount).IsEqualTo(1);
        using var value = second.Result;
        await Assert.That(value.AsString()).IsEqualTo("value");
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("GET "))).IsEqualTo(2);
    }

    [Test]
    public async Task SuccessfulMemberCarriesUnitRetryCountOnFinalFailure()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        var finalAttempts = new ConcurrentQueue<int>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = static (instrument, meter) =>
        { if (instrument.Name == "redis.client.errors") meter.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var internalError = false;
            var attempts = 0;
            foreach (var tag in tags)
            {
                if (tag.Key == "redis.client.errors.internal") internalError = Equals(tag.Value, true);
                if (tag.Key == "redis.client.operation.retry_attempts") attempts = (int)tag.Value!;
            }
            if (!internalError) finalAttempts.Enqueue(attempts);
        });
        listener.Start();
        var rounds = 0;
        await using var server = Server(command => command switch
        {
            "GET first" => rounds == 0 ? "$5\r\nvalue\r\n"u8.ToArray() : "-BUSY permanent\r\n"u8.ToArray(),
            "GET second" => Interlocked.Increment(ref rounds) == 1 ? TryAgain : "$5\r\nvalue\r\n"u8.ToArray(),
            _ => null,
        });
        await using var client = await RespireClient.ConnectAsync(Options(server));
        using var batch = client.CreateBatch();
        var first = batch.GetString("first");
        var second = batch.GetString("second");
        var result = await batch.TryExecuteAsync();
        await Assert.That(result.FailureCount).IsEqualTo(1);
        await Assert.That(first.Error).IsTypeOf<RespireServerException>();
        await Assert.That(second.Result).IsEqualTo("value");
        await Assert.That(finalAttempts.ToArray()).IsEquivalentTo(new[] { 1 });
        await Assert.That(result.FirstError).IsEqualTo(first.Error);
        await Assert.That(finalAttempts.Count).IsEqualTo(1);
    }

    private static MeterListener HandledListener(TaskCompletionSource signal)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = static (instrument, meter) =>
        { if (instrument.Name == "redis.client.errors") meter.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "redis.client.errors.internal" && Equals(tag.Value, true)) signal.TrySetResult();
        });
        listener.Start();
        return listener;
    }

    private sealed class PartialWriteStream(Stream inner) : Stream
    {
        internal bool FailNextWrite;
        public override bool CanRead => inner.CanRead;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => inner.ReadAsync(buffer, token);
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            if (FailNextWrite)
            {
                FailNextWrite = false;
                await inner.WriteAsync(buffer[..Math.Max(1, buffer.Length / 2)], token);
                throw new IOException("controlled partial write failure");
            }
            await inner.WriteAsync(buffer, token);
        }
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    private static FakeRespServer Server(Func<string, byte[]?> reply) => new(32, FakeRespServer.OkReply)
    { ReplyOverride = (_, command) => command == "CLIENT ID" ? ":1\r\n"u8.ToArray() : reply(command) };

    private static RespireOptions Options(FakeRespServer server) => new()
    {
        Endpoints = [new("127.0.0.1", server.Port)], Protocol = RespProtocol.Resp2,
        Connections = 1, ThreadPoolMonitoring = false, CommandTimeout = TimeSpan.FromSeconds(3),
        RetryPolicy = new() { InitialBackoff = TimeSpan.Zero },
    };
}
