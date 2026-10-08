using System.Net.Sockets;
using System.Reflection;
using System.Buffers;
using System.Threading.Tasks.Sources;
using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public sealed class GatheredSetTests
{
    [Test]
    [Arguments("true", true)]
    [Arguments("false", true)]
    [Arguments("error", true)]
    [Arguments("cancel", true)]
    [Arguments("true", false)]
    [Arguments("false", false)]
    [Arguments("error", false)]
    [Arguments("cancel", false)]
    public async Task ResponseAndWriteMustBothFinishBeforePublicCompletion(string outcome, bool responseFirst)
    {
        var ambient = new AsyncLocal<string?> { Value = "caller" };
        var response = new ControlledResponse(ambient);
        var lease = GatheredSetWriteLease.Rent();
        lease.RetainWrite();
        var completion = lease.CompleteResponseAsync(response.Task);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Exception? expected = outcome switch
        {
            "error" => new InvalidOperationException("response error"),
            "cancel" => new OperationCanceledException(cancellation.Token),
            _ => null,
        };
        if (responseFirst)
        {
            response.Complete(outcome == "true", expected);
            await response.Consumed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(completion.IsCompleted).IsFalse();
            lease.ReleaseWrite();
        }
        else
        {
            lease.ReleaseWrite();
            await Assert.That(completion.IsCompleted).IsFalse();
            response.Complete(outcome == "true", expected);
        }
        if (expected is null) await Assert.That(await completion).IsEqualTo(outcome == "true");
        else
        {
            Exception? actual = null;
            try { await completion; }
            catch (Exception error) { actual = error; }
            await Assert.That(ReferenceEquals(actual, expected)).IsTrue();
        }
        await Assert.That(response.ConsumptionCount).IsEqualTo(1);
        await Assert.That(response.ObservedAmbient).IsEqualTo("caller");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task SynchronousResponseRetainsItsValueUntilWriteRelease(bool result)
    {
        var lease = GatheredSetWriteLease.Rent();
        lease.RetainWrite();
        var completion = lease.CompleteResponseAsync(ValueTask.FromResult(result));
        await Assert.That(completion.IsCompleted).IsFalse();
        lease.ReleaseWrite();
        await Assert.That(await completion).IsEqualTo(result);
    }

    private sealed class ControlledResponse(AsyncLocal<string?> ambient) : IValueTaskSource<bool>
    {
        private ManualResetValueTaskSourceCore<bool> _core = new() { RunContinuationsAsynchronously = true };
        internal TaskCompletionSource Consumed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ConsumptionCount { get; private set; }
        internal string? ObservedAmbient { get; private set; }
        internal ValueTask<bool> Task => new(this, _core.Version);
        internal void Complete(bool result, Exception? error)
        {
            if (error is null) _core.SetResult(result);
            else _core.SetException(error);
        }
        bool IValueTaskSource<bool>.GetResult(short token)
        {
            ConsumptionCount++;
            ObservedAmbient = ambient.Value;
            try { return _core.GetResult(token); }
            finally { Consumed.TrySetResult(); }
        }
        ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => _core.GetStatus(token);
        void IValueTaskSource<bool>.OnCompleted(Action<object?> continuation, object? state, short token,
            ValueTaskSourceOnCompletedFlags flags) => _core.OnCompleted(continuation, state, token, flags);
    }

    [Test]
    public async Task PublicOperationCannotFinishBeforeItsLastWriteAndLeaseCanBeReused()
    {
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var lease = GatheredSetWriteLease.Rent();
            lease.RetainWrite();
            lease.RetainWrite();
            var completion = lease.FinishOperation();
            await Assert.That(completion.IsCompleted).IsFalse();
            lease.ReleaseWrite();
            await Assert.That(completion.IsCompleted).IsFalse();
            lease.ReleaseWrite();
            await Assert.That(await completion).IsTrue();
            await Assert.That(() => completion.GetAwaiter().GetResult()).ThrowsExactly<InvalidOperationException>();
        }
    }

    [Test]
    public async Task BinarySetDoesNotGrowTheCoalescingBufferAndPreservesFollowingFrames()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, ThreadPoolMonitoring = false,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        var connection = client.Core.Multiplexer.GetConnection();
        var initialCapacity = connection.WriteBufferCapacity;
        var flushField = typeof(RespireConnection).GetField("_flushTask", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var persistentSender = flushField.GetValue(connection);
        var payload = Enumerable.Range(0, 512 * 1024).Select(index => (byte)index).ToArray();
        var first = client.SetAsync("large", payload);
        var next = client.SetAsync("small", "after");
        await Assert.That(await first).IsTrue();
        await Assert.That(await next).IsTrue();
        await Assert.That(server.ReceivedArguments[0][2].AsSpan().SequenceEqual(payload)).IsTrue();
        await Assert.That(server.ReceivedCommands[1]).IsEqualTo("SET small after");
        await Assert.That(connection.WriteBufferCapacity).IsEqualTo(initialCapacity);
        await Assert.That(connection.InspectForTests().Inflight.Count).IsEqualTo(0);
        await Assert.That(ReferenceEquals(persistentSender, flushField.GetValue(connection))).IsTrue();
    }

    [Test]
    [Arguments("cancel")]
    [Arguments("deadline")]
    [Arguments("peer-close")]
    [Arguments("dispose")]
    [NotInParallel]
    public async Task BackpressuredPeerPreservesCallerPayloadThroughEveryFailureBoundary(string boundary)
    {
        var reads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(FakeRespServer.OkReply) { ReadGate = reads.Task };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, ThreadPoolMonitoring = false,
            Endpoints = [new("127.0.0.1", server.Port)],
            CommandTimeout = boundary == "deadline" ? TimeSpan.FromMilliseconds(500) : null,
        });
        await server.ConnectionAccepted;
        var connection = client.Core.Multiplexer.GetConnection();
        var initialCapacity = connection.WriteBufferCapacity;
        // Configure only this fixture's real socket, without adding a production testing hook.
        var socket = (Socket)typeof(RespireConnection).GetField("_socket", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(connection)!;
        // Disable this socket's send buffering and require an outstanding kernel send.
        // Keep the peer's normal receive window: shrinking it after connect clamps Linux TCP
        // and can make draining a resumed 5 MiB frame exceed the guard without any ownership bug.
        socket.SendBufferSize = 0;
        var payload = new byte[5 * 1024 * 1024];
        payload.AsSpan().Fill((byte)'a');
        using var cancellation = new CancellationTokenSource();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = client.SetAsync("parked", (RespireValue)payload, cancellationToken: cancellation.Token).AsTask();
        while (connection.CaptureTimeoutDiagnostics().InflightBytes == 0 || socket.Poll(0, SelectMode.SelectWrite))
        {
            if (pending.IsCompleted) await pending;
            await Task.Delay(1, guard.Token);
        }
        await Assert.That(connection.WriteBufferCapacity).IsEqualTo(initialCapacity);
        await Assert.That(connection.CaptureTimeoutDiagnostics().PendingWriteBytes > 0).IsTrue();
        if (boundary is "cancel" or "deadline")
        {
            if (boundary == "cancel") await cancellation.CancelAsync();
            // Cancellation's native completion reservation is observable while the write stays parked.
            await WaitForNativeCompletionAsync(connection, guard.Token);
            var remaining = connection.CaptureTimeoutDiagnostics().PendingWriteBytes;
            Console.WriteLine($"GATHER_WRITE_BOUNDARY {boundary}: pending write bytes {remaining}");
            await Assert.That(remaining > 0).IsTrue();
            await Assert.That(pending.IsCompleted).IsFalse();
            reads.TrySetResult();
            if (boundary == "cancel")
                await Assert.That(async () => await pending.WaitAsync(guard.Token)).Throws<OperationCanceledException>();
            else
                await Assert.That(async () => await pending.WaitAsync(guard.Token)).Throws<RespireTimeoutException>();
            payload.AsSpan().Fill((byte)'b');
            while (server.CommandsSeen == 0) await Task.Delay(1, guard.Token);
            await Assert.That(server.ReceivedArguments[0][2].All(value => value == (byte)'a')).IsTrue();
            var next = await client.SetAsync("after", "unchanged", cancellationToken: guard.Token);
            await Assert.That(next).IsTrue();
            await Assert.That(server.ReceivedCommands[1]).IsEqualTo("SET after unchanged");
        }
        else
        {
            if (boundary == "peer-close") server.CloseConnections();
            else await client.DisposeAsync();
            await Assert.That(async () => await pending.WaitAsync(guard.Token)).Throws<Exception>();
            // The public completion has released every send reference; caller reuse is permitted.
            payload.AsSpan().Fill((byte)'b');
        }
        reads.TrySetResult();
    }

    [Test]
    [Arguments("cancel", false)]
    [Arguments("deadline", false)]
    [Arguments("watchdog", false)]
    [Arguments("cancel", true)]
    [Arguments("deadline", true)]
    [Arguments("watchdog", true)]
    [NotInParallel]
    public async Task ConfiguredWatchdogReleasesBorrowedMemoryWhenPeerNeverResumes(string boundary, bool pausedConsumer)
    {
        var reads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(FakeRespServer.OkReply) { ReadGate = pausedConsumer ? null : reads.Task };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, ThreadPoolMonitoring = false,
            Endpoints = [new("127.0.0.1", server.Port)],
            CommandTimeout = boundary == "deadline" ? TimeSpan.FromMilliseconds(250) : null,
            ConnectionIdleReadTimeout = TimeSpan.FromSeconds(1),
        });
        var connection = client.Core.Multiplexer.GetConnection();
        var socket = (Socket)typeof(RespireConnection).GetField("_socket", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(connection)!;
        socket.SendBufferSize = 0;
        var payload = Enumerable.Repeat((byte)'a', 5 * 1024 * 1024).ToArray();
        using var cancellation = new CancellationTokenSource();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (pausedConsumer)
        {
            var reply = new byte[1024 * 1024 + 12];
            "$1048576\r\n"u8.CopyTo(reply);
            reply.AsSpan(10, 1024 * 1024).Fill((byte)'g');
            "\r\n"u8.CopyTo(reply.AsSpan(reply.Length - 2));
            server.ReplyOverride = (_, command) =>
            {
                if (command != "GET paused") return null;
                server.ReadGate = reads.Task;
                return reply;
            };
        }
        await using var paused = pausedConsumer ? await client.Strings.GetStreamAsync("paused", guard.Token) : null;
        if (pausedConsumer)
        {
            await Assert.That(paused is not null).IsTrue();
            var suppressions = typeof(RespireConnection).GetField("_responseTimeoutSuppressions",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            while ((int)suppressions.GetValue(connection)! == 0) await Task.Delay(1, guard.Token);
            // A consumer pause alone must survive a real watchdog interval. Do not fake the suppression count.
            await Task.Delay(TimeSpan.FromMilliseconds(1200), guard.Token);
            await Assert.That(connection.IsConnected).IsTrue();
        }
        var pending = client.SetAsync("never-resumed", (RespireValue)payload,
            cancellationToken: cancellation.Token).AsTask();
        while (connection.CaptureTimeoutDiagnostics().InflightBytes == 0 || socket.Poll(0, SelectMode.SelectWrite))
        {
            if (pending.IsCompleted) await pending;
            await Task.Delay(1, guard.Token);
        }
        await Assert.That(connection.CaptureTimeoutDiagnostics().PendingWriteBytes > 0).IsTrue();
        if (boundary == "cancel") await cancellation.CancelAsync();
        if (boundary is "cancel" or "deadline")
        {
            await WaitForNativeCompletionAsync(connection, guard.Token);
            await Assert.That(pending.IsCompleted).IsFalse();
        }
        if (boundary == "cancel")
            await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(3), guard.Token)).Throws<OperationCanceledException>();
        else if (boundary == "deadline")
            await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(3), guard.Token)).Throws<RespireTimeoutException>();
        else
            await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(3), guard.Token)).Throws<RespireConnectionException>();
        await Assert.That(connection.IsConnected).IsFalse();
        await Assert.That(server.CommandsSeen).IsEqualTo(pausedConsumer ? 1 : 0);
        // Completion is observable while the peer remains parked: every kernel send reference
        // has ended before this caller reuses its array. A partial frame cannot be reused.
        payload.AsSpan().Fill((byte)'b');
        reads.TrySetResult();
    }

    [Test]
    [NotInParallel]
    [Arguments("cancel")]
    [Arguments("deadline")]
    [Arguments("watchdog")]
    [Arguments("resume")]
    public async Task ConfiguredWatchdogReleasesBorrowedMemoryQueuedBehindCopiedWrite(string boundary)
    {
        var reads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(FakeRespServer.OkReply) { ReadGate = reads.Task };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, ThreadPoolMonitoring = false,
            Endpoints = [new("127.0.0.1", server.Port)],
            CommandTimeout = boundary == "deadline" ? TimeSpan.FromMilliseconds(250) : null,
            // A resumed 5 MiB copied send can remain pending while Linux drains it, especially
            // under coverage. Give that positive control a finite drain budget; stalled-peer
            // controls retain the short timeout that proves prompt ownership release.
            ConnectionIdleReadTimeout = TimeSpan.FromSeconds(boundary == "resume" ? 5 : 1),
        });
        var connection = client.Core.Multiplexer.GetConnection();
        var socket = (Socket)typeof(RespireConnection).GetField("_socket", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(connection)!;
        socket.SendBufferSize = 0;
        using var cancellation = new CancellationTokenSource();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // SET GET uses the copied serializer. Park the real kernel send before borrowing another array.
        var copied = client.Strings.GetAndSetAsync("copied-first", new byte[5 * 1024 * 1024]).AsTask();
        while (connection.CaptureTimeoutDiagnostics().InflightBytes == 0 || socket.Poll(0, SelectMode.SelectWrite))
        {
            if (copied.IsCompleted) await copied;
            await Task.Delay(1, guard.Token);
        }
        var payload = Enumerable.Repeat((byte)'a', 512 * 1024).ToArray();
        var pending = client.SetAsync("queued-borrowed", (RespireValue)payload,
            cancellationToken: cancellation.Token).AsTask();
        await Assert.That(connection.CaptureTimeoutDiagnostics().PendingWriteBytes > payload.Length).IsTrue();
        await Assert.That(ReadBorrowedWatch(connection).Buffers).IsEqualTo(1);
        await Assert.That(ReadBorrowedWatch(connection).Timestamp != 0).IsTrue();
        if (boundary == "cancel") await cancellation.CancelAsync();
        try
        {
            if (boundary == "resume")
            {
                reads.TrySetResult();
                await Assert.That(await pending.WaitAsync(guard.Token)).IsTrue();
                await copied.WaitAsync(guard.Token);
                await Assert.That(server.ReceivedArguments[0][2].All(value => value == 0)).IsTrue();
                await Assert.That(server.ReceivedArguments[1][2].All(value => value == (byte)'a')).IsTrue();
                // Both write owners have finished. Inspect the quiescent watch directly rather
                // than making successful drainage depend on a one-second scheduling budget.
                await Assert.That(ReadBorrowedWatch(connection).Buffers).IsEqualTo(0);
                await Assert.That(ReadBorrowedWatch(connection).Timestamp).IsEqualTo(0);
                await Assert.That(connection.IsConnected).IsTrue();
                await Assert.That(await client.SetAsync("following", "small", cancellationToken: guard.Token)).IsTrue();
                return;
            }
            if (boundary == "cancel")
                await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(3), guard.Token)).Throws<OperationCanceledException>();
            else if (boundary == "deadline")
                await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(3), guard.Token)).Throws<RespireTimeoutException>();
            else
                await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(3), guard.Token)).Throws<RespireConnectionException>();
            await Assert.That(connection.IsConnected).IsFalse();
            await Assert.That(server.CommandsSeen).IsEqualTo(0);
            payload.AsSpan().Fill((byte)'b');
        }
        finally
        {
            // Always consume the preceding command, including when the regression times out.
            await client.DisposeAsync();
            try { await copied; } catch (RespireException) { }
            reads.TrySetResult();
        }
    }

    private static (int Buffers, long Timestamp) ReadBorrowedWatch(RespireConnection connection)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var progress = typeof(RespireConnection).GetField("_flushProgress", flags)!.GetValue(connection)!;
        var type = progress.GetType();
        return ((int)type.GetField("GatheredWriteBufferCount", flags)!.GetValue(progress)!,
            (long)type.GetField("GatheredWriteDeadlineTimestamp", flags)!.GetValue(progress)!);
    }

    [Test]
    [NotInParallel]
    public async Task CompletedBorrowedWriteDoesNotLeaveAnIdleWatchdogArmed()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, ThreadPoolMonitoring = false,
            Endpoints = [new("127.0.0.1", server.Port)],
            ConnectionIdleReadTimeout = TimeSpan.FromMilliseconds(200), CommandTimeout = null,
        });
        var connection = client.Core.Multiplexer.GetConnection();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.That(await client.SetAsync("complete", new byte[512 * 1024],
            cancellationToken: guard.Token)).IsTrue();
        // Cover multiple real watchdog scans after both the write and its reply have completed.
        await Task.Delay(600, guard.Token);
        await Assert.That(connection.IsConnected).IsTrue();
        await Assert.That(await client.SetAsync("following", "small", cancellationToken: guard.Token)).IsTrue();
        await Assert.That(server.ReceivedCommands[1]).IsEqualTo("SET following small");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task QueuedWriteKeepsPublicCompletionPendingAfterNativeCallerConsumption(bool deadline)
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, ThreadPoolMonitoring = false,
            Endpoints = [new("127.0.0.1", server.Port)],
            CommandTimeout = deadline ? TimeSpan.FromMilliseconds(500) : null,
        });
        var connection = client.Core.Multiplexer.GetConnection();
        var gate = typeof(RespireConnection).GetField("_writeGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(connection)!;
        var enter = gate.GetType().GetMethod("Enter")!;
        var exit = gate.GetType().GetMethod("Exit")!;
        var published = new TaskCompletionSource<Task<bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var payload = Enumerable.Repeat((byte)'a', 512 * 1024).ToArray();
        using var cancellation = new CancellationTokenSource();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        // A dedicated producer owns the gate reentrantly. Its flush wake cannot run inline,
        // and the persistent sender cannot take this gate until the fixture releases it.
        var producer = Task.Factory.StartNew(() =>
        {
            enter.Invoke(gate, null);
            try
            {
                published.TrySetResult(client.SetAsync("queued", (RespireValue)payload,
                    cancellationToken: cancellation.Token).AsTask());
                release.Task.GetAwaiter().GetResult();
            }
            catch (Exception error) { published.TrySetException(error); throw; }
            finally { exit.Invoke(gate, null); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            var pending = await published.Task.WaitAsync(guard.Token);
            if (!deadline) await cancellation.CancelAsync();
            await WaitForNativeCompletionAsync(connection, guard.Token);
            await Assert.That(server.CommandsSeen).IsEqualTo(0);
            await Assert.That(pending.IsCompleted).IsFalse();
            release.TrySetResult();
            await producer.WaitAsync(guard.Token);
            if (deadline)
                await Assert.That(async () => await pending.WaitAsync(guard.Token)).Throws<RespireTimeoutException>();
            else
                await Assert.That(async () => await pending.WaitAsync(guard.Token)).Throws<OperationCanceledException>();
            payload.AsSpan().Fill((byte)'b');
            while (server.CommandsSeen == 0) await Task.Delay(1, guard.Token);
            await Assert.That(server.ReceivedArguments[0][2].All(value => value == (byte)'a')).IsTrue();
        }
        finally
        {
            release.TrySetResult();
            await producer.WaitAsync(guard.Token);
        }
    }

    private static async Task WaitForNativeCompletionAsync(RespireConnection connection, CancellationToken guard)
    {
        while (true)
        {
            if (connection.InspectForTests().Inflight.TryPeek(out var source)
                && PendingResponse.IsCompleted(source.State) && source.InspectForTests().ReferenceCount == 1) return;
            await Task.Delay(1, guard);
        }
    }

    [Test]
    public async Task BoundedVectorsPreserveSlicedPayloadsOptionsAndInterleavedSmallCommands()
    {
        var gathered = new WriteBuffer(1024);
        var copied = new WriteBuffer(1024);
        var completions = new List<ValueTask<bool>>();
        try
        {
            var bytes = Enumerable.Range(0, 8192 + 24).Select(index => (byte)index).ToArray();
            var slice = new ArraySegment<byte>(bytes, 11, 8192);
            for (var index = 0; index < 256; index++)
            {
                var command = new SetCommand($"key-{index}", new RespireValue(slice.AsMemory()),
                    RespireExpiry.Keep, SetWhen.NotExists, returnOld: false);
                var lease = GatheredSetWriteLease.Rent();
                await Assert.That(gathered.PrepareBorrowedPayload()).IsTrue();
                var wrapper = new GatheredSetCommand(command, slice, lease);
                gathered.AddBorrowedPayload(wrapper.WriteEnvelope(gathered), slice, lease);
                completions.Add(lease.FinishOperation());
                var writer = new RespWriter(copied, command.GetWriteSizeHint());
                command.Write(ref writer);
                writer.Complete();
                var small = new Cmd1(Verbs.Get, "after");
                writer = new RespWriter(gathered, small.GetWriteSizeHint());
                small.Write(ref writer);
                writer.Complete();
                writer = new RespWriter(copied, small.GetWriteSizeHint());
                small.Write(ref writer);
                writer.Complete();
            }
            await Assert.That(gathered.PrepareBorrowedPayload()).IsFalse();
            using var wire = new MemoryStream();
            var cursor = 0;
            while (true)
            {
                var segments = gathered.GetSendSegments(ref cursor);
                await Assert.That(segments.Count).IsLessThanOrEqualTo(16);
                if (segments.Count == 0) break;
                while (segments.Count != 0)
                {
                    // Short progress crosses headers, sliced payloads, trailers and vector batches.
                    var sent = Math.Min(29, segments.Sum(segment => segment.Count));
                    var remaining = sent;
                    foreach (var segment in segments)
                    {
                        var count = Math.Min(remaining, segment.Count);
                        wire.Write(segment.AsSpan()[..count]);
                        remaining -= count;
                        if (remaining == 0) break;
                    }
                    WriteBuffer.ConsumeSentSegments(segments, sent);
                }
            }
            await Assert.That(wire.ToArray().AsSpan().SequenceEqual(copied.WrittenMemory.Span)).IsTrue();
            await Assert.That(completions.All(completion => !completion.IsCompleted)).IsTrue();
            gathered.CompleteWrite();
            foreach (var completion in completions) await Assert.That(await completion).IsTrue();
            gathered.Reset();
            await Assert.That(gathered.HasBorrowedPayloads).IsFalse();
        }
        finally { gathered.Release(); copied.Release(); }
    }

    [Test]
    public async Task NonArrayMemoryRetainsSafeCopyingFallback()
    {
        using var memory = new NonArrayMemory(128 * 1024);
        memory.GetSpan().Fill((byte)'a');
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, ThreadPoolMonitoring = false,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        await Assert.That(await client.SetAsync("non-array", new RespireValue(memory.Memory))).IsTrue();
        await Assert.That(client.Core.Multiplexer.GetConnection().WriteBufferCapacity).IsGreaterThanOrEqualTo(128 * 1024);
        await Assert.That(server.ReceivedArguments[0][2].All(value => value == (byte)'a')).IsTrue();
    }

    [Test]
    public async Task CustomStreamCancellationCanReturnWhileOwnedCopyRemainsInWrite()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        PausedWriteStream? stream = null;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, ThreadPoolMonitoring = false,
            Endpoints = [new("127.0.0.1", server.Port)],
            TestingStreamFactory = async (host, port, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(host, port, token);
                return stream = new PausedWriteStream(new NetworkStream(socket, ownsSocket: true));
            },
        });
        var payload = new byte[128 * 1024];
        payload.AsSpan().Fill((byte)'a');
        using var cancellation = new CancellationTokenSource();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var pending = client.SetAsync("copied", (RespireValue)payload, cancellationToken: cancellation.Token).AsTask();
            await stream!.Started.Task.WaitAsync(guard.Token);
            await cancellation.CancelAsync();
            await Assert.That(async () => await pending.WaitAsync(guard.Token)).Throws<OperationCanceledException>();
            payload.AsSpan().Fill((byte)'b');
            // The stream still owns the accepted write. Its payload is the client's copy.
            await Assert.That(stream.Pending.Span.IndexOf(payload)).IsEqualTo(-1);
            stream.Resume.TrySetResult();
            while (server.CommandsSeen == 0) await Task.Delay(1, guard.Token);
            await Assert.That(server.ReceivedArguments[0][2].All(value => value == (byte)'a')).IsTrue();
            await Assert.That(await client.SetAsync("following", "small", cancellationToken: guard.Token)).IsTrue();
            await Assert.That(server.ReceivedCommands[1]).IsEqualTo("SET following small");
        }
        finally { stream!.Resume.TrySetResult(); }
    }

    private sealed class NonArrayMemory(int length) : MemoryManager<byte>
    {
        private readonly byte[] _bytes = new byte[length];
        public override Span<byte> GetSpan() => _bytes;
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }

    private sealed class PausedWriteStream(Stream inner) : Stream
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ReadOnlyMemory<byte> Pending { get; private set; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Pending = buffer;
            Started.TrySetResult();
            await Resume.Task;
            await inner.WriteAsync(buffer, cancellationToken);
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.ReadAsync(buffer, cancellationToken);
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
