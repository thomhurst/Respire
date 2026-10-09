using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class TransactionDeadlineTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);
    private static readonly byte[] Queued = "+QUEUED\r\n"u8.ToArray();
    private static readonly byte[] Committed = "*1\r\n+OK\r\n"u8.ToArray();

    [Test]
    [Arguments(false, false, false)]
    [Arguments(false, true, false)]
    [Arguments(true, false, false)]
    [Arguments(true, true, false)]
    [Arguments(false, false, true)]
    [Arguments(false, true, true)]
    [Arguments(true, false, true)]
    [Arguments(true, true, true)]
    public async Task SweepTimesOutCommitWithoutATimeoutToken(bool watched, bool drainQueueReplies, bool cluster)
    {
        var exec = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(2)
        {
            SuppressReply = command =>
            {
                if (command == "EXEC") exec.TrySetResult();
                return command == "EXEC" || !drainQueueReplies && command is ("MULTI" or "SET k v");
            },
        };
        var topology = System.Text.Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{server.Port}\r\n");
        server.ReplyOverride = (_, command) => command switch
        {
            "CLUSTER SLOTS" => topology,
            "SET k v" => Queued,
            _ => FakeRespServer.OkReply,
        };
        await using var client = await ConnectAsync(server.Port, Timeout, cluster);
        await using RespireTransactionBase transaction = watched
            ? await client.CreateTransactionAsync(["k"]) : client.CreateTransaction();
        var pending = transaction.Set("k", "v");
        var connection = watched
            ? transaction.InspectForTests().WatchConnection!
            : await client.AcquireConnectionAsync(cluster ? ClusterHash.GetSlot("k") : null, default);
        var earliest = Environment.TickCount64;
        var commit = transaction is RespireWatchedTransaction watch
            ? watch.CommitAsync().AsTask() : CommitAsync((RespireTransaction)transaction);
        await exec.Task.WaitAsync(Limit);
        var ring = GetRing(connection);
        if (drainQueueReplies)
        {
            // Wait for receive processing, rather than assuming the server write was parsed.
            await WaitUntilAsync(() => ring.Count == 1);
        }
        await Assert.That(ring.TryPeek(out var source)).IsTrue();
        await WaitUntilAsync(() => connection.CaptureTimeoutDiagnostics(source.WriteStart, source.WriteEnd).Stage
            == RespireCommandStage.AwaitingReply);
        await Assert.That(source.CommandName).IsEqualTo("MULTI/EXEC");
        await Assert.That(source.Deadline.Ticks).IsGreaterThanOrEqualTo(earliest + (long)Timeout.TotalMilliseconds);
        await Assert.That(source.InspectForTests().RegisteredCancellationToken.CanBeCanceled).IsFalse();
        await Assert.That(commit.IsCompleted).IsFalse();

        // Advance the sweep's observation directly; no wall-clock timeout or competing timer.
        ring.SweepExpired(source.Deadline.Ticks + 1, Timeout, connection);
        var error = await Assert.That(async () => await commit.WaitAsync(Limit)).ThrowsExactly<RespireTimeoutException>();
        await Assert.That(error!.CommandName).IsEqualTo("MULTI/EXEC");
        await Assert.That(error.Diagnostics.Stage).IsEqualTo(RespireCommandStage.AwaitingReply);
        await Assert.That(pending.Error).IsSameReferenceAs(error);
        if (watched)
        {
            await Assert.That(connection.IsConnected).IsFalse(); // Failed WATCH leases must be discarded.
        }
        else
        {
            // Timed-out sources keep every FIFO slot until the late replies drain.
            await server.SendRawAsync(drainQueueReplies ? Committed : [.. FakeRespServer.OkReply, .. Queued, .. Committed]);
            await WaitUntilAsync(() => ring.Count == 0);
            await Assert.That(await client.SetAsync("next", "value")).IsTrue();
            await Assert.That(connection.IsConnected).IsTrue();
        }
    }

    [Test]
    public async Task CallerCancellationKeepsTokenAndDrainsLateReplies()
    {
        var exec = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer
        {
            ReplyOverride = (_, command) => command == "SET k v" ? Queued : FakeRespServer.OkReply,
            SuppressReply = command => { if (command != "EXEC") return false; exec.TrySetResult(); return true; },
        };
        await using var client = await ConnectAsync(server.Port, Timeout);
        await using var transaction = client.CreateTransaction();
        var pending = transaction.Set("k", "v");
        using var caller = new CancellationTokenSource();
        var commit = transaction.CommitAsync(caller.Token).AsTask();
        await exec.Task.WaitAsync(Limit);
        caller.Cancel();
        var error = await Assert.That(async () => await commit.WaitAsync(Limit)).ThrowsExactly<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
        await Assert.That(pending.Error).IsSameReferenceAs(error);
        await server.SendRawAsync(Committed);
        await Assert.That(await client.SetAsync("next", "value")).IsTrue();
    }

    [Test]
    public async Task ColdAcquisitionCancellationKeepsCallerToken()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, UseTls = true,
            CommandTimeout = Timeout, ConnectTimeout = Timeout,
            Endpoints = [new("127.0.0.1", port)],
        });
        await using var transaction = client.CreateTransaction();
        var pending = transaction.Set("k", "v");
        using var caller = new CancellationTokenSource();
        var commit = transaction.CommitAsync(caller.Token).AsTask();
        using var accepted = await listener.AcceptSocketAsync().WaitAsync(Limit);
        caller.Cancel();
        var error = await Assert.That(async () => await commit.WaitAsync(Limit)).ThrowsExactly<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
        await Assert.That(pending.Error).IsSameReferenceAs(error);
    }

    [Test]
    public async Task ColdAcquisitionDoesNotResetExecDeadline()
    {
        var handshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exec = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer
        {
            ReplyOverride = (_, command) => command == "SET k v" ? Queued : FakeRespServer.OkReply,
            SuppressReply = command =>
            {
                if (command == "CLIENT SETNAME acquisition-budget") { handshake.TrySetResult(); return true; }
                if (command == "EXEC") { exec.TrySetResult(); return true; }
                return false;
            },
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, ClientName = "acquisition-budget",
            CommandTimeout = Timeout, Endpoints = [new("127.0.0.1", server.Port)],
        });
        await using var transaction = client.CreateTransaction();
        _ = transaction.Set("k", "v");
        var commit = transaction.CommitAsync().AsTask();
        var startedBy = Environment.TickCount64;
        await handshake.Task.WaitAsync(Limit);
        // Guarantee a later tick before finishing initialization, so a reset is observable.
        await WaitUntilAsync(() => Environment.TickCount64 > startedBy);
        await server.SendRawAsync(FakeRespServer.OkReply);
        await exec.Task.WaitAsync(Limit);
        var connection = client.Core.Multiplexer.GetConnection();
        var ring = GetRing(connection);
        await WaitUntilAsync(() => ring.Count == 1);
        await Assert.That(ring.TryPeek(out var source)).IsTrue();
        await Assert.That(source.Deadline.Ticks).IsLessThanOrEqualTo(startedBy + (long)Timeout.TotalMilliseconds);
        await Assert.That(source.InspectForTests().RegisteredCancellationToken.CanBeCanceled).IsFalse();
        await server.SendRawAsync(Committed);
        await commit.WaitAsync(Limit);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ImportCommitSharesDeadlineAcrossMultiAndExec(bool stopAtMulti)
    {
        var multi = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exec = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inMulti = false;
        await using var server = new FakeRespServer(20)
        {
            SuppressReply = command =>
            {
                if (command == "MULTI") { inMulti = true; multi.TrySetResult(); return true; }
                if (command == "EXEC") { exec.TrySetResult(); return true; }
                return false;
            },
            ReplyOverride = (_, _) => inMulti ? Queued : FakeRespServer.OkReply,
        };
        await using var client = await ConnectAsync(server.Port, Timeout);
        await using var session = await client.Hashes.CreateImportSessionAsync();
        await session.PrepareAsync("schema", "field");
        await using var transaction = session.CreateTransaction();
        var pending = transaction.Hashes.Import("k", "schema", "v");
        var commit = transaction.CommitAsync().AsTask();
        var startedBy = Environment.TickCount64;
        await multi.Task.WaitAsync(Limit);
        if (!stopAtMulti)
        {
            await WaitUntilAsync(() => Environment.TickCount64 > startedBy);
            var connectionId = server.ReceivedConnectionIds[Array.IndexOf(server.ReceivedCommands.ToArray(), "MULTI")];
            await server.SendRawAsync(FakeRespServer.OkReply, connectionId);
            await exec.Task.WaitAsync(Limit);
        }
        var connection = session.Connection;
        var ring = GetRing(connection);
        await WaitUntilAsync(() => ring.Count == 1);
        await Assert.That(ring.TryPeek(out var source)).IsTrue();
        await Assert.That(source.Deadline.Ticks).IsLessThanOrEqualTo(startedBy + (long)Timeout.TotalMilliseconds);
        await Assert.That(source.InspectForTests().RegisteredCancellationToken.CanBeCanceled).IsFalse();
        ring.SweepExpired(source.Deadline.Ticks + 1, Timeout, connection);
        var error = await Assert.That(async () => await commit.WaitAsync(Limit)).ThrowsExactly<RespireTimeoutException>();
        await Assert.That(error!.CommandName).IsEqualTo(stopAtMulti ? "MULTI" : "MULTI/EXEC");
        await Assert.That(pending.Error).IsSameReferenceAs(error);
        await Assert.That(connection.IsConnected).IsFalse();
        await Assert.That(async () => await session.SetAsync("later", "schema", "v")).Throws<ObjectDisposedException>();
    }

    [Test, NotInParallel]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task ImportCredentialGatePreservesDeadlineAndCallerCancellation(bool cancelCaller, bool preCanceled)
    {
        await using var server = new FakeRespServer(20, FakeRespServer.OkReply);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1,
            // Setup shares this budget; allow AUTH and PREPARE to complete before
            // testing expiry at the held gate, still well below the ten-second safety limit.
            CommandTimeout = cancelCaller ? Timeout : TimeSpan.FromSeconds(2),
            CredentialProvider = new ExpiringCredentials(),
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        await using var session = await client.Hashes.CreateImportSessionAsync();
        await session.PrepareAsync("schema", "field");
        var gate = await session.Connection.AcquireCredentialSequenceAsync(default);
        await using var transaction = session.CreateTransaction();
        var pending = transaction.Hashes.Import("k", "schema", "v");
        using var caller = new CancellationTokenSource();
        if (preCanceled) caller.Cancel();
        var commit = transaction.CommitAsync(caller.Token).AsTask();
        try
        {
            if (cancelCaller)
            {
                caller.Cancel();
                var error = await Assert.That(async () => await commit.WaitAsync(Limit)).Throws<RespireCommandNotSubmittedException>();
                await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
                await Assert.That(pending.Error).IsSameReferenceAs(error);
            }
            else
            {
                var error = await Assert.That(async () => await commit.WaitAsync(Limit)).ThrowsExactly<RespireTimeoutException>();
                await Assert.That(error!.CommandName).IsEqualTo("MULTI/EXEC");
                await Assert.That(error.Diagnostics.Stage).IsEqualTo(RespireCommandStage.WaitingForCapacity);
                await Assert.That(pending.Error).IsSameReferenceAs(error);
            }
            await Assert.That(server.ReceivedCommands.Contains("MULTI")).IsFalse();
        }
        finally { gate.Dispose(); }
        // No MULTI was accepted, so prepared fieldsets and the import lease remain usable.
        await Assert.That(await session.SetAsync("later", "schema", "v")).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ImportFailureBeforeMultiKeepsPreparedFieldsets(bool cancelCaller)
    {
        var timeout = TimeSpan.FromSeconds(2);
        await using var server = new FakeRespServer(20, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("HELLO ", StringComparison.Ordinal)
                ? "%1\r\n+proto\r\n:3\r\n"u8.ToArray() : null,
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Connections = 1, CommandTimeout = timeout,
            ClientSideCache = new() { CoalesceConcurrentMisses = true },
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        await using var session = await client.Hashes.CreateImportSessionAsync();
        await session.PrepareAsync("schema", "field");
        await using var transaction = session.CreateTransaction();
        var pending = transaction.Hashes.Import("k", "schema", "v");
        using var caller = new CancellationTokenSource();
        var started = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        // A real joinable flight makes invalidation require the gate. Empty invalidations
        // deliberately skip it, so merely holding the gate cannot park commit admission.
        var cache = client.Core.ClientCache!;
        var readReply = new TaskCompletionSource<RespValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        var flight = cache.CoalesceReadAsync(new ClientCacheCommandKey("GET", (RespireValue)"held-flight"), readReply,
            static (reply, cancellation) => new ValueTask<RespValue>(reply.Task.WaitAsync(cancellation)), default).AsTask();
        await Assert.That(cache.ActiveSharedReadCount).IsEqualTo(1);
        // Hold the pre-admission cache barrier until the budget is spent. No transport
        // timer or semaphore cancellation callback can win this failure path.
        var worker = new Thread(() =>
        {
            try { started.TrySetResult(transaction.CommitAsync(caller.Token).AsTask()); }
            catch (Exception error) { started.TrySetException(error); }
        }) { IsBackground = true };
        try
        {
            using (cache.InspectForTests().SharedReadGate.EnterScope())
            {
                worker.Start();
                if (!SpinWait.SpinUntil(() => (worker.ThreadState & ThreadState.WaitSleepJoin) != 0, Limit))
                    throw new TimeoutException("Commit did not enter the held cache barrier.");
                if (cancelCaller) caller.Cancel();
                else Thread.Sleep(timeout + TimeSpan.FromMilliseconds(20));
            }
            var commit = await started.Task.WaitAsync(Limit);
            if (cancelCaller)
            {
                var error = await Assert.That(async () => await commit.WaitAsync(Limit)).Throws<RespireCommandNotSubmittedException>();
                await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
                await Assert.That(pending.Error).IsSameReferenceAs(error);
            }
            else
            {
                var error = await Assert.That(async () => await commit.WaitAsync(Limit)).ThrowsExactly<RespireTimeoutException>();
                await Assert.That(error!.CommandName).IsEqualTo("MULTI/EXEC");
                await Assert.That(error.Diagnostics.Stage).IsEqualTo(RespireCommandStage.WaitingForCapacity);
                await Assert.That(pending.Error).IsSameReferenceAs(error);
            }
        }
        finally
        {
            readReply.TrySetCanceled();
            try { (await flight.WaitAsync(Limit)).Dispose(); }
            catch (OperationCanceledException) { }
            if (!worker.Join(Limit)) throw new TimeoutException("Commit worker did not stop.");
        }
        await Assert.That(server.ReceivedCommands.Contains("MULTI")).IsFalse();
        await Assert.That(await session.SetAsync("later", "schema", "v")).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ClusterCommitUsesPublishedOwnerDuringPendingRefresh(bool withReconnectPolicy)
    {
        await using var server = new FakeRespServer(100, FakeRespServer.OkReply);
        var topology = System.Text.Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{server.Port}\r\n");
        server.ReplyOverride = (_, command) => command switch
        {
            "CLUSTER SLOTS" => topology,
            "EXEC" => Committed,
            _ when command.StartsWith("SET ", StringComparison.Ordinal) => Queued,
            _ => FakeRespServer.OkReply,
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 2, UseCluster = true, CommandTimeout = Timeout,
            ReconnectPolicy = withReconnectPolicy ? new() : null,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        var keys = new Dictionary<int, string>();
        for (var index = 0; keys.Count < 2 && index < 32; index++)
        {
            var key = "route-" + index;
            await client.GetStringAsync(key);
            keys[server.ReceivedConnectionIds[^1]] = key;
        }
        await Assert.That(keys.Count).IsEqualTo(2);
        var held = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.SuppressReply = command =>
        {
            if (command != "CLUSTER SLOTS") return false;
            held.TrySetResult(server.ReceivedConnectionIds[^1]);
            return true;
        };
        var router = client.Core.Cluster!;
        var refresh = router.GetPrimaryEndpointsAsync(default).AsTask();
        var heldConnection = await held.Task.WaitAsync(Limit);
        try
        {
            // A refresh publishes only when complete. Existing connected owners remain
            // usable meanwhile; select the other socket so the held reply cannot block FIFO.
            var key = keys.Single(pair => pair.Key != heldConnection).Value;
            var slot = ClusterHash.GetSlot(key);
            var normal = await client.AcquireConnectionAsync(slot, default);
            var acquisition = new CommandAcquisitionScope(default,
                CommandDeadline.After((long)Timeout.TotalMilliseconds), Timeout);
            try
            {
                var timed = await client.AcquireConnectionAsync(slot, ref acquisition);
                await Assert.That(timed).IsSameReferenceAs(normal);
                await Assert.That(acquisition.HasCancellation).IsFalse();
            }
            finally { acquisition.Dispose(); }
            await using var transaction = client.CreateTransaction();
            var pending = transaction.Set(key, "v");
            await transaction.CommitAsync().AsTask().WaitAsync(Limit);
            await Assert.That(pending.Result).IsTrue();
            await Assert.That(refresh.IsCompleted).IsFalse();
        }
        finally
        {
            server.SuppressReply = null;
            await server.SendRawAsync(topology, heldConnection);
            await refresh.WaitAsync(Limit);
        }
    }

    [Test]
    public async Task AcquiredImportGateStillChecksExpiredDeadline()
    {
        await using var server = new FakeRespServer(20, FakeRespServer.OkReply);
        await using var client = await ConnectAsync(server.Port, Timeout, credentialProvider: new ExpiringCredentials());
        await using var session = await client.Hashes.CreateImportSessionAsync();
        using var gate = await session.Connection.AcquireCredentialSequenceAsync(default);
        var expired = CommandDeadline.At(Environment.TickCount64 - 1);
        var acquisition = new CommandAcquisitionScope(default, expired, Timeout);
        _ = acquisition.Token;
        acquisition.Dispose();
        await Assert.That(acquisition.HasCancellation).IsFalse();
        var error = await Assert.That(() => acquisition.CheckDeadline("MULTI/EXEC", client.Core, session.Connection))
            .ThrowsExactly<RespireTimeoutException>();
        await Assert.That(error!.Diagnostics.Stage).IsEqualTo(RespireCommandStage.WaitingForCapacity);
        await Assert.That(server.ReceivedCommands.Contains("MULTI")).IsFalse();
        await Assert.That(expired.RemainingMilliseconds).IsEqualTo(0);
        await Assert.That(expired.Relax(1).RemainingMilliseconds).IsEqualTo(0);
        await Assert.That(CommandDeadline.None.RemainingMilliseconds).IsEqualTo(long.MaxValue);
    }

    [Test, NotInParallel]
    public async Task SmallTransactionTransportAddsNoAllocationForCommandTimeout()
    {
        await using var timedServer = CreateCommitServer();
        await using var untimedServer = CreateCommitServer();
        await using var timed = await RespireConnection.ConnectAsync("127.0.0.1", timedServer.Port,
            new RespireConnectionOptions { Protocol = RespProtocol.Resp2, CommandTimeout = Timeout });
        await using var untimed = await RespireConnection.ConnectAsync("127.0.0.1", untimedServer.Port,
            new RespireConnectionOptions { Protocol = RespProtocol.Resp2, CommandTimeout = null });
        var body = "*3\r\n$3\r\nSET\r\n$1\r\nk\r\n$1\r\nv\r\n"u8.ToArray();
        // Measure atomic MULTI/SET/EXEC admission, excluding transaction construction,
        // reply waits, and async-builder pool misses on other completion threads. The wire
        // result is checked for every commit; the public API's token boundary is checked above.
        Task<(long Timed, long Untimed, long Control)> measurement;
        // Keep the test runner's ambient state outside the synchronous measurement worker.
        using (ExecutionContext.SuppressFlow()) measurement = Task.Run(() =>
        {
            using var completed = new ManualResetEventSlim();
            Action signal = completed.Set;
            MeasureStarts(timed, body, completed, signal, false);
            MeasureStarts(untimed, body, completed, signal, false);
            MeasureStarts(timed, body, completed, signal, true);
            return AllocationMeasurement.WithoutConcurrentGc(() => (
                Timed: MeasureStarts(timed, body, completed, signal, false),
                Untimed: MeasureStarts(untimed, body, completed, signal, false),
                Control: MeasureStarts(timed, body, completed, signal, true)));
        });
        var measured = await measurement;
        Console.WriteLine($"Transaction admission allocations (32 commits): timed={measured.Timed}, untimed={measured.Untimed}, control={measured.Control}.");
        await Assert.That(measured.Timed).IsEqualTo(measured.Untimed);
        await Assert.That(measured.Control).IsGreaterThanOrEqualTo(measured.Timed + 32 * 37);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureStarts(RespireConnection connection, byte[] body,
        ManualResetEventSlim completed, Action signal, bool control)
    {
        long allocated = 0;
        for (var i = 0; i < 32; i++)
        {
            completed.Reset();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var commit = connection.SendTransactionAsync(body, commandCount: 1);
            if (control) GC.KeepAlive(new byte[37]);
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            var awaiter = commit.GetAwaiter();
            if (!awaiter.IsCompleted)
            {
                awaiter.UnsafeOnCompleted(signal);
                if (!completed.Wait(Limit)) throw new TimeoutException("Commit allocation fixture did not reply.");
            }
            using var reply = awaiter.GetResult();
            var results = reply.AsArray();
            if (results.Length != 1 || results[0].AsString() != "OK")
                throw new InvalidOperationException("The measured transaction did not commit SET.");
        }
        return allocated;
    }

    private static FakeRespServer CreateCommitServer() => new()
    {
        ReplyOverride = (_, command) => command switch
        {
            "SET k v" => Queued,
            "EXEC" => Committed,
            _ => FakeRespServer.OkReply,
        },
    };

    private static ValueTask<RespireClient> ConnectAsync(int port, TimeSpan? timeout, bool cluster = false,
        IRespireCredentialProvider? credentialProvider = null)
        => RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, CommandTimeout = timeout, UseCluster = cluster,
            CredentialProvider = credentialProvider,
            Endpoints = [new("127.0.0.1", port)],
        });

    private sealed class ExpiringCredentials : IRespireCredentialProvider
    {
        public ValueTask<RespireCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default)
            => new(new RespireCredentials("default", "test-password", DateTimeOffset.UtcNow.AddHours(1)));
    }

    private static InflightRing GetRing(RespireConnection connection)
        => connection.InspectForTests().Inflight;

    private static async Task<bool> CommitAsync(RespireTransaction transaction)
    {
        await transaction.CommitAsync();
        return true;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var limit = new CancellationTokenSource(Limit);
        while (!condition()) await Task.Delay(1, limit.Token);
    }
}
