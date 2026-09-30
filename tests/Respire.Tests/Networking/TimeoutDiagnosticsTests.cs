using Respire.Commands;
using Respire.Networking;
using Respire.Infrastructure;
using System.Net;
using System.Net.Sockets;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class TimeoutDiagnosticsTests
{
    [Test]
    [Arguments("MOVED", false)]
    [Arguments("ASK", false)]
    [Arguments("READONLY", false)]
    [Arguments("MOVED", true)]
    [Arguments("ASK", true)]
    [Arguments("READONLY", true)]
    public async Task RedirectPoolAcquisitionDoesNotInspectTheCompletedSource(string redirect, bool cancelCaller)
    {
        var handshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var replacement = new FakeRespServer(FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (command != "CLIENT SETNAME redirect-timeout") return false;
                handshake.TrySetResult();
                return true;
            },
        };
        var slot = Respire.Internal.ClusterHash.GetSlot("private-key");
        var redirectReply = System.Text.Encoding.ASCII.GetBytes(redirect == "READONLY"
            ? "-READONLY replica\r\n" : $"-{redirect} {slot} 127.0.0.1:{replacement.Port}\r\n");
        await using var source = new FakeRespServer(2, FakeRespServer.OkReply, redirectReply);
        var topology = System.Text.Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{source.Port}\r\n");
        await using var seed = new FakeRespServer(FakeRespServer.OkReply, topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", seed.Port)], UseCluster = true, Connections = 1,
            ClientName = "redirect-timeout", CommandTimeout = null, ConnectTimeout = TimeSpan.FromSeconds(10),
        });
        if (redirect == "READONLY")
        {
            source.SuppressReply = command =>
            {
                if (command == "GET private-key")
                {
                    var router = client.Core.Cluster!;
                    router.SetSlotOwner(slot, router.GetMultiplexer(new("127.0.0.1", replacement.Port)));
                }
                return false;
            };
        }
        using var caller = new CancellationTokenSource();
        using var deadline = Respire.Internal.CommandTimeoutCancellation.Create(caller.Token, TimeSpan.FromSeconds(10));
        var pending = client.SendBlockingAsync("GET", new Cmd1(Verbs.Get, "private-key"), deadline.Token,
            cancellationTimeout: TimeSpan.FromSeconds(10), callerCancellationToken: caller.Token).AsTask();
        await handshake.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancelCaller)
        {
            caller.Cancel();
            await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)))
                .Throws<OperationCanceledException>();
        }
        else
        {
            deadline.Cancel();
            var error = await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)))
                .ThrowsExactly<RespireTimeoutException>();
            await Assert.That(error!.Diagnostics.Stage).IsEqualTo(RespireCommandStage.Connecting);
            await Assert.That(error.Diagnostics.ConnectionId).IsNull();
            await Assert.That(error.Diagnostics.Endpoint).IsNull();
            await Assert.That(error.Message).Contains("had not been enqueued");
        }
        await Assert.That(source.ReceivedCommands.Count(command => command == "GET private-key")).IsEqualTo(1);
        await Assert.That(replacement.ReceivedCommands.Any(command => command == "GET private-key")).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DedicatedTlsAcquisitionPreservesDeadlineAndCallerCancellation(bool cancelCaller)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", port)], Connections = 1, UseTls = true,
            ConnectTimeout = TimeSpan.FromSeconds(10), CommandTimeout = null,
        });
        using var caller = new CancellationTokenSource();
        var timeout = TimeSpan.FromSeconds(1);
        using var deadline = Respire.Internal.CommandTimeoutCancellation.Create(caller.Token, timeout);
        var pending = client.SendBlockingAsync("EVAL", new RawCommand(FakeRespServer.PingFrame), deadline.Token,
            cancellationTimeout: timeout, callerCancellationToken: caller.Token).AsTask();
        using var accepted = await listener.AcceptSocketAsync().WaitAsync(TimeSpan.FromSeconds(5));
        if (cancelCaller)
        {
            caller.Cancel();
            await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)))
                .Throws<OperationCanceledException>();
        }
        else
        {
            var error = await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)))
                .ThrowsExactly<RespireTimeoutException>();
            await Assert.That(error!.Diagnostics.Stage).IsEqualTo(RespireCommandStage.Connecting);
            await Assert.That(error.Diagnostics.Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", port));
            await Assert.That(error.Diagnostics.ConnectionId).IsNull();
            await Assert.That(error.Message).Contains("had not been enqueued");
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GuardedRemovalHandshakePreservesDeadlineAndLeaseSafety(bool cancelCaller)
    {
        var handshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selects = 0;
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (command != "SELECT 1" || Interlocked.Increment(ref selects) != 2) return false;
                handshake.TrySetResult();
                return true;
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], Database = 1, Connections = 1,
            ConnectTimeout = TimeSpan.FromSeconds(10), CommandTimeout = null,
        });
        // Use the guarded removal's fallback deadline without arming a competing SELECT deadline.
        client.RemovalLeaseTtl = TimeSpan.FromSeconds(1);
        using var caller = new CancellationTokenSource();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var pending = client.UnlinkGuardedAsync("private-key", caller.Token).AsTask();
        await handshake.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancelCaller)
        {
            caller.Cancel();
            await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)))
                .Throws<OperationCanceledException>();
        }
        else
        {
            var error = await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)))
                .ThrowsExactly<RespireTimeoutException>();
            await Assert.That(error!.CommandName).IsEqualTo("UNLINK");
            await Assert.That(error.Diagnostics.Stage).IsEqualTo(RespireCommandStage.Connecting);
            await Assert.That(error.Diagnostics.Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", server.Port));
        }
        if (cancelCaller)
            await Assert.That(server.ReceivedCommands).Contains(command => command.StartsWith("UNLINK "));
        else
            await Assert.That(System.Diagnostics.Stopwatch.GetElapsedTime(started)).IsGreaterThanOrEqualTo(client.RemovalLeaseTtl);
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("EVAL "))).IsFalse();
    }

    [Test]
    [Arguments(RespireCommandStage.Connecting, false, 0L, "Connection initialization")]
    [Arguments(RespireCommandStage.AwaitingReply, true, 0L, "Connection initialization")]
    [Arguments(RespireCommandStage.Buffered, false, 0L, "Writes are queued")]
    [Arguments(RespireCommandStage.Writing, false, 0L, "Writes are queued")]
    [Arguments(RespireCommandStage.AwaitingReply, false, 1L, "Writes are queued")]
    [Arguments(RespireCommandStage.WaitingForCapacity, false, 0L, "The in-flight queue is full")]
    [Arguments(RespireCommandStage.WaitingForCapacity, false, 1L, "Writes are queued")]
    [Arguments(RespireCommandStage.AwaitingReply, false, 0L, "Possible thread-pool starvation")]
    public async Task ConnectionHintsTakePriorityOverThreadPoolHeuristic(
        RespireCommandStage stage, bool reconnecting, long pendingBytes, string expectedHint)
    {
        var snapshot = RespireTimeoutDiagnostics.Capture(stage, pendingWriteBytes: pendingBytes,
            isReconnecting: reconnecting);
        // Fix the captured pool observation without changing the process-wide thread pool.
        typeof(RespireTimeoutDiagnostics).GetProperty(nameof(snapshot.PendingWorkItems))!.SetValue(snapshot, 1L);
        typeof(RespireTimeoutDiagnostics).GetProperty(nameof(snapshot.BusyWorkerThreads))!.SetValue(snapshot, 4);
        typeof(RespireTimeoutDiagnostics).GetProperty(nameof(snapshot.MinWorkerThreads))!.SetValue(snapshot, 4);
        await Assert.That(snapshot.PossibleThreadPoolStarvation).IsTrue();
        await Assert.That(snapshot.Hint).StartsWith(expectedHint);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task AcquisitionTimeoutReportsConnecting(bool cluster, bool correctionSetup)
    {
        // Accept TCP but never answer the TLS handshake: no RESP command can reach its
        // reply deadline before the enclosing acquisition timeout cancels initialization.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", port) },
            Connections = 1, UseCluster = cluster, UseTls = true,
            CommandTimeout = TimeSpan.FromMilliseconds(200), ConnectTimeout = TimeSpan.FromSeconds(5)
        });
        RespireTimeoutException? error;
        if (correctionSetup)
        {
            error = await Assert.That(async () => await client.EnsureReliableCorrectionOrderingAsync())
                .ThrowsExactly<RespireTimeoutException>();
        }
        else
        {
            await using var transaction = client.CreateTransaction();
            _ = transaction.GetString("key");
            error = await Assert.That(async () => await transaction.CommitAsync())
                .ThrowsExactly<RespireTimeoutException>();
        }
        await Assert.That(error!.Diagnostics.Stage).IsEqualTo(RespireCommandStage.Connecting);
        await Assert.That(error.Diagnostics.ConnectionId).IsNull();
        RespireEndpoint? expectedEndpoint = cluster ? (RespireEndpoint?)null : new RespireEndpoint("127.0.0.1", port);
        await Assert.That(error.Diagnostics.Endpoint).IsEqualTo(expectedEndpoint);
        await Assert.That(error.Diagnostics.Hint).StartsWith("Connection initialization");
        await Assert.That(error.Message).Contains("had not been enqueued");
        await Assert.That(error.Message.Contains("may still execute", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    [Arguments(RespireCommandStage.Connecting, false)]
    [Arguments(RespireCommandStage.WaitingForCapacity, false)]
    [Arguments(RespireCommandStage.Buffered, true)]
    [Arguments(RespireCommandStage.Writing, true)]
    [Arguments(RespireCommandStage.AwaitingReply, true)]
    [Arguments(RespireCommandStage.Unknown, true)]
    public async Task TimeoutMessageDistinguishesCommandsNotEnqueued(RespireCommandStage stage, bool mayExecute)
    {
        var error = new RespireTimeoutException("GET", TimeSpan.FromSeconds(1), null,
            RespireTimeoutDiagnostics.Capture(stage));
        await Assert.That(error.Message.Contains("may still execute", StringComparison.Ordinal)).IsEqualTo(mayExecute);
        await Assert.That(error.Message.Contains("had not been enqueued", StringComparison.Ordinal)).IsEqualTo(!mayExecute);
    }

    [Test]
    [Arguments(9L, RespireCommandStage.Buffered)]
    [Arguments(10L, RespireCommandStage.Buffered)]
    [Arguments(11L, RespireCommandStage.Writing)]
    [Arguments(19L, RespireCommandStage.Writing)]
    [Arguments(20L, RespireCommandStage.AwaitingReply)]
    [Arguments(21L, RespireCommandStage.AwaitingReply)]
    public async Task WriteStageIncludesExactFrameBoundaries(long sent, RespireCommandStage expected)
        => await Assert.That(RespireTimeoutDiagnostics.ComputeStage(sent, 10, 20)).IsEqualTo(expected);

    [Test]
    public async Task IntermediateRepliesRetainWholeFrameByteCountAcrossRingWrap()
    {
        var ring = new InflightRing(2);
        ring.TryEnqueue(InflightRing.DiscardSentinel, 10);
        ring.TryDequeue(out _);
        ring.TryEnqueue(InflightRing.DiscardSentinel, 10);
        ring.TryEnqueue(InflightRing.DiscardSentinel, 30);
        ring.TryDequeue(out _);
        await Assert.That(ring.CompletedWriteEnd).IsEqualTo(10);
        await Assert.That(ring.Count).IsEqualTo(1);
        ring.TryDequeue(out _);
        await Assert.That(ring.CompletedWriteEnd).IsEqualTo(30);
        ring.TryEnqueue(InflightRing.DiscardSentinel, 40);
        ring.TryDequeue(out _);
        await Assert.That(ring.CompletedWriteEnd).IsEqualTo(40);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PublicConstructionDoesNotInventObservations(bool withCause)
    {
        var error = withCause
            ? new RespireTimeoutException("GET", TimeSpan.FromSeconds(1), new IOException("cause"))
            : new RespireTimeoutException("GET", TimeSpan.FromSeconds(1));
        var snapshot = error.Diagnostics;
        await Assert.That(snapshot.Stage).IsEqualTo(RespireCommandStage.Unknown);
        await Assert.That(snapshot.Endpoint).IsNull();
        await Assert.That(snapshot.BusyWorkerThreads).IsNull();
        await Assert.That(snapshot.MinWorkerThreads).IsNull();
        await Assert.That(snapshot.BusyIoThreads).IsNull();
        await Assert.That(snapshot.MinIoThreads).IsNull();
        await Assert.That(snapshot.PendingWorkItems).IsNull();
        await Assert.That(snapshot.PossibleThreadPoolStarvation).IsFalse();
        await Assert.That(snapshot.Hint).Contains("No timeout observations");
    }

    [Test]
    [Arguments(true, false)]
    [Arguments(false, false)]
    [Arguments(true, true)]
    public async Task TransactionCancellationRequiresTheDeadlineToken(bool deadlineExpired, bool callerCancelled)
    {
        await using var server = new FakeRespServer();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        using var deadline = new CancellationTokenSource();
        using var unrelated = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        if (callerCancelled) caller.Cancel();
        deadline.Cancel();
        unrelated.Cancel();
        var cause = new OperationCanceledException(deadlineExpired ? deadline.Token : unrelated.Token);
        var source = MultiReplyPendingResponseSource.Rent(1, 0, "MULTI/EXEC");
        source.ConfigureTimeout(connection, TimeSpan.FromSeconds(1), caller.Token, deadline.Token);
        var reply = source.Task;
        source.TrySetException(cause);
        try
        {
            if (deadlineExpired && !callerCancelled)
            {
                var error = await Assert.That(async () => await reply).ThrowsExactly<RespireTimeoutException>();
                await Assert.That(error!.InnerException).IsSameReferenceAs(cause);
            }
            else
            {
                var error = await Assert.That(async () => await reply).ThrowsExactly<OperationCanceledException>();
                await Assert.That(error).IsSameReferenceAs(cause);
            }
        }
        finally
        {
            source.ReleaseRef(); // No receive loop owns this isolated source.
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task GuardedRemovalPreservesTimeoutSnapshotAndCallerCancellation(bool cluster, bool cancelCaller)
    {
        var scriptSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var target = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (!command.StartsWith("EVAL ", StringComparison.Ordinal)) return false;
                scriptSeen.TrySetResult();
                return true;
            }
        };
        var topology = System.Text.Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{target.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", cluster ? seed.Port : target.Port) },
            Connections = 1, UseCluster = cluster,
            CommandTimeout = TimeSpan.FromSeconds(2)
        });
        using var caller = new CancellationTokenSource();
        var removal = client.UnlinkGuardedAsync("private-key", caller.Token).AsTask();
        await scriptSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancelCaller)
        {
            caller.Cancel();
            await Assert.That(async () => await removal).ThrowsExactly<OperationCanceledException>();
        }
        else
        {
            var error = await Assert.That(async () => await removal).ThrowsExactly<RespireTimeoutException>();
            await Assert.That(error!.CommandName).IsEqualTo("UNLINK");
            await Assert.That(error.Diagnostics.Stage).IsEqualTo(RespireCommandStage.AwaitingReply);
            await Assert.That(error.Diagnostics.Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", target.Port));
            await Assert.That(error.Diagnostics.ConnectionId).IsNotNull();
            await Assert.That(error.Diagnostics.InflightCount).IsEqualTo(1);
            await Assert.That(error.Diagnostics.InflightBytes.GetValueOrDefault()).IsGreaterThan(0);
            // Capture must precede discarding the dedicated connection.
            await Assert.That(error.Diagnostics.IsConnected).IsTrue();
            await Assert.That(error.Message.Contains("private-key", StringComparison.Ordinal)).IsFalse();
        }
        // Cancellation and timeout both wait for the removal lease to be revoked.
        await Assert.That(target.ReceivedCommands).Contains(command => command.StartsWith("UNLINK "));
    }

    [Test]
    public async Task FailureObservationsAreCapturedBeforeTheCancellingCallReturns()
    {
        var source = new ObservingPendingResponse();
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var cancellingThread = Environment.CurrentManagedThreadId;
        source.TrySetCanceled(caller.Token);
        await Assert.That(source.CaptureThread).IsEqualTo(cancellingThread);
        await Assert.That(source.Captured).IsTrue();
        var failure = await source.Failure.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(failure is OperationCanceledException).IsTrue();
    }

    private sealed class ObservingPendingResponse : PendingResponse
    {
        internal readonly TaskCompletionSource<Exception> Failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Captured;
        internal int CaptureThread;

        protected override Exception PrepareException(Exception exception)
        {
            CaptureThread = Environment.CurrentManagedThreadId;
            Captured = true;
            return exception;
        }
        protected override void SetExceptionCore(Exception exception) => Failure.TrySetResult(exception);
        protected override void SetResultCore(in Respire.Protocol.RespValue result) => throw new NotSupportedException();
        protected override void ResetAndReturn() => throw new NotSupportedException();
    }

    [Test]
    public async Task TransactionCancellationSnapshotPrecedesConnectionTeardown()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        using var deadline = new CancellationTokenSource();
        deadline.Cancel();
        var source = MultiReplyPendingResponseSource.Rent(1, 0, "MULTI/EXEC");
        source.ConfigureTimeout(connection, TimeSpan.FromSeconds(1), default, deadline.Token);
        var reply = source.Task;
        source.TrySetCanceled(deadline.Token);
        await connection.DisposeAsync();
        try
        {
            var failure = await Assert.That(async () => await reply).ThrowsExactly<RespireTimeoutException>();
            await Assert.That(failure!.Diagnostics.IsConnected).IsTrue();
            await Assert.That(connection.IsConnected).IsFalse();
        }
        finally { source.ReleaseRef(); }
    }

    [Test]
    public async Task DedicatedSnapshotRetainsTheWholeAskingFrameAfterThePreludeReply()
    {
        var commandSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (command != "PING") return false;
                commandSeen.TrySetResult();
                return true;
            }
        };
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        using var cancellation = new CancellationTokenSource();
        var ping = new RawCommand(FakeRespServer.PingFrame);
        var response = Respire.Internal.ClusterRouter.SendBlockingAskingUncheckedAsync(connection, in ping, cancellation.Token);
        await commandSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        RespireTimeoutDiagnostics snapshot;
        do
        {
            deadline.Token.ThrowIfCancellationRequested();
            snapshot = connection.CaptureDedicatedTimeoutDiagnostics();
            if (snapshot.InflightCount == 1 && snapshot.Stage == RespireCommandStage.AwaitingReply) break;
            await Task.Delay(1, deadline.Token);
        } while (true);
        await Assert.That(snapshot.Stage).IsEqualTo(RespireCommandStage.AwaitingReply);
        await Assert.That(snapshot.InflightBytes).IsEqualTo((long)(FakeRespServer.PingFrame.Length + "*1\r\n$6\r\nASKING\r\n"u8.Length));
        cancellation.Cancel();
        await Assert.That(async () => await response).ThrowsExactly<OperationCanceledException>();
    }

    [Test]
    public async Task ImmediateTimeout_ReportsConnectionAndAwaitingReplyWithoutArguments()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply)
        {
            SuppressReply = command => command.StartsWith("GET ", StringComparison.Ordinal)
        };
        await using var client = await ConnectAsync(server.Port);
        await client.PingAsync();

        var error = await Assert.That(async () => await client.GetStringAsync("secret-key"))
            .ThrowsExactly<RespireTimeoutException>();
        var diagnostics = error!.Diagnostics;
        await Assert.That(diagnostics.Stage).IsEqualTo(RespireCommandStage.AwaitingReply);
        await Assert.That(diagnostics.Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", server.Port));
        await Assert.That(diagnostics.ConnectionId.GetValueOrDefault()).IsGreaterThan(0);
        await Assert.That(diagnostics.InflightCount).IsEqualTo(1);
        await Assert.That(diagnostics.InflightBytes.GetValueOrDefault()).IsGreaterThan(0);
        await Assert.That(diagnostics.PendingWriteBytes).IsEqualTo(0);
        await Assert.That(diagnostics.TimeSinceLastRead).IsNotNull();
        await Assert.That(diagnostics.TimeSinceLastWrite).IsNotNull();
        await Assert.That(diagnostics.IsConnected).IsTrue();
        await Assert.That(error.Message.Contains("secret-key", StringComparison.Ordinal)).IsFalse();
        await Assert.That(error.Message.Contains("Stage=AwaitingReply", StringComparison.Ordinal)).IsTrue();
        await Assert.That(diagnostics.BusyWorkerThreads.GetValueOrDefault()).IsGreaterThanOrEqualTo(0);
        await Assert.That(diagnostics.PendingWorkItems.GetValueOrDefault()).IsGreaterThanOrEqualTo(0);
    }

    [Test]
    public async Task FullRingTimeout_ReportsNotEnqueuedAndPreservesOutstandingBytes()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply) { SuppressReply = _ => true };
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new RespireConnectionOptions { MaxInflightCommands = 1, CommandTimeout = TimeSpan.FromMilliseconds(200) });
        var command = new RawCommand(FakeRespServer.PingFrame);
        using var cancellation = new CancellationTokenSource();
        var first = connection.SendAsync(command, cancellation.Token, armCommandDeadline: false).AsTask();
        var error = await Assert.That(async () => await connection.SendAsync(command, commandName: "PING"))
            .ThrowsExactly<RespireTimeoutException>();
        await Assert.That(error!.Diagnostics.Stage).IsEqualTo(RespireCommandStage.WaitingForCapacity);
        await Assert.That(error.Diagnostics.InflightCount).IsEqualTo(1);
        await Assert.That(error.Diagnostics.InflightBytes).IsEqualTo(FakeRespServer.PingFrame.LongLength);
        await Assert.That(error.Message.Contains("not been enqueued", StringComparison.Ordinal)).IsTrue();
        cancellation.Cancel();
        await Assert.That(async () => await first).ThrowsExactly<OperationCanceledException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TransactionTimeout_ReportsSharedOrDedicatedConnection(bool watched)
    {
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            SuppressReply = command => command == "EXEC"
        };
        await using var client = await ConnectAsync(server.Port);
        await using RespireTransactionBase transaction = watched
            ? await client.CreateTransactionAsync(["key"])
            : client.CreateTransaction();
        var pending = transaction.GetString("key");
        var error = await Assert.That(async () =>
        {
            if (transaction is RespireWatchedTransaction watchedTransaction)
                await watchedTransaction.CommitAsync();
            else
                await ((RespireTransaction)transaction).CommitAsync();
        })
            .ThrowsExactly<RespireTimeoutException>();
        await Assert.That(error!.Diagnostics.Stage).IsEqualTo(RespireCommandStage.AwaitingReply);
        await Assert.That(error.Diagnostics.Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", server.Port));
        await Assert.That(error.Diagnostics.InflightCount).IsEqualTo(1);
        await Assert.That(error.Diagnostics.InflightBytes.GetValueOrDefault()).IsGreaterThan(0);
        await Assert.That(pending.Error).IsSameReferenceAs(error);
    }

    [Test]
    public async Task BatchTimeout_PreservesPhysicalSnapshot()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply) { SuppressReply = _ => true };
        await using var client = await ConnectAsync(server.Port);
        using var batch = client.CreateBatch();
        var pending = batch.GetString("key");
        var result = await batch.TryExecuteAsync();
        await Assert.That(result.FailureCount).IsEqualTo(1);
        var error = await Assert.That(pending.Error).IsTypeOf<RespireTimeoutException>();
        await Assert.That(error!.Diagnostics.Stage).IsEqualTo(RespireCommandStage.AwaitingReply);
        await Assert.That(error.Diagnostics.Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", server.Port));
    }

    [Test]
    public async Task RelabelingTimeout_PreservesSnapshotAndCause()
    {
        var original = new RespireTimeoutException("CLIENT ID", TimeSpan.FromSeconds(1));
        var relabeled = new RespireTimeoutException("CLIENT ID / CLIENT KILL", TimeSpan.FromSeconds(2), original);
        await Assert.That(relabeled.Diagnostics).IsSameReferenceAs(original.Diagnostics);
        await Assert.That(relabeled.InnerException).IsSameReferenceAs(original);
        await Assert.That(relabeled.Diagnostics.Stage).IsEqualTo(RespireCommandStage.Unknown);
        await Assert.That(relabeled.Diagnostics.ConnectionId).IsNull();
    }

    [Test]
    public async Task StalledPeer_ReportsUnansweredPayloadAndObservedStage()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptSocketAsync();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", port,
            new RespireConnectionOptions { SocketSendBufferSize = 1024, CommandTimeout = TimeSpan.FromMilliseconds(500) });
        using var peer = await accept;
        peer.ReceiveBufferSize = 1024;
        // The peer never reads, but kernels differ in how much loopback data a send can
        // accept. Assert the observed write stage while all unanswered bytes remain in flight.
        using var cancellation = new CancellationTokenSource();
        var large = connection.SendAsync(new RawCommand(new byte[8 * 1024 * 1024]), cancellation.Token,
            armCommandDeadline: false).AsTask();
        var error = await Assert.That(async () => await connection.SendAsync(
                new RawCommand(FakeRespServer.PingFrame), commandName: "PING"))
            .ThrowsExactly<RespireTimeoutException>();
        var snapshot = error!.Diagnostics;
        var pendingBytes = snapshot.PendingWriteBytes.GetValueOrDefault();
        var expectedStage = pendingBytes == 0 ? RespireCommandStage.AwaitingReply
            : pendingBytes >= FakeRespServer.PingFrame.Length ? RespireCommandStage.Buffered : RespireCommandStage.Writing;
        await Assert.That(snapshot.Stage).IsEqualTo(expectedStage);
        await Assert.That(snapshot.InflightBytes).IsEqualTo(8 * 1024 * 1024 + FakeRespServer.PingFrame.LongLength);
        await Assert.That(snapshot.InflightCount).IsEqualTo(2);
        cancellation.Cancel();
        await Assert.That(async () => await large).ThrowsExactly<OperationCanceledException>();
    }

    [Test]
    public async Task SuccessfulRingAccounting_DoesNotAllocatePerCommand()
    {
        var ring = new InflightRing(1);
        var source = InflightRing.DiscardSentinel;
        ring.TryEnqueue(source, 10);
        ring.TryDequeue(out _);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 1; i <= 1000; i++)
        {
            ring.TryEnqueue(source, 10 + i);
            ring.TryDequeue(out _);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await Assert.That(allocated).IsEqualTo(0);
        await Assert.That(ring.CompletedWriteEnd).IsEqualTo(1010);
    }

    [Test]
    public async Task ReusedResponseSourceReceivesNewCommandOffsets()
    {
        await using var server = new FakeRespServer();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        var stamp = typeof(RespireConnection).GetMethod("StampWritePosition",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var pool = new PendingResponsePool(1);
        var source = pool.Rent();
        stamp.Invoke(connection, [source, 10]);
        source.TrySetResult(Respire.Protocol.RespValue.Integer(1));
        source.ReleaseRef();
        (await source.Task).Dispose();
        var reused = pool.Rent();
        await Assert.That(reused).IsSameReferenceAs(source);
        stamp.Invoke(connection, [reused, 7]);
        await Assert.That(reused.WriteStart).IsEqualTo(10);
        await Assert.That(reused.WriteEnd).IsEqualTo(17);
        var snapshot = RespireTimeoutDiagnostics.Capture(writtenBytes: 10)
            .ForCommand(reused.WriteStart, reused.WriteEnd);
        await Assert.That(snapshot.Stage).IsEqualTo(RespireCommandStage.Buffered);
        reused.TrySetResult(Respire.Protocol.RespValue.Integer(1));
        reused.ReleaseRef();
        (await reused.Task).Dispose();
    }

    [Test]
    public async Task DeadlineSweep_SharesObservationsAcrossExpiredCommands()
    {
        var pool = new PendingResponsePool(2);
        var ring = new InflightRing(2);
        var first = pool.Rent(commandName: "GET");
        var second = pool.Rent(commandName: "PING");
        first.Deadline = second.Deadline = 1;
        ring.TryEnqueue(first);
        ring.TryEnqueue(second);

        await Assert.That(ring.SweepExpired(2, TimeSpan.FromMilliseconds(1), connection: null)).IsEqualTo(-1);
        var firstError = await Assert.That(async () => await first.Task).ThrowsExactly<RespireTimeoutException>();
        var secondError = await Assert.That(async () => await second.Task).ThrowsExactly<RespireTimeoutException>();
        await Assert.That(firstError!.Diagnostics).IsSameReferenceAs(secondError!.Diagnostics);
        await Assert.That(firstError.CommandName).IsEqualTo("GET");
        await Assert.That(secondError.CommandName).IsEqualTo("PING");
        while (ring.TryDequeue(out var source))
            source.ReleaseRef();
    }

    [Test]
    public async Task SharedSnapshot_PreservesCountersAndClassifiesEachCommand()
    {
        var shared = RespireTimeoutDiagnostics.Capture(inflightCount: 3, inflightBytes: 30,
            pendingWriteBytes: 15, writtenBytes: 15);
        var replied = shared.ForCommand(0, 10);
        var writing = shared.ForCommand(10, 20);
        var buffered = shared.ForCommand(20, 30);
        await Assert.That(replied.Stage).IsEqualTo(RespireCommandStage.AwaitingReply);
        await Assert.That(writing.Stage).IsEqualTo(RespireCommandStage.Writing);
        await Assert.That(buffered.Stage).IsEqualTo(RespireCommandStage.Buffered);
        await Assert.That(shared.Stage).IsEqualTo(RespireCommandStage.Unknown);
        foreach (var snapshot in new[] { replied, writing, buffered })
        {
            await Assert.That(snapshot.InflightCount).IsEqualTo(3);
            await Assert.That(snapshot.InflightBytes).IsEqualTo(30);
            await Assert.That(snapshot.PendingWriteBytes).IsEqualTo(15);
            await Assert.That(snapshot.PendingWorkItems).IsEqualTo(shared.PendingWorkItems);
            await Assert.That(snapshot.BusyWorkerThreads).IsEqualTo(shared.BusyWorkerThreads);
        }
    }

    [Test]
    public async Task PhysicalSnapshot_DoesNotReportAnotherSlotsReconnect()
    {
        await using var server = new FakeRespServer(3, FakeRespServer.PongReply);
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync(
            "127.0.0.1", server.Port, connectionCount: 2);
        var healthy = multiplexer.GetConnection();
        var failed = multiplexer.GetConnection();
        var observed = new TaskCompletionSource<RespireTimeoutDiagnostics>(TaskCreationOptions.RunContinuationsAsynchronously);
        multiplexer.StateChanged += change =>
        {
            if (change.State == RespireConnectionState.Reconnecting)
                observed.TrySetResult(healthy.CaptureTimeoutDiagnostics());
        };
        await failed.DisposeAsync();
        multiplexer.GetConnection();
        multiplexer.GetConnection();
        var snapshot = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(snapshot.IsConnected).IsTrue();
        await Assert.That(snapshot.IsReconnecting).IsFalse();
    }

    private static ValueTask<RespireClient> ConnectAsync(int port)
        => RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", port) },
            Connections = 1, CommandTimeout = TimeSpan.FromMilliseconds(200)
        });
}
