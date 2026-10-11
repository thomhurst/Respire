using System.Buffers;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Infrastructure;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class StandaloneBatchFlushTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OrderedAdmissionReroutesOnlyWhenConnectionIsNotPinned(bool pinned)
    {
        await using var server = new FakeRespServer(2, FakeRespServer.PongReply);
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", server.Port,
            connectionCount: 2, options: new RespireConnectionOptions { Protocol = RespProtocol.Resp2 });
        var retired = multiplexer.GetConnection();
        await retired.RetireAsync();
        if (pinned)
        {
            await Assert.That(async () => await retired.EnqueuePinnedAsync(new RawCommand(FakeRespServer.PingFrame),
                default, "PING", pinToConnection: true)).Throws<RespireConnectionRetiredException>();
            await Assert.That(server.CommandsSeen).IsEqualTo(0);
        }
        else
        {
            var reply = await retired.EnqueuePinnedAsync(new RawCommand(FakeRespServer.PingFrame),
                default, "PING", pinToConnection: false);
            using var value = await reply;
            await Assert.That(value.AsString()).IsEqualTo("PONG");
            await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[] { "PING" }, CollectionOrdering.Matching);
        }
    }

    private const string AllocationProbeMode = "RESPIRE_BATCH_START_ALLOCATION_PROBE";

    [Test, ParallelLimiter<ChildProcessProbes>]
    public async Task StartingOneHundredOperationsDoesNotAllocateOneTaskPerOperation()
    {
        var start = AsyncFlushSignalTests.CreateProbeStartInfo(Environment.ProcessPath,
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"), typeof(StandaloneBatchFlushTests).Assembly.Location);
        start.Environment[AllocationProbeMode] = Environment.Version.ToString();
        // Tier-0 and instrumented code box each struct command three times when MutationCommand
        // forwards to IRespCommand default interface methods (408 bytes per INCR). Optimized code
        // does not. Promotion timing depends on unrelated JIT activity in the test process, so
        // measure fully optimized steady state from the first call.
        start.Environment["DOTNET_TieredCompilation"] = "0";
        await AsyncFlushSignalTests.RunProbeAsync(start, TimeSpan.FromSeconds(30));
    }

    internal static int? RunIsolatedAllocationProbe()
    {
        var runtime = Environment.GetEnvironmentVariable(AllocationProbeMode);
        if (runtime is null) return null;
        try
        {
            if (runtime != Environment.Version.ToString()) throw new InvalidOperationException("Probe runtime differs from parent.");
            // A pool thread lets the first command run the flush loop inline, as in production.
            Task.Run(MeasureBatchStartAsync).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static async Task MeasureBatchStartAsync()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        for (var warmup = 0; warmup < 32; warmup++)
        {
            using var warm = CreateBatch(client, 100);
            _ = MeasureStart(warm, warmup % 2 == 0, out var execution);
            await execution.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }

        server.SuppressReply = _ => true;
        var replyBytes = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(":1\r\n", 100)));
        var bytes = await MeasureAndCompleteAsync(server, client, replyBytes, allocateControl: false);
        var controlBytes = await MeasureAndCompleteAsync(server, client, replyBytes, allocateControl: true);
        Console.WriteLine($"Standalone batch start: {bytes} bytes; one Task per operation control: {controlBytes} bytes.");
        // Includes the execution state machine; excludes queued operations and response-thread work.
        // The control adds one real Task per operation, so the limit must catch that regression.
        if (bytes >= MaxStartBytes || controlBytes < MaxStartBytes || controlBytes - bytes < 100 * MinimumTaskBytes)
            throw new InvalidOperationException(
                $"Expected under {MaxStartBytes} bytes to start 100 operations and a control of at least {MaxStartBytes} bytes; "
                + $"observed {bytes} and {controlBytes} bytes.");
    }

    private static async Task<long> MeasureAndCompleteAsync(FakeRespServer server, RespireClient client, byte[] replyBytes,
        bool allocateControl)
    {
        using var batch = CreateBatch(client, 100);
        var expectedCommands = server.CommandsSeen + 100;
        ValueTask<RespireBatchResult> pending = default;
        var bytes = AllocationMeasurement.WithoutConcurrentGc(() => MeasureStart(batch, allocateControl, out pending));
        await WaitForCommandsAsync(server, expectedCommands).WaitAsync(TimeSpan.FromSeconds(5));
        await server.SendRawAsync(replyBytes);
        await pending.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        return bytes;
    }

    // 100 operations x one Task (at least 64 bytes on x64) clearly exceed this limit.
    private const long MaxStartBytes = 4_096;
    private const long MinimumTaskBytes = 64;
    private static readonly Task<long>?[] s_controlTasks = new Task<long>?[100];
    private static readonly Func<long> s_controlTaskBody = static () => 0;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureStart(RespireBatch batch, bool allocateControl, out ValueTask<RespireBatchResult> pending)
    {
        PrimeStartingThread();
        var before = GC.GetAllocatedBytesForCurrentThread();
        pending = batch.TryExecuteAsync();
        if (allocateControl)
        {
            for (var index = 0; index < s_controlTasks.Length; index++)
                s_controlTasks[index] = new Task<long>(s_controlTaskBody);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static readonly FieldInfo s_serializeScratch = typeof(RespireConnection)
        .GetField("_serializeScratch", BindingFlags.Static | BindingFlags.NonPublic)!;

    // Earlier batches start on other pool threads, so this thread's caches are not warm:
    // - RunStandaloneBatchAsync rents two scratch arrays (10,288 bytes on x64) here and
    //   returns them on the completing thread. ArrayPool.Shared serves this thread's slot
    //   first, then shared per-core stacks; gen-2 GCs, including the no-GC region's, can
    //   trim both under memory pressure.
    // - Command serialization uses a thread-static 4 KiB scratch buffer, created on first use.
    // Both are one-time per-thread costs, not per-operation work.
    private static void PrimeStartingThread()
    {
        ArrayPool<ValueTask<RespValue>>.Shared.Return(ArrayPool<ValueTask<RespValue>>.Shared.Rent(100), clearArray: true);
        ArrayPool<RespireTelemetry.ErrorObservation>.Shared.Return(
            ArrayPool<RespireTelemetry.ErrorObservation>.Shared.Rent(100), clearArray: true);
        if (s_serializeScratch.GetValue(null) is null) s_serializeScratch.SetValue(null, new WriteBuffer(4 * 1024));
    }

    private static RespireBatch CreateBatch(RespireClient client, int count)
    {
        var batch = client.CreateBatch();
        for (var index = 0; index < count; index++) batch.Increment($"allocation:{index}");
        return batch;
    }

    [Test]
    [Arguments(1)]
    [Arguments(32)]
    [Arguments(100)]
    public async Task IdleBatchUsesOneTransportWrite(int count)
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray());
        CountingStream? transport = null;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1,
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            TestingStreamFactory = async (host, port, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(host, port, token);
                    return transport = new CountingStream(new NetworkStream(socket, ownsSocket: true));
                }
                catch { socket.Dispose(); throw; }
            },
        });
        using var batch = client.CreateBatch();
        var pending = new RespirePending<long>[count];
        for (var index = 0; index < count; index++) pending[index] = batch.Increment($"counter:{index}");

        // Explicit pool dispatch exercises the inline flush wake-up that split idle batches.
        await Task.Run(async () => await batch.ExecuteAsync());
        await transport!.Written.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(transport.WriteCount).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(
            Enumerable.Range(0, count).Select(index => $"INCR counter:{index}"), CollectionOrdering.Matching);
        foreach (var result in pending) await Assert.That(result.Result).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BatchLargerThanRingPreservesQueueOrder(bool circuitEnabled)
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, MaxInflightCommands = 2,
            CircuitBreaker = circuitEnabled ? new() : null,
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
        });
        using var batch = client.CreateBatch();
        for (var index = 0; index < 100; index++) _ = batch.Increment($"counter:{index}");
        await batch.ExecuteAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(
            Enumerable.Range(0, 100).Select(index => $"INCR counter:{index}"), CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task StalledBoundedRingBatchRetainsOriginalTimeout(bool durability, bool circuitEnabled)
    {
        await using var server = new FakeRespServer(2, ":1\r\n"u8.ToArray())
        {
            SuppressReply = command => command.StartsWith("INCR ", StringComparison.Ordinal),
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, MaxInflightCommands = 2,
            CommandTimeout = TimeSpan.FromMilliseconds(500),
            CircuitBreaker = circuitEnabled ? new() { MinimumFailureCount = 101 } : null,
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
        });
        using var batch = client.CreateBatch();
        var pending = new RespirePending<long>[100];
        for (var index = 0; index < pending.Length; index++) pending[index] = batch.Increment($"counter:{index}");

        // A restarted timeout for each of the 98 capacity waiters takes about 49 seconds.
        Task execution = durability
            ? batch.ExecuteAndWaitForReplicationAsync(1, TimeSpan.Zero).AsTask()
            : batch.ExecuteAsync().AsTask();
        await Assert.That(async () => await execution.WaitAsync(TimeSpan.FromSeconds(3)))
            .ThrowsExactly<RespireTimeoutException>();
        foreach (var result in pending)
        {
            await Assert.That(result.IsCompleted).IsTrue();
            await Assert.That(result.Error).IsTypeOf<RespireTimeoutException>();
        }
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("INCR ", StringComparison.Ordinal)))
            .IsEqualTo(2);
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("WAIT", StringComparison.Ordinal)))
            .IsFalse();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task WriteGateContentionConsumesBatchTimeout(bool boundedRing, bool circuitEnabled)
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray()) { SuppressReply = _ => true };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1,
            MaxInflightCommands = boundedRing ? 2 : 128,
            CommandTimeout = TimeSpan.FromSeconds(1),
            CircuitBreaker = circuitEnabled ? new() : null,
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
        });
        using var batch = client.CreateBatch();
        var pending = new RespirePending<long>[3];
        for (var index = 0; index < pending.Length; index++) pending[index] = batch.Increment($"counter:{index}");
        var connection = client.Core.Multiplexer!.GetConnection();
        var gate = typeof(RespireConnection).GetField("_writeGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(connection)!;
        using var release = new ManualResetEventSlim();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = new Thread(() =>
        {
            gate.GetType().GetMethod("Enter")!.Invoke(gate, null);
            try
            {
                held.SetResult();
                release.Wait(TimeSpan.FromSeconds(15));
            }
            finally { gate.GetType().GetMethod("Exit")!.Invoke(gate, null); }
        }) { IsBackground = true };
        var started = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var producer = new Thread(() =>
        {
            try { started.SetResult(batch.ExecuteAsync().AsTask()); }
            catch (Exception error) { started.SetException(error); }
        }) { IsBackground = true };
        holder.Start();
        try
        {
            await held.Task.WaitAsync(TimeSpan.FromSeconds(5));
            producer.Start();
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            // Wait until admission actually blocks on the gate before spending its budget.
            while ((producer.ThreadState & ThreadState.WaitSleepJoin) == 0)
            {
                await Assert.That(started.Task.IsCompleted).IsFalse();
                await Task.Delay(1, guard.Token);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(1_100));
            release.Set();
            var execution = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // An expired budget must not become another full one-second timeout after admission.
            await Assert.That(async () => await execution.WaitAsync(TimeSpan.FromMilliseconds(750)))
                .ThrowsExactly<RespireTimeoutException>();
            foreach (var result in pending)
            {
                await Assert.That(result.IsCompleted).IsTrue();
                await Assert.That(result.Error).IsTypeOf<RespireTimeoutException>();
            }
        }
        finally
        {
            release.Set();
            holder.Join(TimeSpan.FromSeconds(5));
            if ((producer.ThreadState & ThreadState.Unstarted) == 0) producer.Join(TimeSpan.FromSeconds(5));
            if (started.Task.IsCompletedSuccessfully)
            {
                try { await started.Task.Result.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (RespireTimeoutException) { }
            }
        }
    }

    [Test]
    public async Task CancellationDuringBoundedRingAdmissionCompletesEveryPending()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray())
        {
            SuppressReply = command => command != "INCR after",
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, MaxInflightCommands = 2,
            CommandTimeout = null,
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
        });
        using var cancellation = new CancellationTokenSource();
        using var batch = client.CreateBatch();
        var pending = new RespirePending<long>[100];
        for (var index = 0; index < pending.Length; index++) pending[index] = batch.Increment($"counter:{index}");
        var execution = batch.TryExecuteAsync(cancellation.Token);
        await WaitForCommandsAsync(server, 2).WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var result = await execution.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(result.Failures.Count).IsEqualTo(pending.Length);
        foreach (var operation in pending) await Assert.That(operation.IsCompleted).IsTrue();
        await Assert.That(server.CommandsSeen).IsEqualTo(2);

        // Late replies release the occupied slots without borrowing the next command's reply.
        await server.SendRawAsync(":1\r\n:1\r\n"u8.ToArray());
        await Assert.That(await client.IncrementAsync("after")).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(
            new[] { "INCR counter:0", "INCR counter:1", "INCR after" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ErrorsAndOwnedRepliesRetainTheirOperation()
    {
        await using var server = new FakeRespServer(
            "$3\r\none\r\n"u8.ToArray(), "-ERR wrong type\r\n"u8.ToArray(),
            "$3\r\ntwo\r\n"u8.ToArray(), ":7\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var batch = client.CreateBatch();
        var first = batch.GetBytes("first");
        var failed = batch.Increment("failed");
        var third = batch.GetBytes("third");
        var result = await batch.TryExecuteAsync();
        await Assert.That(result.Failures.Count).IsEqualTo(1);
        await Assert.That(result.Failures[0].Index).IsEqualTo(1);
        await Assert.That(failed.Error).IsTypeOf<RespireServerException>();
        await Assert.That(first.Result).IsEquivalentTo("one"u8.ToArray(), CollectionOrdering.Matching);
        await Assert.That(third.Result).IsEquivalentTo("two"u8.ToArray(), CollectionOrdering.Matching);
        await Assert.That(await client.IncrementAsync("after")).IsEqualTo(7);
        await Assert.That(first.Result).IsEquivalentTo("one"u8.ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task CancellationDrainsAcceptedRepliesBeforeNextCommand()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray())
        {
            SuppressReply = command => command != "INCR after",
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        using var batch = client.CreateBatch();
        var first = batch.Increment("first");
        var second = batch.Increment("second");
        var third = batch.Increment("third");
        var execution = batch.TryExecuteAsync(cancellation.Token);
        await WaitForCommandsAsync(server, 3).WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var result = await execution.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(result.Failures.Count).IsEqualTo(3);
        await server.SendRawAsync(":1\r\n:1\r\n:1\r\n"u8.ToArray());
        await Assert.That(await client.IncrementAsync("after")).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(
            new[] { "INCR first", "INCR second", "INCR third", "INCR after" }, CollectionOrdering.Matching);
    }

    private static async Task WaitForCommandsAsync(FakeRespServer server, int count)
    {
        while (server.CommandsSeen < count) await Task.Delay(1);
    }

    // The existing transport factory keeps write instrumentation outside production code.
    private sealed class CountingStream(Stream inner) : Stream
    {
        private int _writeCount;
        internal int WriteCount => Volatile.Read(ref _writeCount);
        internal TaskCompletionSource Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            Interlocked.Increment(ref _writeCount);
            Written.TrySetResult();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
