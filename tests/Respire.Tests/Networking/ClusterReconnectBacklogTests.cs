using System.Net.Sockets;
using System.Text;
using Respire.Infrastructure;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClusterReconnectBacklogTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly byte[] ValueReply = "$5\r\nvalue\r\n"u8.ToArray();

    [Test]
    [Arguments("string")]
    [Arguments("bytes")]
    [Arguments("typed")]
    [Arguments("stream")]
    [Arguments("catalog")]
    [Arguments("raw")]
    [Arguments("write")]
    [Arguments("fire-and-forget")]
    public async Task PublicCommandsWaitForRecoveryWithoutCommandRetries(string surface)
    {
        await using var fixture = await Fixture.CreateAsync();
        var operation = ExecuteAsync(fixture.Client, surface, "{queue}key");
        await fixture.Connecting.Task.WaitAsync(Limit);
        await Assert.That(operation.IsCompleted).IsFalse();
        await Assert.That(fixture.Server.ReceivedCommands.Any(IsApplicationCommand)).IsFalse();
        await fixture.ReleaseAsync();
        await Assert.That(await operation.WaitAsync(Limit)).IsEqualTo(surface is "write" or "fire-and-forget" ? "OK" : "value");
        await Assert.That(await fixture.Client.GetStringAsync("{queue}barrier")).IsEqualTo("value");
        await Assert.That(fixture.Server.ReceivedCommands.Count(command => command.Contains("{queue}key", StringComparison.Ordinal)))
            .IsEqualTo(1);
    }

    [Test]
    [Arguments(false, false, false)]
    [Arguments(false, true, false)]
    [Arguments(true, false, false)]
    [Arguments(true, true, false)]
    [Arguments(false, false, true)]
    [Arguments(false, true, true)]
    [Arguments(true, false, true)]
    [Arguments(true, true, true)]
    public async Task GroupsQueueWhenInitialAcquisitionLosesConnectedRoute(bool transaction, bool retry, bool keyless)
    {
        await using var fixture = await Fixture.CreateAsync(disconnectOnCreate: false,
            retryPolicy: retry ? new RespireRetryPolicy { MaxAttempts = 1 } : null);
        var router = fixture.Client.Core.Cluster!;
        var connection = fixture.Node.GetConnection();
        var raced = false;
        router.BacklogAdmissionCheckedForTests = shouldBacklog =>
        {
            router.BacklogAdmissionCheckedForTests = null;
            if (shouldBacklog) throw new InvalidOperationException("The initial route must be healthy.");
            raced = true;
#pragma warning disable RESP009 // Inject the disconnect synchronously between the precheck and acquisition.
            connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
#pragma warning restore RESP009
        };
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{fixture.Server.Port}\r\n");
        fixture.Server.ReplyOverride = (_, command) => command switch
        {
            "CLUSTER SLOTS" => keyless ? topology : "*0\r\n"u8.ToArray(),
            "MULTI" => FakeRespServer.OkReply,
            "EXEC" => keyless ? "*1\r\n+OK\r\n"u8.ToArray() : "*1\r\n$5\r\nvalue\r\n"u8.ToArray(),
            "FLUSHDB" => transaction ? "+QUEUED\r\n"u8.ToArray() : FakeRespServer.OkReply,
            _ when command.StartsWith("GET ", StringComparison.Ordinal) => transaction
                ? "+QUEUED\r\n"u8.ToArray() : ValueReply,
            _ => null,
        };
        using var batch = fixture.Client.CreateBatch();
        await using var tx = fixture.Client.CreateTransaction();
        IRespireCommandQueue queue = transaction ? tx : batch;
        var get = keyless ? default : transaction ? tx.GetString("{queue}race") : batch.GetString("{queue}race");
        var flush = keyless ? queue.Server.FlushDatabase() : default;
        var pending = transaction ? CommitAsync(tx) : batch.ExecuteAsync().AsTask();
        await fixture.Connecting.Task.WaitAsync(Limit);
        await Assert.That(raced).IsTrue();
        await Assert.That(pending.IsCompleted).IsFalse();
        await Assert.That(router.IsBacklogDraining(keyless ? null : ClusterHash.GetSlot("{queue}race"))).IsTrue();
        await Assert.That(fixture.Server.ReceivedCommands.Any(IsApplicationCommand)).IsFalse();
        await fixture.ReleaseAsync();
        await pending.WaitAsync(Limit);
#pragma warning disable RESP002 // Both execution branches were awaited through pending above.
        if (keyless) await Assert.That(flush!.Result).IsTrue();
        else await Assert.That(get!.Result).IsEqualTo("value");
#pragma warning restore RESP002
        await Assert.That(fixture.Server.ReceivedCommands.Count(command => command == (keyless ? "FLUSHDB" : "GET {queue}race"))).IsEqualTo(1);
    }

    [Test]
    public async Task RecoveryQueuesAfterInitialDiscoveryUsedFallbackSeed()
    {
        await using var fixture = await Fixture.CreateAsync(failPrimarySeed: true);
        var pending = fixture.Client.GetStringAsync("{queue}fallback").AsTask();
        await fixture.Connecting.Task.WaitAsync(Limit);
        await Assert.That(pending.IsCompleted).IsFalse();
        await Assert.That(fixture.Server.ReceivedCommands.Any(IsApplicationCommand)).IsFalse();
        await fixture.ReleaseAsync();
        await Assert.That(await pending.WaitAsync(Limit)).IsEqualTo("value");
        await Assert.That(fixture.Server.ReceivedCommands.Count(command => command == "GET {queue}fallback")).IsEqualTo(1);
    }

    [Test]
    public async Task UnkeyedCommandWaitsForSeedRecovery()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Server.ReplyOverride = (_, command) => command == "PING" ? FakeRespServer.PongReply : null;
        var pending = fixture.Client.PingAsync().AsTask();
        await fixture.Connecting.Task.WaitAsync(Limit);
        await Assert.That(pending.IsCompleted).IsFalse();
        await Assert.That(fixture.Server.ReceivedCommands.Contains("PING")).IsFalse();
        await fixture.ReleaseAsync();
        await pending.WaitAsync(Limit);
        await Assert.That(fixture.Server.ReceivedCommands.Count(command => command == "PING")).IsEqualTo(1);
    }

    [Test]
    public async Task RecoveryDrainsInOrderAndKeepsLaterArrivalsBehindTheHead()
    {
        await using var fixture = await Fixture.CreateAsync(capacity: 2);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Server.SuppressReply = command =>
        {
            if (command != "GET {queue}first") return false;
            received.TrySetResult();
            return true;
        };
        var first = fixture.Client.GetStringAsync("{queue}first").AsTask();
        await fixture.Connecting.Task.WaitAsync(Limit);
        var second = fixture.Client.GetStringAsync("{queue}second").AsTask();
        await Assert.That(async () => await fixture.Client.GetStringAsync("{queue}overflow").AsTask().WaitAsync(Limit))
            .ThrowsExactly<RespireConnectionException>();
        await fixture.ReleaseAsync();
        await received.Task.WaitAsync(Limit);
        var third = fixture.Client.GetStringAsync("{queue}third").AsTask();
        await Assert.That(fixture.Server.ReceivedCommands.Where(IsApplicationCommand).ToArray())
            .IsEquivalentTo(new[] { "GET {queue}first" }, CollectionOrdering.Matching);
        await fixture.Server.SendRawAsync(ValueReply, 1);
        await Task.WhenAll(first, second, third).WaitAsync(Limit);
        await Assert.That(fixture.Server.ReceivedCommands.Where(IsApplicationCommand).ToArray())
            .IsEquivalentTo(new[] { "GET {queue}first", "GET {queue}second", "GET {queue}third" }, CollectionOrdering.Matching);
    }

    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CancellationAndTimeoutReleaseCapacityWithoutSending(bool cancelCaller)
    {
        await using var fixture = await Fixture.CreateAsync(capacity: 1,
            commandTimeout: cancelCaller ? Limit : TimeSpan.FromMilliseconds(250));
        using var caller = new CancellationTokenSource();
        var pending = fixture.Client.GetStringAsync("{queue}unsent", caller.Token).AsTask();
        await fixture.Connecting.Task.WaitAsync(Limit);
        if (cancelCaller)
        {
            await caller.CancelAsync();
            var error = await Assert.That(async () => await pending.WaitAsync(Limit)).Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
        }
        else
        {
            var error = await Assert.That(async () => await pending.WaitAsync(Limit)).ThrowsExactly<RespireTimeoutException>();
            await Assert.That(error!.Diagnostics.Stage).IsEqualTo(RespireCommandStage.Connecting);
        }
        var next = fixture.Client.GetStringAsync("{queue}next").AsTask();
        await fixture.ReleaseAsync();
        await Assert.That(await next.WaitAsync(Limit)).IsEqualTo("value");
        await Assert.That(fixture.Server.ReceivedCommands.Where(IsApplicationCommand).ToArray())
            .IsEquivalentTo(new[] { "GET {queue}next" }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task QueuedWritesSnapshotBorrowedArguments(bool fireAndForget)
    {
        await using var fixture = await Fixture.CreateAsync();
        var bytes = "value"u8.ToArray();
        async Task SendAsync()
        {
            if (fireAndForget) await fixture.Client.ExecuteFireAndForgetAsync("SET", ["{queue}key", bytes]);
            else { using var reply = await fixture.Client.ExecuteAsync("SET", ["{queue}key", bytes]); }
        }
        var pending = SendAsync();
        await fixture.Connecting.Task.WaitAsync(Limit);
        Array.Fill(bytes, (byte)'x');
        await fixture.ReleaseAsync();
        await pending.WaitAsync(Limit);
        await fixture.Client.GetStringAsync("{queue}barrier");
        await Assert.That(fixture.Server.ReceivedCommands.Where(IsApplicationCommand).ToArray())
            .IsEquivalentTo(new[] { "SET {queue}key value", "GET {queue}barrier" }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("string")]
    [Arguments("bytes")]
    [Arguments("raw")]
    public async Task ResumedExecutionRetainsOriginalDeadline(string surface)
    {
        await using var fixture = await Fixture.CreateAsync();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Server.SuppressReply = command =>
        {
            if (command != "GET {queue}pending") return false;
            received.TrySetResult();
            return true;
        };
        var earliest = Environment.TickCount64;
        var pending = ExecuteAsync(fixture.Client, surface, "{queue}pending");
        var latest = Environment.TickCount64;
        await fixture.Connecting.Task.WaitAsync(Limit);
        // Distinguish admission and dispatch ticks; inspect the deadline directly.
        await Task.Delay(25);
        await fixture.ReleaseAsync();
        await received.Task.WaitAsync(Limit);
        var connection = fixture.Node.GetConnection();
        var ring = connection.InspectForTests().Inflight;
        await Assert.That(ring.TryPeek(out var source)).IsTrue();
        await Assert.That(source.Deadline.Ticks).IsGreaterThanOrEqualTo(earliest + (long)Limit.TotalMilliseconds);
        await Assert.That(source.Deadline.Ticks).IsLessThanOrEqualTo(latest + (long)Limit.TotalMilliseconds);
        ring.SweepExpired(source.Deadline.Ticks + 1, Limit, connection);
        await Assert.That(async () => await pending.WaitAsync(Limit)).ThrowsExactly<RespireTimeoutException>();
        await fixture.Server.SendRawAsync(ValueReply, 1);
        await Assert.That(await fixture.Client.GetStringAsync("{queue}healthy")).IsEqualTo("value");
    }

    [Test]
    public async Task FailFastRejectsUnsentCommand()
    {
        await using var fixture = await Fixture.CreateAsync(policy: RespireBacklogPolicy.FailFast);
        await Assert.That(async () => await fixture.Client.GetStringAsync("{queue}rejected").AsTask().WaitAsync(Limit))
            .Throws<RespireConnectionException>();
        await Assert.That(fixture.Server.ReceivedCommands.Any(IsApplicationCommand)).IsFalse();
    }

    [Test]
    public async Task DisposalFailsEveryQueuedCommand()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = fixture.Client.GetStringAsync("{queue}first").AsTask();
        await fixture.Connecting.Task.WaitAsync(Limit);
        var second = fixture.Client.SetAsync("{queue}second", "value").AsTask();
        await fixture.Client.DisposeAsync();
        await Assert.That(async () => await first.WaitAsync(Limit)).Throws<ObjectDisposedException>();
        await Assert.That(async () => await second.WaitAsync(Limit)).Throws<ObjectDisposedException>();
        await Assert.That(fixture.Server.ReceivedCommands.Any(IsApplicationCommand)).IsFalse();
    }

    [Test]
    public async Task ExhaustionFailsQueuedCommandsAndFurtherAdmission()
    {
        await using var fixture = await Fixture.CreateAsync(reconnectPolicy: new()
        {
            InitialDelay = TimeSpan.Zero, JitterRatio = 0, MaxAttempts = 1,
        });
        var first = fixture.Client.GetStringAsync("{queue}first").AsTask();
        await fixture.Connecting.Task.WaitAsync(Limit);
        var second = fixture.Client.SetAsync("{queue}second", "value").AsTask();
        fixture.Server.ReplyOverride = (_, command) => command == "AUTH test"
            ? "-ERR replacement failed\r\n"u8.ToArray() : null;
        fixture.Resume.TrySetResult();
        await Assert.That(async () => await first.WaitAsync(Limit)).ThrowsExactly<RespireReconnectLimitException>();
        await Assert.That(async () => await second.WaitAsync(Limit)).ThrowsExactly<RespireReconnectLimitException>();
        await Assert.That(async () => await fixture.Client.GetStringAsync("{queue}later").AsTask().WaitAsync(Limit))
            .ThrowsExactly<RespireReconnectLimitException>();
        await Assert.That(fixture.Server.ReceivedCommands.Any(IsApplicationCommand)).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BatchAndTransactionWaitAsOneCapacityUnit(bool transaction)
    {
        await using var fixture = await Fixture.CreateAsync(capacity: 1);
        var inTransaction = false;
        fixture.Server.ReplyOverride = (_, command) =>
        {
            if (command == "MULTI") { inTransaction = true; return FakeRespServer.OkReply; }
            if (command == "EXEC") { inTransaction = false; return "*2\r\n$5\r\nvalue\r\n+OK\r\n"u8.ToArray(); }
            if (inTransaction) return "+QUEUED\r\n"u8.ToArray();
            return command.StartsWith("GET ", StringComparison.Ordinal) ? ValueReply : FakeRespServer.OkReply;
        };
        using var batch = transaction ? null : fixture.Client.CreateBatch();
        await using var commit = transaction ? fixture.Client.CreateTransaction() : null;
        var first = transaction ? commit!.GetString("{queue}first") : batch!.GetString("{queue}first");
        var second = transaction ? commit!.Set("{queue}second", "value") : batch!.Set("{queue}second", "value");
        var execution = transaction ? CommitAsync(commit!) : batch!.ExecuteAsync().AsTask();
        await fixture.Connecting.Task.WaitAsync(Limit);
        await Assert.That(execution.IsCompleted).IsFalse();
        await Assert.That(fixture.Server.ReceivedCommands.Any(IsApplicationCommand)).IsFalse();
        await Assert.That(async () => await fixture.Client.GetStringAsync("{queue}overflow").AsTask().WaitAsync(Limit))
            .ThrowsExactly<RespireConnectionException>();
        await fixture.ReleaseAsync();
        await execution.WaitAsync(Limit);
#pragma warning disable RESP002 // Execution completed before reading either member.
        await Assert.That(await first).IsEqualTo("value");
        await Assert.That(await second).IsTrue();
#pragma warning restore RESP002
        await Assert.That(fixture.Server.ReceivedCommands.Where(IsApplicationCommand).ToArray())
            .IsEquivalentTo(transaction
                ? new[] { "MULTI", "GET {queue}first", "SET {queue}second value", "EXEC" }
                : new[] { "GET {queue}first", "SET {queue}second value" }, CollectionOrdering.Matching);
    }

    [Test]
    [NotInParallel]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task BatchAndTransactionCancellationOrTimeoutFailsEveryUnsentMember(bool transaction, bool cancelCaller)
    {
        await using var fixture = await Fixture.CreateAsync(commandTimeout: cancelCaller ? Limit : TimeSpan.FromMilliseconds(250));
        using var caller = new CancellationTokenSource();
        using var batch = transaction ? null : fixture.Client.CreateBatch();
        await using var commit = transaction ? fixture.Client.CreateTransaction() : null;
        var first = transaction ? commit!.GetString("{queue}first") : batch!.GetString("{queue}first");
        var second = transaction ? commit!.Set("{queue}second", "value") : batch!.Set("{queue}second", "value");
        var execution = transaction ? CommitAsync(commit!, caller.Token) : batch!.TryExecuteAsync(caller.Token).AsTask();
        await fixture.Connecting.Task.WaitAsync(Limit);
        if (cancelCaller) await caller.CancelAsync();
        if (transaction)
        {
            if (cancelCaller)
                await Assert.That(async () => await execution.WaitAsync(Limit)).Throws<OperationCanceledException>();
            else
                await Assert.That(async () => await execution.WaitAsync(Limit)).ThrowsExactly<RespireTimeoutException>();
        }
        else await execution.WaitAsync(Limit);
        if (cancelCaller)
        {
            await Assert.That(first.Error).IsTypeOf<OperationCanceledException>();
            await Assert.That(((OperationCanceledException)first.Error!).CancellationToken).IsEqualTo(caller.Token);
        }
        else
        {
            await Assert.That(first.Error).IsTypeOf<RespireTimeoutException>();
            await Assert.That(((RespireTimeoutException)first.Error!).Diagnostics.Stage).IsEqualTo(RespireCommandStage.Connecting);
        }
        await Assert.That(second.Error).IsSameReferenceAs(first.Error);
        await Assert.That(fixture.Server.ReceivedCommands.Any(IsApplicationCommand)).IsFalse();
        await fixture.ReleaseAsync();
        await Assert.That(await fixture.Client.GetStringAsync("{queue}healthy")).IsEqualTo("value");
    }

    [Test]
    public async Task ExplicitBatchRetriesRemainInsideQueuedUnit()
    {
        await using var fixture = await Fixture.CreateAsync(retryPolicy: new()
        {
            InitialBackoff = TimeSpan.Zero, MaxAttempts = 2,
        });
        var attempts = 0;
        fixture.Server.ReplyOverride = (_, command) => command switch
        {
            "GET {queue}retry" => Interlocked.Increment(ref attempts) == 1 ? "-TRYAGAIN retry\r\n"u8.ToArray() : ValueReply,
            "GET {queue}successful" or "GET {queue}later" => ValueReply,
            _ => null,
        };
        using var batch = fixture.Client.CreateBatch();
        var retry = batch.GetString("{queue}retry");
        var successful = batch.GetString("{queue}successful");
        var execution = batch.ExecuteAsync().AsTask();
        await fixture.Connecting.Task.WaitAsync(Limit);
        var later = fixture.Client.GetStringAsync("{queue}later").AsTask();
        await fixture.ReleaseAsync();
        await execution.WaitAsync(Limit);
#pragma warning disable RESP002 // Batch execution has completed.
        await Assert.That(await retry).IsEqualTo("value");
        await Assert.That(await successful).IsEqualTo("value");
#pragma warning restore RESP002
        await Assert.That(await later.WaitAsync(Limit)).IsEqualTo("value");
        var commands = fixture.Server.ReceivedCommands.Where(IsApplicationCommand).ToArray();
        await Assert.That(commands.Count(command => command == "GET {queue}retry")).IsEqualTo(2);
        // Explicit safe-unit retries intentionally repeat every member; the backlog
        // must retain ownership until that unit finishes before sending later work.
        await Assert.That(commands.Count(command => command == "GET {queue}successful")).IsEqualTo(2);
        await Assert.That(commands[^1]).IsEqualTo("GET {queue}later");
    }

    [Test]
    public async Task CircuitOpenedDuringRecoveryRejectsQueuedDispatch()
    {
        await using var fixture = await Fixture.CreateAsync(circuit: true);
        var pending = fixture.Client.GetStringAsync("{queue}rejected").AsTask();
        await fixture.Connecting.Task.WaitAsync(Limit);
        var admission = fixture.Client.Core.Circuits!.Acquire(new("127.0.0.1", fixture.Server.Port), default);
        try { admission.Failed(new RespireConnectionException("Injected node failure."), default); }
        finally { admission.Dispose(); }
        fixture.Resume.TrySetResult();
        await Assert.That(async () => await pending.WaitAsync(Limit)).ThrowsExactly<RespireCircuitOpenException>();
        await Assert.That(fixture.Server.ReceivedCommands.Any(IsApplicationCommand)).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task StrictReplicaRecoveryNeverSendsReadToPrimary(bool cancelCaller)
    {
        await using var replica = new FakeRespServer(4, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal) ? ValueReply : null,
        };
        await using var primary = new FakeRespServer(4, FakeRespServer.OkReply);
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? Encoding.ASCII.GetBytes(
            $"*1\r\n*4\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{primary.Port}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{replica.Port}\r\n") : null;
        var connecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateReplacement = false;
#pragma warning disable RESP010 // Each wire test requires its own isolated cluster and recovery gate.
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true, Endpoints = [new("127.0.0.1", primary.Port)], Connections = 1,
            Protocol = RespireProtocol.Resp2, CommandTimeout = Limit, ConnectTimeout = Limit,
            ClusterTopologyRefreshInterval = null, RetryPolicy = null,
            TestingStreamFactory = async (host, port, token) =>
            {
                if (port == replica.Port && Volatile.Read(ref gateReplacement))
                {
                    connecting.TrySetResult();
                    await resume.Task.WaitAsync(token);
                }
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(host, port, token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            },
        });
#pragma warning restore RESP010
        var reads = client.WithReadFrom(RespireReadFrom.Replica);
        await Assert.That(await reads.GetStringAsync("{queue}warm")).IsEqualTo("value");
        var connection = await client.Core.Cluster!.GetReadConnectionAsync(ClusterHash.GetSlot("{queue}warm"),
            RespireReadFrom.Replica, default, default);
        Volatile.Write(ref gateReplacement, true);
        await connection.DisposeAsync();
        using var caller = new CancellationTokenSource();
        var pending = reads.GetStringAsync("{queue}pending", caller.Token).AsTask();
        await connecting.Task.WaitAsync(Limit);
        await Assert.That(pending.IsCompleted).IsFalse();
        if (cancelCaller)
        {
            await caller.CancelAsync();
            await Assert.That(async () => await pending.WaitAsync(Limit)).Throws<OperationCanceledException>();
        }
        resume.TrySetResult();
        if (!cancelCaller) await Assert.That(await pending.WaitAsync(Limit)).IsEqualTo("value");
        await Assert.That(primary.ReceivedCommands.Any(command => command.StartsWith("GET ", StringComparison.Ordinal))).IsFalse();
        await Assert.That(replica.ReceivedCommands.Count(command => command == "GET {queue}pending")).IsEqualTo(cancelCaller ? 0 : 1);
    }

    [Test]
    public async Task SlotsSharingARecoveringOwnerHaveIndependentCapacity()
    {
        await using var fixture = await Fixture.CreateAsync(capacity: 1);
        fixture.Client.Core.Cluster!.SetSlotOwner(ClusterHash.GetSlot("{other}key"), fixture.Node);
        var first = fixture.Client.GetStringAsync("{queue}first").AsTask();
        await fixture.Connecting.Task.WaitAsync(Limit);
        var other = fixture.Client.GetStringAsync("{other}key").AsTask();
        await Assert.That(other.IsCompleted).IsFalse();
        await Assert.That(async () => await fixture.Client.GetStringAsync("{queue}overflow").AsTask().WaitAsync(Limit))
            .ThrowsExactly<RespireConnectionException>();
        await fixture.ReleaseAsync();
        await Assert.That(await first.WaitAsync(Limit)).IsEqualTo("value");
        await Assert.That(await other.WaitAsync(Limit)).IsEqualTo("value");
    }

    [Test]
    public async Task UnavailableSlotDoesNotBlockAHealthyOwner()
    {
        await using var fixture = await Fixture.CreateAsync(capacity: 1);
        await using var healthy = new FakeRespServer(4, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal) ? ValueReply : null,
        };
        var router = fixture.Client.Core.Cluster!;
        var healthyNode = router.GetMultiplexer(new("127.0.0.1", healthy.Port));
        // Establish the second owner before queuing work; the fixture's dial gate is
        // deliberately scoped to the recovering server below.
        await healthyNode.EnsureConnectedAsync(default);
        router.SetSlotOwner(ClusterHash.GetSlot("{healthy}key"), healthyNode);
        var pending = fixture.Client.GetStringAsync("{queue}pending").AsTask();
        await fixture.Connecting.Task.WaitAsync(Limit);
        await Assert.That(await fixture.Client.GetStringAsync("{healthy}key").AsTask().WaitAsync(Limit)).IsEqualTo("value");
        await Assert.That(pending.IsCompleted).IsFalse();
        await fixture.ReleaseAsync();
        await Assert.That(await pending.WaitAsync(Limit)).IsEqualTo("value");
    }

    [Test]
    public async Task QueuedCommandResolvesSlotOwnerAgainBeforeSending()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var replacement = new FakeRespServer(4, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal) ? ValueReply : null,
        };
        var pending = fixture.Client.GetStringAsync("{queue}moved").AsTask();
        await fixture.Connecting.Task.WaitAsync(Limit);
        var router = fixture.Client.Core.Cluster!;
        router.SetSlotOwner(ClusterHash.GetSlot("{queue}moved"), router.GetMultiplexer(new("127.0.0.1", replacement.Port)));
        fixture.Resume.TrySetResult();
        await Assert.That(await pending.WaitAsync(Limit)).IsEqualTo("value");
        await Assert.That(fixture.Server.ReceivedCommands.Contains("GET {queue}moved")).IsFalse();
        await Assert.That(replacement.ReceivedCommands.Count(command => command == "GET {queue}moved")).IsEqualTo(1);
    }

    [Test]
    [Arguments("string")]
    [Arguments("write")]
    public async Task AcceptedCommandWithLostReplyIsNeverReplayed(string surface)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Resume.TrySetResult();
        await fixture.Node.GetHealthyConnectionAsync(default);
        fixture.Server.CloseConnectionAfterCommand = fixture.Server.CommandsSeen + 1;
        await Assert.That(async () => await ExecuteAsync(fixture.Client, surface, "{queue}lost").WaitAsync(Limit))
            .Throws<RespireConnectionException>();
        await Assert.That(await fixture.Client.GetStringAsync("{queue}next").AsTask().WaitAsync(Limit)).IsEqualTo("value");
        await Assert.That(fixture.Server.ReceivedCommands.Count(command => command.Contains("{queue}lost", StringComparison.Ordinal)))
            .IsEqualTo(1);
    }

    [Test]
    public async Task TrackedReadWaitsForRecoveryAndPopulatesCache()
    {
        await using var fixture = await Fixture.CreateAsync(cache: true);
        var pending = fixture.Client.GetStringAsync("{queue}cached").AsTask();
        await fixture.Connecting.Task.WaitAsync(Limit);
        await Assert.That(pending.IsCompleted).IsFalse();
        await fixture.ReleaseAsync();
        await Assert.That(await pending.WaitAsync(Limit)).IsEqualTo("value");
        await Assert.That(await fixture.Client.GetStringAsync("{queue}cached")).IsEqualTo("value");
        await Assert.That(fixture.Server.ReceivedCommands.Count(command => command == "GET {queue}cached")).IsEqualTo(1);
        await Assert.That(fixture.Server.ReceivedCommands.Contains("CLIENT CACHING YES")).IsTrue();
    }

    [Test]
    [Arguments(false, false, false)]
    [Arguments(false, false, true)]
    [Arguments(false, true, false)]
    [Arguments(false, true, true)]
    [Arguments(true, false, false)]
    [Arguments(true, false, true)]
    [Arguments(true, true, false)]
    [Arguments(true, true, true)]
    public async Task QueuedKeylessFlushReselectsSlotOwningPrimary(bool transaction, bool retry, bool all)
    {
        await using var fixture = await Fixture.CreateAsync(retryPolicy: retry ? new RespireRetryPolicy() : null);
        await using var primary = new FakeRespServer(8, FakeRespServer.OkReply);
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{primary.Port}\r\n");
        primary.ReplyOverride = (_, command) => command switch
        {
            "CLUSTER SLOTS" => topology,
            "EXEC" => "*1\r\n+OK\r\n"u8.ToArray(),
            "FLUSHDB" or "FLUSHALL" when transaction => "+QUEUED\r\n"u8.ToArray(),
            _ => null,
        };
        fixture.Server.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? topology : null;
        using var batch = fixture.Client.CreateBatch();
        await using var tx = fixture.Client.CreateTransaction();
        IRespireCommandQueue queue = transaction ? tx : batch;
        var flush = all ? queue.Server.FlushAll() : queue.Server.FlushDatabase();
        var pending = transaction ? CommitAsync(tx) : batch.ExecuteAsync().AsTask();
        await fixture.Connecting.Task.WaitAsync(Limit);
        await Assert.That(pending.IsCompleted).IsFalse();
        await fixture.ReleaseAsync();
        await pending.WaitAsync(Limit);
        await Assert.That(flush.Result).IsTrue();
        var command = all ? "FLUSHALL" : "FLUSHDB";
        await Assert.That(primary.ReceivedCommands.Count(value => value == command)).IsEqualTo(1);
        await Assert.That(fixture.Server.ReceivedCommands.Contains(command)).IsFalse();
    }

    private static async Task<string> ExecuteAsync(RespireClient client, string surface, string key)
    {
        switch (surface)
        {
            case "string": return (await client.GetStringAsync(key))!;
            case "bytes": return Encoding.UTF8.GetString((await client.GetBytesAsync(key))!);
            case "typed": return (await client.GetAsync<string>(key))!;
            case "stream":
                await using (var stream = await client.Strings.GetStreamAsync(key))
                using (var reader = new StreamReader(stream!)) return await reader.ReadToEndAsync();
            case "write": return await client.SetAsync(key, "value") ? "OK" : "failed";
            case "fire-and-forget": await client.ExecuteFireAndForgetAsync("SET", key, "value"); return "OK";
            default:
                using (var reply = surface == "catalog"
                    ? await client.ExecuteAsync(RespireCommands.String.GET, key)
                    : await client.ExecuteAsync("GET", key)) return reply.AsString()!;
        }
    }

    private static async Task CommitAsync(RespireTransaction transaction, CancellationToken token = default)
        => await transaction.CommitAsync(token);

    private static bool IsApplicationCommand(string command) => !command.StartsWith("AUTH ", StringComparison.Ordinal)
        && !command.StartsWith("HELLO ", StringComparison.Ordinal) && !command.StartsWith("CLIENT ", StringComparison.Ordinal)
        && command != "CLUSTER SLOTS";

    private sealed class Fixture(FakeRespServer server, RespireClient client, RespireConnectionMultiplexer node,
        TaskCompletionSource connecting, TaskCompletionSource resume) : IAsyncDisposable
    {
        internal FakeRespServer Server { get; } = server;
        internal RespireClient Client { get; } = client;
        internal RespireConnectionMultiplexer Node { get; } = node;
        internal TaskCompletionSource Connecting { get; } = connecting;
        internal TaskCompletionSource Resume { get; } = resume;

        internal static async Task<Fixture> CreateAsync(int capacity = 1024, TimeSpan? commandTimeout = null,
            RespireBacklogPolicy policy = RespireBacklogPolicy.Queue, RespireReconnectPolicy? reconnectPolicy = null,
            bool cache = false, bool circuit = false, RespireRetryPolicy? retryPolicy = null, bool failPrimarySeed = false,
            bool disconnectOnCreate = true)
        {
            var server = new FakeRespServer(8, FakeRespServer.OkReply)
            {
                ReplyOverride = (_, command) => command switch
                {
                    "CLUSTER SLOTS" => "*0\r\n"u8.ToArray(),
                    "HELLO 3 AUTH default test" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
                    _ => command.StartsWith("GET ", StringComparison.Ordinal) ? ValueReply : null,
                },
            };
            try
            {
                await using var rejectedSeed = failPrimarySeed
                    ? new FakeRespServer("-ERR first seed unavailable\r\n"u8.ToArray()) : null;
                var connecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var gateReplacement = false;
                var client = await RespireClient.ConnectAsync(new RespireOptions
                {
                    UseCluster = true, AllowAdmin = true, Endpoints = rejectedSeed is null ? [new("127.0.0.1", server.Port)]
                        : [new("127.0.0.1", rejectedSeed.Port), new("127.0.0.1", server.Port)], Connections = 1,
                    Protocol = cache ? RespireProtocol.Resp3 : RespireProtocol.Resp2, Password = "test",
                    ConnectTimeout = Limit, CommandTimeout = commandTimeout ?? Limit, RetryPolicy = retryPolicy,
                    BacklogCapacity = capacity, BacklogPolicy = policy, ReconnectPolicy = reconnectPolicy,
                    ClientSideCache = cache ? new() : null,
                    CircuitBreaker = circuit ? new() { MinimumFailureCount = 1, FailureRateThreshold = 0.01,
                        OpenDuration = TimeSpan.FromMinutes(1) } : null,
                    ClusterTopologyRefreshInterval = null,
                    TestingStreamFactory = async (host, port, token) =>
                    {
                        if (port == server.Port && Volatile.Read(ref gateReplacement))
                        {
                            connecting.TrySetResult();
                            await resume.Task.WaitAsync(token);
                        }
                        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                        try
                        {
                            await socket.ConnectAsync(host, port, token);
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                        catch { socket.Dispose(); throw; }
                    },
                });
                var node = client.Core.Cluster!.GetMultiplexer(new("127.0.0.1", server.Port));
                client.Core.Cluster.SetSlotOwner(ClusterHash.GetSlot("{queue}key"), node);
                Volatile.Write(ref gateReplacement, true);
                if (disconnectOnCreate) await node.GetConnection().DisposeAsync();
                return new(server, client, node, connecting, resume);
            }
            catch { await server.DisposeAsync(); throw; }
        }

        internal async Task ReleaseAsync()
        {
            Resume.TrySetResult();
            using var deadline = new CancellationTokenSource(Limit);
            await Node.GetHealthyConnectionAsync(deadline.Token);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Server.DisposeAsync();
        }
    }
}
