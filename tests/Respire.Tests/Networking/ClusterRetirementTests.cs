using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Respire.Commands;
using Respire.Infrastructure;
using Respire.Internal;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClusterRetirementTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    [Test]
    public async Task RetirementSnapshotsAreOwnedAndRequireALiveClient()
    {
        await using var standalone = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new RespireEndpoint("seed.invalid")],
        });
        await Assert.That(standalone.GetClusterRetirementSnapshot()).IsNull();
        await using var client = CreateClient();
        var snapshot = client.GetClusterRetirementSnapshot()!;
        await Assert.That(snapshot.RetiringGenerationCount).IsEqualTo(0);
        await Assert.That(snapshot.OldestRetirementAge).IsEqualTo(TimeSpan.Zero);
        await Assert.That(snapshot.PendingCorrectionFenceCount).IsEqualTo(0);
        var view = (RespireClient)client.WithKeyPrefix("tenant:");
        await Assert.That(view.GetClusterRetirementSnapshot()!.RetiringGenerationCount).IsEqualTo(0);
        await view.DisposeAsync();
        await Assert.That(client.GetClusterRetirementSnapshot()).IsNotNull();
        await client.DisposeAsync();
        await Assert.That(() => client.GetClusterRetirementSnapshot()).ThrowsExactly<ObjectDisposedException>();
        await Assert.That(() => view.GetClusterRetirementSnapshot()).ThrowsExactly<ObjectDisposedException>();
        await Assert.That(snapshot.RetiringGenerationCount).IsEqualTo(0);
    }

    [Test]
    [Arguments("keyed")]
    [Arguments("unkeyed")]
    [Arguments("dedicated")]
    public async Task RouteAcquisitionRetriesAnUnpublishedRetiredHandshake(string path)
    {
        await using var target = new FakeRespServer(path == "dedicated" ? 2 : 1, FakeRespServer.PongReply);
        await using var client = CreateClient();
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("127.0.0.1", target.Port);
        Publish(router, endpoint, "old", 1);
        var old = router.GetMultiplexer(endpoint);
        var gate = (SemaphoreSlim)typeof(RespireConnectionMultiplexer).GetField("_connectGate", Private)!.GetValue(old)!;
        using var timeout = new CancellationTokenSource(Limit);
        await gate.WaitAsync(timeout.Token);
        Task<RespireConnection>? connectionTask = null;
        Task<DedicatedConnectionPool>? poolTask = null;
        try
        {
            if (path == "dedicated") poolTask = router.GetDedicatedPoolAsync(42, timeout.Token).AsTask();
            else connectionTask = router.GetConnectionAsync(path == "keyed" ? 42 : null, timeout.Token).AsTask();
            // The old node has not created a socket or accepted any application command.
            Publish(router, endpoint, "new", 2);
        }
        finally { gate.Release(); }
        DedicatedConnectionPool? pool = poolTask is null ? null : await poolTask.WaitAsync(timeout.Token);
        var connection = pool is null ? await connectionTask!.WaitAsync(timeout.Token) : await pool.RentAsync(timeout.Token);
        try
        {
            using var reply = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame), timeout.Token);
            await Assert.That(reply.AsString()).IsEqualTo("PONG");
            await Assert.That(target.ReceivedCommands).IsEquivalentTo(["PING"]);
            await Assert.That(ReferenceEquals(old, router.GetMultiplexer(endpoint))).IsFalse();
        }
        finally { pool?.Return(connection); }
        await router.WaitForRetirementAsync().WaitAsync(timeout.Token);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task ClusterWideRetirementRetriesOnlyTheRejectedTarget(bool fireAndForget)
        => AssertFanOutRetirementAsync(fireAndForget ? "fire-forget" : "raw");

    [Test]
    [Arguments("function")]
    [Arguments("script")]
    [Arguments("flush")]
    [Arguments("size")]
    [Arguments("scan")]
    public Task FacetFanOutRetirementRetriesOnlyTheRejectedTarget(string path)
        => AssertFanOutRetirementAsync(path);

    private static async Task AssertFanOutRetirementAsync(string path)
    {
        var commandName = path switch
        {
            "script" => "SCRIPT FLUSH", "flush" => "FLUSHDB", "size" => "DBSIZE",
            "scan" => "SCAN 0 COUNT 250", _ => "FUNCTION FLUSH",
        };
        var commandReply = path switch
        {
            "size" => ":1\r\n"u8.ToArray(),
            "scan" => "*2\r\n$1\r\n0\r\n*1\r\n$3\r\nkey\r\n"u8.ToArray(),
            _ => FakeRespServer.OkReply,
        };
        var topologyRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstAccepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFull = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAccepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pingCount = 0;
        await using var first = new FakeRespServer
        {
            SuppressReply = command =>
            {
                if (command == "CLUSTER SLOTS") topologyRequested.TrySetResult();
                else if (command == commandName) firstAccepted.TrySetResult();
                return true;
            },
        };
        await using var second = new FakeRespServer(2, commandReply)
        {
            SuppressReply = command =>
            {
                if (command != "PING") { secondAccepted.TrySetResult(); return false; }
                if (Interlocked.Increment(ref pingCount) == 4) secondFull.TrySetResult();
                return true;
            },
        };
        await using var client = CreateClient(maxInflightCommands: 4, allowAdmin: true);
        using var timeout = new CancellationTokenSource(Limit);
        var router = client.Core.Cluster!;
        PublishTargets("old", 1);
        var old = await router.GetConnectionAsync(9000, timeout.Token);
        var accepted = Enumerable.Range(0, 4)
            .Select(_ => old.SendAsync(new RawCommand(FakeRespServer.PingFrame), timeout.Token).AsTask()).ToArray();
        await secondFull.Task.WaitAsync(timeout.Token);

        var pending = SendAsync();
        await topologyRequested.Task.WaitAsync(timeout.Token);
        var topology = System.Text.Encoding.ASCII.GetBytes(
            $"*2\r\n*3\r\n:0\r\n:8191\r\n*3\r\n$9\r\n127.0.0.1\r\n:{first.Port}\r\n$5\r\nfirst\r\n" +
            $"*3\r\n:8192\r\n:16383\r\n*3\r\n$9\r\n127.0.0.1\r\n:{second.Port}\r\n$3\r\nold\r\n");
        await first.SendRawAsync(topology);
        await firstAccepted.Task.WaitAsync(timeout.Token);
        PublishTargets("new", 2);
        await first.SendRawAsync(commandReply);
        await pending.WaitAsync(timeout.Token);
        await secondAccepted.Task.WaitAsync(timeout.Token);

        await Assert.That(first.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS", commandName]);
        await Assert.That(second.ReceivedCommands).IsEquivalentTo(["PING", "PING", "PING", "PING", commandName]);
        await Assert.That(second.ReceivedConnectionIds).IsEquivalentTo([0, 0, 0, 0, 1]);
        await Assert.That(accepted.All(task => !task.IsCompleted)).IsTrue();
        await second.SendRawAsync("+PONG\r\n+PONG\r\n+PONG\r\n+PONG\r\n"u8.ToArray(), 0);
        foreach (var task in accepted)
        {
            using var reply = await task.WaitAsync(timeout.Token);
            await Assert.That(reply.AsString()).IsEqualTo("PONG");
        }
        await router.WaitForRetirementAsync().WaitAsync(timeout.Token);

        async Task SendAsync()
        {
            switch (path)
            {
                case "raw":
                    using (var reply = await client.ExecuteAsync($"FUNCTION FLUSH", cancellationToken: timeout.Token))
                        await Assert.That(reply.AsString()).IsEqualTo("OK");
                    break;
                case "fire-forget": await client.ExecuteFireAndForgetAsync($"FUNCTION FLUSH", timeout.Token); break;
                case "function": await client.Functions.FlushAsync(cancellationToken: timeout.Token); break;
                case "script": await client.Scripts.FlushAsync(cancellationToken: timeout.Token); break;
                case "flush": await client.Server.FlushDatabaseAsync(timeout.Token); break;
                case "scan":
                    var keys = new List<string>();
                    await foreach (var key in client.Keys.ScanAsync(cancellationToken: timeout.Token)) keys.Add(key);
                    await Assert.That(keys).IsEquivalentTo(["key", "key"]);
                    break;
                default: await Assert.That(await client.Server.DatabaseSizeAsync(timeout.Token)).IsEqualTo(2); break;
            }
        }
        void PublishTargets(string secondId, long generation)
        {
            var version = (long)typeof(ClusterRouter).GetField("_topologyVersion", Private)!.GetValue(router)!;
            List<ClusterTopologyRange> ranges =
            [
                new(0, 8191, new("127.0.0.1", first.Port), "first", []),
                new(8192, 16383, new("127.0.0.1", second.Port), secondId, []),
            ];
            typeof(ClusterRouter).GetMethod("ApplyTopology", Private)!.Invoke(router, [ranges, version, generation]);
        }
    }

    [Test]
    [Arguments("ordinary")]
    [Arguments("no-redirect")]
    [Arguments("tracked")]
    [Arguments("fire-forget")]
    [Arguments("batch")]
    [Arguments("transaction")]
    public async Task RejectedCommandRetriesWithoutReplayingAcceptedWork(string path)
    {
        var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acceptedCount = 0;
        byte[][] replies = path == "transaction"
            ? [FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), "*1\r\n+OK\r\n"u8.ToArray()]
            : [FakeRespServer.OkReply];
        await using var server = new FakeRespServer(2, replies)
        {
            SuppressReply = command =>
            {
                if (command != "PING") { retried.TrySetResult(); return false; }
                if (Interlocked.Increment(ref acceptedCount) == 4) full.TrySetResult();
                return true;
            },
        };
        await using var client = CreateClient(maxInflightCommands: 4, allowAdmin: true);
        using var timeout = new CancellationTokenSource(Limit);
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("127.0.0.1", server.Port);
        Publish(router, endpoint, "old", 1);
        var old = await router.GetConnectionAsync(42, timeout.Token);
        var accepted = Enumerable.Range(0, 4)
            .Select(_ => old.SendAsync(new RawCommand(FakeRespServer.PingFrame), timeout.Token).AsTask()).ToArray();
        await full.Task.WaitAsync(timeout.Token);

        var retry = SendAsync();
        await Assert.That(retry.IsCompleted).IsFalse();
        Publish(router, endpoint, "new", 2);
        await retry.WaitAsync(timeout.Token);
        await retried.Task.WaitAsync(timeout.Token);
        await Assert.That(accepted.All(task => !task.IsCompleted)).IsTrue();
        await Assert.That(server.ReceivedCommands.Take(4)).IsEquivalentTo(["PING", "PING", "PING", "PING"]);
        await Assert.That(server.ReceivedConnectionIds.Take(4)).IsEquivalentTo([0, 0, 0, 0]);
        await Assert.That(server.ReceivedConnectionIds.Skip(4).All(id => id == 1)).IsTrue();
        string[] expected = path switch
        {
            "tracked" => ["CLIENT CACHING YES", "SET key value"],
            "fire-forget" => ["SHUTDOWN NOSAVE"],
            "transaction" => ["MULTI", "SET key value", "EXEC"],
            _ => ["SET key value"],
        };
        await Assert.That(server.ReceivedCommands.Skip(4)).IsEquivalentTo(expected);
        await server.SendRawAsync("+PONG\r\n+PONG\r\n+PONG\r\n+PONG\r\n"u8.ToArray(), 0);
        foreach (var task in accepted)
        {
            using var reply = await task.WaitAsync(timeout.Token);
            await Assert.That(reply.AsString()).IsEqualTo("PONG");
        }
        await router.WaitForRetirementAsync().WaitAsync(timeout.Token);

        async Task SendAsync()
        {
            switch (path)
            {
                case "tracked":
                    var rebased = 0;
                    Action<bool> onRedirect = allowInsert => { if (allowInsert) rebased++; };
                    var method = typeof(RespireClient).GetMethod("SendTrackedClusterAsync", Private)!.MakeGenericMethod(typeof(Cmd2));
                    var pending = (ValueTask<Respire.Protocol.RespValue>)method.Invoke(client,
                        ["SET", router, new Cmd2(RespireCommands.String.SET.Verb, "key", "value"), timeout.Token, onRedirect])!;
                    using (var reply = await pending) await Assert.That(reply.AsString()).IsEqualTo("OK");
                    await Assert.That(rebased).IsEqualTo(1);
                    break;
                case "fire-forget":
                    await client.ExecuteFireAndForgetAsync($"SHUTDOWN NOSAVE", timeout.Token);
                    break;
                case "batch":
                    var batch = client.CreateBatch();
                    var batched = batch.Set("key", "value");
                    await batch.ExecuteAsync(timeout.Token);
                    await Assert.That(batched.Result).IsTrue();
                    break;
                case "transaction":
                    await using (var transaction = client.CreateTransaction())
                    {
                        var committed = transaction.Set("key", "value");
                        await transaction.CommitAsync(timeout.Token);
                        await Assert.That(committed.Result).IsTrue();
                    }
                    break;
                default:
                    using (var reply = await client.ExecuteAsync(RespireCommands.String.SET, ["key", "value"],
                        path == "no-redirect" ? RespireCommandFlags.NoRedirect : RespireCommandFlags.None, timeout.Token))
                        await Assert.That(reply.AsString()).IsEqualTo("OK");
                    break;
            }
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task RetiredPoolSelectionRetriesBeforeRentAndKeepsItsOwner(bool asking, bool reuseIdle)
    {
        await using var oldServer = new FakeRespServer(3, FakeRespServer.PongReply);
        await using var newServer = new FakeRespServer(2, FakeRespServer.PongReply);
        await using var client = CreateClient();
        using var timeout = new CancellationTokenSource(Limit);
        var router = client.Core.Cluster!;
        var oldEndpoint = new RespireEndpoint("127.0.0.1", oldServer.Port);
        Publish(router, oldEndpoint, "old", 1);
        var source = await router.GetConnectionAsync(42, timeout.Token);
        using (var ready = await source.SendAsync(new RawCommand(FakeRespServer.PingFrame), timeout.Token))
            await Assert.That(ready.AsString()).IsEqualTo("PONG");
        var selected = await router.GetDedicatedPoolAsync(42, timeout.Token);
        Publish(router, new("127.0.0.1", newServer.Port), "new", 2);
        await Assert.That(selected.IsStopping).IsTrue();
        // The ASK error originates at the slot owner, not at the temporary target.
        var redirectSource = await router.GetConnectionAsync(42, timeout.Token);
        var (pool, connection) = await router.RentDedicatedConnectionAsync(
            selected, 42, timeout.Token, reuseIdle,
            asking ? new RespireServerException($"ASK 42 127.0.0.1:{oldServer.Port}") : null, redirectSource);
        try
        {
            await Assert.That(ReferenceEquals(pool, selected)).IsFalse();
            await Assert.That(connection.Port).IsEqualTo(asking ? oldServer.Port : newServer.Port);
            using var reply = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame), timeout.Token);
            await Assert.That(reply.AsString()).IsEqualTo("PONG");
            var owner = await router.GetConnectionAsync(42, timeout.Token);
            await Assert.That(owner.Port).IsEqualTo(newServer.Port);
        }
        catch (OperationCanceledException error)
        {
            throw new TimeoutException($"Rent target {connection.Port}; old wire: {string.Join(", ", oldServer.ReceivedCommands)}; "
                + $"old connections: {string.Join(", ", oldServer.ReceivedConnectionIds)}; "
                + $"new wire: {string.Join(", ", newServer.ReceivedCommands)}", error);
        }
        finally { pool.Return(connection); }
        await router.WaitForRetirementAsync().WaitAsync(timeout.Token);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RejectedAskCommandKeepsTemporaryTarget(bool tracked)
    {
        var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pings = 0;
        await using var target = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (command != "PING") return false;
                if (Interlocked.Increment(ref pings) == 4) full.TrySetResult();
                return true;
            },
        };
        var slot = ClusterHash.GetSlot("key");
        var ask = System.Text.Encoding.ASCII.GetBytes($"-ASK {slot} 127.0.0.1:{target.Port}\r\n");
        await using var source = new FakeRespServer(tracked ? [FakeRespServer.OkReply, ask] : [ask]);
        await using var client = CreateClient(maxInflightCommands: 4);
        using var timeout = new CancellationTokenSource(Limit);
        var router = client.Core.Cluster!;
        var sourceEndpoint = new RespireEndpoint("127.0.0.1", source.Port);
        Publish(router, sourceEndpoint, "source", 1);
        _ = await router.GetConnectionAsync(slot, timeout.Token);
        var oldTarget = router.GetMultiplexer(new("127.0.0.1", target.Port));
        await oldTarget.EnsureConnectedAsync(timeout.Token);
        var old = oldTarget.GetConnection();
        var accepted = Enumerable.Range(0, 4)
            .Select(_ => old.SendAsync(new RawCommand(FakeRespServer.PingFrame), timeout.Token).AsTask()).ToArray();
        await full.Task.WaitAsync(timeout.Token);
        var command = new Cmd2(RespireCommands.String.SET.Verb, "key", "value");
        var rebased = new List<bool>();
        Task<Respire.Protocol.RespValue> pending;
        if (tracked)
        {
            var method = typeof(RespireClient).GetMethod("SendTrackedClusterAsync", Private)!.MakeGenericMethod(typeof(Cmd2));
            Action<bool> onRedirect = rebased.Add;
            pending = ((ValueTask<Respire.Protocol.RespValue>)method.Invoke(client,
                ["SET", router, command, timeout.Token, onRedirect])!).AsTask();
        }
        else pending = client.SendAsync("SET", command, timeout.Token).AsTask();
        var signal = typeof(RespireConnection).GetField("_capacitySignal", Private)!.GetValue(old)!;
        var waiters = signal.GetType().GetField("_waiters", Private)!;
        while (waiters.GetValue(signal) is null)
        {
            if (pending.IsCompleted) { using var unexpected = await pending; throw new InvalidOperationException("ASK did not reach the full target queue."); }
            await Task.Delay(1, timeout.Token);
        }
        // The source stays the slot owner; only its temporary ASK target is detached.
        Publish(router, sourceEndpoint, "source", 2);
        using (var reply = await pending.WaitAsync(timeout.Token))
            await Assert.That(reply.AsString()).IsEqualTo("OK");
        await Assert.That(oldTarget.IsRetired).IsTrue();
        await Assert.That(target.ReceivedCommands.Skip(4)).IsEquivalentTo(["ASKING", "SET key value"]);
        await Assert.That(source.ReceivedCommands.Count(value => value == "SET key value")).IsEqualTo(1);
        await Assert.That((await router.GetConnectionAsync(slot, timeout.Token)).Port).IsEqualTo(source.Port);
        if (tracked) await Assert.That(rebased).IsEquivalentTo([false, false]);
        await target.SendRawAsync("+PONG\r\n+PONG\r\n+PONG\r\n+PONG\r\n"u8.ToArray(), 0);
        foreach (var task in accepted) { using var reply = await task.WaitAsync(timeout.Token); }
        await router.WaitForRetirementAsync().WaitAsync(timeout.Token);
    }

    [Test]
    [Arguments("retire")]
    [Arguments("cancel")]
    [Arguments("dispose")]
    public async Task DedicatedHandshakeRetriesOnlyRetirement(string action)
    {
        var handshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var oldServer = new FakeRespServer(FakeRespServer.OkReply)
        {
            SuppressReply = command => { handshake.TrySetResult(); return true; },
        };
        await using var replacement = new FakeRespServer(2, FakeRespServer.OkReply);
        await using var client = CreateClient();
        using var timeout = new CancellationTokenSource(Limit);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var router = client.Core.Cluster!;
        // Use a dedicated pool with a parked SELECT handshake; the router only sends
        // application commands after rent succeeds and returns the owning pool.
        await using var selected = new DedicatedConnectionPool("127.0.0.1", oldServer.Port,
            new RespireConnectionOptions { Database = 1 }, null);
        Publish(router, new("127.0.0.1", replacement.Port), "new", 1);
        var pending = router.RentDedicatedConnectionAsync(selected, 42, caller.Token).AsTask();
        await handshake.Task.WaitAsync(timeout.Token);
        if (action == "cancel") caller.Cancel();
        if (action == "dispose") await client.DisposeAsync();
        await selected.RetireAsync();
        if (action != "retire")
        {
            await Assert.That(async () => await pending.WaitAsync(timeout.Token)).Throws<OperationCanceledException>();
            await Assert.That(replacement.CommandsSeen).IsEqualTo(0);
            return;
        }
        var (pool, connection) = await pending.WaitAsync(timeout.Token);
        try
        {
            using var reply = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame), timeout.Token);
            await Assert.That(reply.AsString()).IsEqualTo("OK");
            await Assert.That(connection.Port).IsEqualTo(replacement.Port);
            await Assert.That(ReferenceEquals(selected, pool)).IsFalse();
        }
        finally { pool.Return(connection); }
        await Assert.That(oldServer.ReceivedCommands).IsEquivalentTo(["SELECT 1"]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RejectedTrackedExecutionPublishesNewIdentityBeforeWriting(bool script)
    {
        var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pings = 0;
        await using var oldServer = new FakeRespServer(":41\r\n"u8.ToArray(), ":0\r\n"u8.ToArray())
        {
            SuppressReply = command =>
            {
                if (command != "PING") return false;
                if (Interlocked.Increment(ref pings) == 4) full.TrySetResult();
                return true;
            },
        };
        Func<RespireClient.TrackedConnectionIdentity>? currentIdentity = null;
        RespireClient.TrackedConnectionIdentity? identityAtWrite = null;
        await using var newServer = new FakeRespServer(":42\r\n"u8.ToArray(), ":0\r\n"u8.ToArray(), ":1\r\n"u8.ToArray())
        {
            SuppressReply = command =>
            {
                if (command.StartsWith("EVALSHA ", StringComparison.Ordinal) || command.StartsWith("DELEX ", StringComparison.Ordinal))
                    identityAtWrite = currentIdentity!();
                return false;
            },
        };
        await using var client = CreateClient(maxInflightCommands: 4);
        using var timeout = new CancellationTokenSource(Limit);
        var router = client.Core.Cluster!;
        Publish(router, new("127.0.0.1", oldServer.Port), "old", 1);
        var old = await router.GetTrackedConnectionAsync(42, true, timeout.Token);
        var accepted = Enumerable.Range(0, 4)
            .Select(_ => old.SendAsync(new RawCommand(FakeRespServer.PingFrame), timeout.Token).AsTask()).ToArray();
        await full.Task.WaitAsync(timeout.Token);
        Task operation;
        if (script)
        {
            var execution = await client.StartTrackedScriptExecutionAsync(
                RespireScript.Create("return 1"), ["key"], [], timeout.Token, true);
            currentIdentity = () => execution.ConnectionIdentity;
            operation = CompleteScriptAsync(execution.Response);
        }
        else
        {
            var execution = await client.StartLockExecutionAsync("key", "token", null, true, timeout.Token);
            currentIdentity = () => execution.ConnectionIdentity;
            operation = execution.Response.AsTask();
        }
        await Assert.That(operation.IsCompleted).IsFalse();
        Publish(router, new("127.0.0.1", newServer.Port), "new", 2);
        await operation.WaitAsync(timeout.Token);
        await Assert.That(identityAtWrite.HasValue).IsTrue();
        await Assert.That(identityAtWrite!.Value.ServerClientId).IsEqualTo(42);
        await Assert.That(identityAtWrite.Value.Endpoint.Port).IsEqualTo(newServer.Port);
        await Assert.That(ReferenceEquals(identityAtWrite.Value.Connection, old)).IsFalse();
        await Assert.That(newServer.ReceivedCommands.Count).IsEqualTo(3);
        await Assert.That(oldServer.ReceivedCommands.Skip(2)).IsEquivalentTo(["PING", "PING", "PING", "PING"]);
        await oldServer.SendRawAsync("+PONG\r\n+PONG\r\n+PONG\r\n+PONG\r\n"u8.ToArray(), 0);
        foreach (var task in accepted) { using var reply = await task.WaitAsync(timeout.Token); }
        await router.WaitForRetirementAsync().WaitAsync(timeout.Token);

        static async Task CompleteScriptAsync(ValueTask<RespireResult> response)
        {
            using var result = await response;
            await Assert.That(result.AsInteger()).IsEqualTo(1);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReplacementDrainsAcceptedWorkAndKeepsNewGeneration(bool dedicated)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = 0;
        await using var server = new FakeRespServer(4, FakeRespServer.PongReply)
        {
            SuppressReply = _ => { if (Interlocked.Increment(ref commands) != 1) return false; received.TrySetResult(); return true; },
        };
        await using var client = CreateClient();
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("127.0.0.1", server.Port);
        Publish(router, endpoint, "old", 1);
        var old = router.GetMultiplexer(endpoint);
        await old.EnsureConnectedAsync();
        var pool = router.GetDedicatedPool(endpoint);
        var connection = dedicated ? await pool.RentAsync(default) : old.GetConnection();
        var pending = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await received.Task.WaitAsync(Limit);
        Publish(router, endpoint, "new", 2);
        var current = router.GetMultiplexer(endpoint);
        await Assert.That(ReferenceEquals(old, current)).IsFalse();
        await Assert.That(old.IsRetired).IsTrue();
        await Assert.That(connection.IsConnected).IsTrue();
        await Assert.That(router.WaitForRetirementAsync().IsCompleted).IsFalse();
        await Assert.That(ReferenceEquals(pool, router.GetDedicatedPool(endpoint))).IsFalse();
        var snapshot = client.GetClusterRetirementSnapshot()!;
        await Assert.That(snapshot.RetiringGenerationCount).IsEqualTo(1);
        await Assert.That(snapshot.BorrowedDedicatedConnectionCount).IsEqualTo(dedicated ? 1L : 0L);
        await Assert.That(snapshot.PendingCorrectionFenceCount).IsEqualTo(0);
        await Assert.That(snapshot.CleanupFailedGenerationCount).IsEqualTo(0);
        if (!dedicated) await Assert.That(snapshot.UndrainedGenerationCount).IsEqualTo(1);
        await current.EnsureConnectedAsync();
        using (var reply = await current.SendAsync(new RawCommand(FakeRespServer.PingFrame)))
            await Assert.That(reply.AsString()).IsEqualTo("PONG");
        await server.SendRawAsync(FakeRespServer.PongReply, dedicated ? 1 : 0);
        using (var reply = await pending.WaitAsync(Limit))
            await Assert.That(reply.AsString()).IsEqualTo("PONG");
        if (dedicated) pool.Return(connection);
        await router.WaitForRetirementAsync().WaitAsync(Limit);
        await Assert.That(connection.IsConnected).IsFalse();
        await Assert.That(ReferenceEquals(current, router.GetMultiplexer(endpoint))).IsTrue();
        await Assert.That(Count(router, "_retiringNodes")).IsEqualTo(0);
        await Assert.That(RetainedNodes(router)).IsEqualTo(2);
        await Assert.That(client.GetClusterRetirementSnapshot()!.RetiringGenerationCount).IsEqualTo(0);
        await Assert.That(client.GetClusterRetirementSnapshot()!.OldestRetirementAge).IsEqualTo(TimeSpan.Zero);
        await Assert.That(snapshot.RetiringGenerationCount).IsEqualTo(1); // Owned historical observation.
    }

    [Test]
    public async Task RepeatedCompletedChurnBoundsIndexesHandlersAndPools()
    {
        await using var server = new FakeRespServer(32, FakeRespServer.PongReply);
        await using var client = CreateClient();
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("127.0.0.1", server.Port);
        for (var generation = 1; generation <= 12; generation++)
        {
            Publish(router, endpoint, $"node-{generation}", generation);
            await router.WaitForRetirementAsync().WaitAsync(Limit);
            var node = router.GetMultiplexer(endpoint);
            await node.EnsureConnectedAsync();
            var pool = router.GetDedicatedPool(endpoint);
            using (var ready = await node.SendAsync(new RawCommand(FakeRespServer.PingFrame)))
                await Assert.That(ready.AsString()).IsEqualTo("PONG");
            var borrowed = await pool.RentAsync(default);
            using (var ready = await borrowed.SendAsync(new RawCommand(FakeRespServer.PingFrame)))
                await Assert.That(ready.AsString()).IsEqualTo("PONG");
            pool.Return(borrowed);
            await using var correction = router.GetCorrectionLease(node.GetConnection());
            await Assert.That(RetainedNodes(router)).IsEqualTo(2);
            await Assert.That(Count(router, "_nodeStateHandlers")).IsEqualTo(1);
            await Assert.That(Count(router, "_dedicatedPools")).IsEqualTo(1);
            await Assert.That(Count(router, "_correctionPools")).IsEqualTo(1);
            await Assert.That(Count(router, "_correctionStateHandlers")).IsEqualTo(1);
            await Assert.That(Count(router, "_ownedPools")).IsEqualTo(2);
            var identities = Identities(router);
            await Assert.That(identities.NodeIdCount).IsEqualTo(1);
            await Assert.That(identities.ReverseNodeIdCount).IsEqualTo(1);
            await Assert.That(identities.Endpoints.Count()).IsEqualTo(3); // seed, preferred, advertised alias
            await Assert.That(client.GetClusterRetirementSnapshot()!.RetiringGenerationCount).IsEqualTo(0);
        }
        await Assert.That(client.Core.Multiplexer.IsRetired).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ClientDisposalAbortsRetiringAcceptedWork(bool dedicated)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(2, FakeRespServer.PongReply)
        {
            SuppressReply = _ => { received.TrySetResult(); return true; },
        };
        await using var client = CreateClient();
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("127.0.0.1", server.Port);
        Publish(router, endpoint, "old", 1);
        var node = router.GetMultiplexer(endpoint);
        await node.EnsureConnectedAsync();
        var pool = router.GetDedicatedPool(endpoint);
        var connection = dedicated ? await pool.RentAsync(default) : node.GetConnection();
        var pending = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await received.Task.WaitAsync(Limit);
        Publish(router, endpoint, "new", 2);
        var snapshot = client.GetClusterRetirementSnapshot()!;
        await client.DisposeAsync().AsTask().WaitAsync(Limit);
        await Assert.That(async () => await pending).Throws<RespireConnectionException>();
        await router.WaitForRetirementAsync().WaitAsync(Limit);
        await Assert.That(connection.IsConnected).IsFalse();
        await Assert.That(snapshot.RetiringGenerationCount).IsEqualTo(1);
        await Assert.That(() => client.GetClusterRetirementSnapshot()).ThrowsExactly<ObjectDisposedException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RedirectRetriesGenerationRetiredDuringConnectionSetup(bool dedicated)
    {
        await using var sourceServer = new FakeRespServer(FakeRespServer.PongReply);
        await using var source = await RespireConnection.ConnectAsync("127.0.0.1", sourceServer.Port);
        using (var ready = await source.SendAsync(new RawCommand(FakeRespServer.PingFrame)))
            await Assert.That(ready.AsString()).IsEqualTo("PONG");
        await using var target = new FakeRespServer(2, FakeRespServer.PongReply);
        await using var client = CreateClient();
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("127.0.0.1", target.Port);
        Publish(router, endpoint, "old", 1);
        var old = router.GetMultiplexer(endpoint);
        var gate = (SemaphoreSlim)typeof(RespireConnectionMultiplexer).GetField("_connectGate", Private)!.GetValue(old)!;
        using var timeout = new CancellationTokenSource(Limit);
        await gate.WaitAsync(timeout.Token);
        Task<RespireConnection>? connectionTask = null;
        Task<DedicatedConnectionPool>? poolTask = null;
        try
        {
            var moved = new RespireServerException($"MOVED 42 127.0.0.1:{target.Port}");
            if (dedicated) poolTask = router.GetRedirectDedicatedPoolAsync(moved, source, timeout.Token).AsTask();
            else connectionTask = router.GetRedirectConnectionAsync(moved, source, timeout.Token).AsTask();
            // Redirect setup is parked inside the old generation's connection gate.
            Publish(router, endpoint, "new", 2);
        }
        finally
        {
            gate.Release();
        }

        var current = router.GetMultiplexer(endpoint);
        await Assert.That(ReferenceEquals(old, current)).IsFalse();
        await Assert.That(old.IsRetired).IsTrue();
        DedicatedConnectionPool? pool = dedicated ? await poolTask!.WaitAsync(timeout.Token) : null;
        var connection = pool is null ? await connectionTask!.WaitAsync(timeout.Token) : await pool.RentAsync(timeout.Token);
        try
        {
            using var reply = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame), timeout.Token);
            await Assert.That(reply.AsString()).IsEqualTo("PONG");
            await Assert.That(ReferenceEquals(current, router.GetMultiplexer(endpoint))).IsTrue();
        }
        finally
        {
            pool?.Return(connection);
        }
        await router.WaitForRetirementAsync().WaitAsync(timeout.Token);
    }

    [Test]
    public async Task FailedNodeCleanupBeforeDrainRemainsOwnedAndFaultsRetirement()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        using var logger = new FailingPoolDisconnectLogger(failNodeDisconnect: true);
        await using var client = CreateClient(logger);
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("127.0.0.1", server.Port);
        Publish(router, endpoint, "old", 1);
        var old = router.GetMultiplexer(endpoint);
        await old.EnsureConnectedAsync();
        using (var ready = await old.SendAsync(new RawCommand(FakeRespServer.PingFrame)))
            await Assert.That(ready.AsString()).IsEqualTo("PONG");
        Publish(router, endpoint, "new", 2);
        try
        {
            var error = await Assert.That(async () => await router.WaitForRetirementAsync().WaitAsync(Limit))
                .ThrowsExactly<InvalidOperationException>();
            await Assert.That(error).IsSameReferenceAs(logger.Failure);
            await Assert.That(old.RetirementDrained).IsFalse();
            await Assert.That(old.HasPendingCorrectionFences).IsFalse();
            await Assert.That(Count(router, "_retiringNodes")).IsEqualTo(1);
            await Assert.That(RetainedNodes(router)).IsEqualTo(3);
            var snapshot = client.GetClusterRetirementSnapshot()!;
            await Assert.That(snapshot.CleanupFailedGenerationCount).IsEqualTo(1);
            await Assert.That(snapshot.UndrainedGenerationCount).IsEqualTo(1);
            await Assert.That(snapshot.AwaitingFenceGenerationCount).IsEqualTo(0);
        }
        finally
        {
            await Assert.That(async () => await client.DisposeAsync().AsTask().WaitAsync(Limit))
                .ThrowsExactly<InvalidOperationException>();
        }
    }

    [Test]
    public async Task FailedDedicatedCleanupRemainsOwnedAndFaultsGenerationRetirement()
    {
        await using var server = new FakeRespServer(2, FakeRespServer.PongReply);
        using var logger = new FailingPoolDisconnectLogger();
        await using var client = CreateClient(logger);
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("127.0.0.1", server.Port);
        Publish(router, endpoint, "old", 1);
        var node = router.GetMultiplexer(endpoint);
        await node.EnsureConnectedAsync();
        using (var ready = await node.SendAsync(new RawCommand(FakeRespServer.PingFrame)))
            await Assert.That(ready.AsString()).IsEqualTo("PONG");
        var pool = router.GetDedicatedPool(endpoint);
        var borrowed = await pool.RentAsync(default);
        using (var ready = await borrowed.SendAsync(new RawCommand(FakeRespServer.PingFrame)))
            await Assert.That(ready.AsString()).IsEqualTo("PONG");
        Publish(router, endpoint, "new", 2);
        var retirement = router.WaitForRetirementAsync();
        pool.Return(borrowed);
        var error = await Assert.That(async () => await retirement.WaitAsync(Limit)).ThrowsExactly<InvalidOperationException>();
        await Assert.That(error).IsSameReferenceAs(logger.Failure);
        await Assert.That(Count(router, "_retiringNodes")).IsEqualTo(1);
        await Assert.That(Count(router, "_ownedPools")).IsEqualTo(1);
        var snapshot = client.GetClusterRetirementSnapshot()!;
        await Assert.That(snapshot.CleanupFailedGenerationCount).IsEqualTo(1);
        await Assert.That(snapshot.BorrowedDedicatedConnectionCount).IsEqualTo(0);
        await Assert.That(snapshot.AwaitingFenceGenerationCount).IsEqualTo(0);
        await Assert.That(async () => await client.DisposeAsync().AsTask().WaitAsync(Limit)).ThrowsExactly<InvalidOperationException>();
        await Assert.That(borrowed.IsConnected).IsFalse();
    }

    [Test]
    public async Task UnexpectedRetirementFailureStillObservesLatePoolFailure()
    {
        var killSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(3, ":42\r\n"u8.ToArray())
        {
            SuppressReply = command =>
            {
                if (command != "CLIENT KILL ID 42") return false;
                killSeen.TrySetResult();
                return true;
            },
        };
        using var logger = new FailingPoolDisconnectLogger(failRetirementLog: true);
        await using var client = CreateClient(logger);
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("127.0.0.1", server.Port);
        Publish(router, endpoint, "old", 1);
        var node = router.GetMultiplexer(endpoint);
        await node.EnsureConnectedAsync();
        var original = node.GetConnection();
        await original.EnsureServerClientIdAsync();
        var pool = router.GetDedicatedPool(endpoint);
        var borrowed = await pool.RentAsync(default);
        using (var ready = await borrowed.SendAsync(new RawCommand(FakeRespServer.PingFrame)))
            await Assert.That(ready.AsInteger()).IsEqualTo(42);
        await original.DisposeAsync();
        Publish(router, endpoint, "new", 2);
        var retirement = router.WaitForRetirementAsync();
        await killSeen.Task.WaitAsync(Limit);
        await server.SendRawAsync("-ERR unavailable\r\n"u8.ToArray(), 2);
        await logger.RetirementFailureSeen.Task.WaitAsync(Limit);
        pool.Return(borrowed);
        try
        {
            var error = await Assert.That(async () => await retirement.WaitAsync(Limit)).ThrowsExactly<AggregateException>();
            await Assert.That(error!.InnerExceptions).Contains(logger.RetirementFailure);
            await Assert.That(error.InnerExceptions).Contains(logger.Failure);
        }
        finally
        {
            // Explicit disposal completes all owned cleanup, then reports the same stored failure.
            await Assert.That(async () => await client.DisposeAsync().AsTask().WaitAsync(Limit)).Throws<Exception>();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DisposedFenceEntryHasDistinctSignal(bool waitForGate)
    {
        await using var node = RespireConnectionMultiplexer.Create("retired.invalid");
        var gate = (SemaphoreSlim)typeof(RespireConnectionMultiplexer).GetField("_retiredFenceGate", Private)!.GetValue(node)!;
        Task fencing;
        Task disposal;
        if (waitForGate)
        {
            await gate.WaitAsync();
            try
            {
                fencing = node.FenceRetiredConnectionsAsync().AsTask();
                disposal = node.DisposeAsync().AsTask();
            }
            finally
            {
                gate.Release();
            }
        }
        else
        {
            disposal = node.DisposeAsync().AsTask();
            await disposal.WaitAsync(Limit);
            fencing = node.FenceRetiredConnectionsAsync().AsTask();
        }
        await Assert.That(async () => await fencing.WaitAsync(Limit))
            .ThrowsExactly<RespireConnectionMultiplexer.CorrectionFenceDisposedException>();
        await disposal.WaitAsync(Limit);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ShutdownSignalDoesNotHideUnrelatedNodeFailure(bool disposeNode)
    {
        await using var client = CreateClient();
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("retired.invalid");
        Publish(router, endpoint, "old", 1);
        var node = router.GetMultiplexer(endpoint);
        var nodeRetirement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(RespireConnectionMultiplexer).GetField("_retirementCompletion", Private)!.SetValue(node, nodeRetirement);
        var failure = new ObjectDisposedException("unrelated resource");
        try
        {
            Publish(router, endpoint, "new", 2);
            var retirement = router.WaitForRetirementAsync();
            // The unrelated failure stays unexpected even if node disposal wins before
            // the asynchronous retirement continuation observes that failure.
            var stop = (CancellationTokenSource)typeof(ClusterRouter).GetField("_stopRetirement", Private)!.GetValue(router)!;
            stop.Cancel();
            var disposal = disposeNode ? node.DisposeAsync().AsTask() : Task.CompletedTask;
            nodeRetirement.SetException(failure);
            var error = await Assert.That(async () => await retirement.WaitAsync(Limit)).ThrowsExactly<ObjectDisposedException>();
            await Assert.That(error).IsSameReferenceAs(failure);
            await disposal.WaitAsync(Limit);
            await Assert.That(client.GetClusterRetirementSnapshot()!.CleanupFailedGenerationCount).IsEqualTo(1);
            await Assert.That(async () => await client.DisposeAsync().AsTask().WaitAsync(Limit)).ThrowsExactly<ObjectDisposedException>();
        }
        finally
        {
            nodeRetirement.TrySetException(failure);
            _ = nodeRetirement.Task.Exception;
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task DisposalOnlySuppressesExpectedRetiredNodeShutdown(bool disposeFirst, bool unexpectedFailure)
    {
        await using var client = CreateClient();
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("retired.invalid");
        Publish(router, endpoint, "old", 1);
        var node = router.GetMultiplexer(endpoint);
        // Hold the node's retirement completion at the boundary observed in the CI race.
        // Inject its exact terminal error after router disposal starts, without a timing loop.
        var nodeRetirement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(RespireConnectionMultiplexer).GetField("_retirementCompletion", Private)!.SetValue(node, nodeRetirement);
        Exception failure = unexpectedFailure ? new InvalidOperationException("Unexpected retirement failure")
            : new RespireConnectionMultiplexer.CorrectionFenceDisposedException();
        try
        {
            Publish(router, endpoint, "new", 2);
            var retirement = router.WaitForRetirementAsync();
            await Assert.That(retirement.IsCompleted).IsFalse();
            if (disposeFirst)
            {
                var disposal = client.DisposeAsync().AsTask();
                var stop = (CancellationTokenSource)typeof(ClusterRouter).GetField("_stopRetirement", Private)!.GetValue(router)!;
                await Assert.That(stop.IsCancellationRequested).IsTrue();
                await Assert.That(disposal.IsCompleted).IsFalse();
                nodeRetirement.SetException(failure);
                if (unexpectedFailure)
                {
                    var error = await Assert.That(async () => await disposal.WaitAsync(Limit)).ThrowsExactly<InvalidOperationException>();
                    await Assert.That(error).IsSameReferenceAs(failure);
                }
                else
                {
                    await disposal.WaitAsync(Limit);
                    await Assert.That(Count(router, "_retiringNodes")).IsEqualTo(0);
                }
            }
            else
            {
                nodeRetirement.SetException(failure);
                var error = await Assert.That(async () => await retirement.WaitAsync(Limit)).Throws<Exception>();
                await Assert.That(error).IsSameReferenceAs(failure);
                await Assert.That(client.GetClusterRetirementSnapshot()!.CleanupFailedGenerationCount).IsEqualTo(1);
                await Assert.That(async () => await client.DisposeAsync().AsTask().WaitAsync(Limit)).Throws<Exception>();
            }
        }
        finally
        {
            nodeRetirement.TrySetException(failure);
            _ = nodeRetirement.Task.Exception; // Observe even when an earlier assertion failed.
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    public async Task FenceDeadlineIsDistinctFromCallerCancellation(bool cancelCaller, bool blockControlConnection)
    {
        var killSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(2, ":42\r\n"u8.ToArray())
        {
            SuppressReply = command =>
            {
                if (command != "CLIENT KILL ID 42") return false;
                killSeen.TrySetResult();
                return true;
            },
        };
        await using var node = RespireConnectionMultiplexer.Create("unresolvable.invalid",
            options: new RespireConnectionOptions
            {
                ConnectTimeout = cancelCaller ? Limit : TimeSpan.FromMilliseconds(100),
                TestingStreamFactory = blockControlConnection ? async (_, _, token) =>
                {
                    // Expire the fence during connection setup, before CLIENT KILL can be sent.
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return Stream.Null;
                } : null,
            });
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        await connection.EnsureServerClientIdAsync();
        InstallPhysicalConnection(node, connection);
        await connection.DisposeAsync();
        using var cancel = new CancellationTokenSource();
        var fencing = node.FenceRetiredConnectionsAsync(cancel.Token).AsTask();
        if (cancelCaller)
        {
            // Caller cancellation specifically exercises an accepted, unacknowledged kill.
            await killSeen.Task.WaitAsync(Limit);
            cancel.Cancel();
            var error = await Assert.That(async () => await fencing.WaitAsync(Limit)).Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken.IsCancellationRequested).IsTrue();
        }
        else
        {
            // The deadline covers connect, handshake, and reply. It may legitimately win
            // before the server sees CLIENT KILL, so observe the fence rather than receipt.
            var error = await Assert.That(async () => await fencing.WaitAsync(Limit)).ThrowsExactly<RespireTimeoutException>();
            await Assert.That(error!.Message).Contains("CLIENT KILL");
            await Assert.That(cancel.IsCancellationRequested).IsFalse();
        }
        if (blockControlConnection)
            await Assert.That(server.ReceivedCommands).DoesNotContain("CLIENT KILL ID 42");
        await Assert.That(node.HasPendingCorrectionFences).IsTrue();
    }

    [Test]
    public async Task LateFenceOfDrainedSocketDoesNotContactReplacementWithSameClientId()
    {
        await using var server = new FakeRespServer(3, ":42\r\n"u8.ToArray());
        await using var client = CreateClient();
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("127.0.0.1", server.Port);
        Publish(router, endpoint, "old", 1);
        var node = router.GetMultiplexer(endpoint);
        await node.EnsureConnectedAsync();
        var original = node.GetConnection();
        await original.EnsureServerClientIdAsync();
        Publish(router, endpoint, "new", 2);
        await router.WaitForRetirementAsync().WaitAsync(Limit);
        var replacement = router.GetMultiplexer(endpoint);
        await replacement.EnsureConnectedAsync();
        await replacement.GetConnection().EnsureServerClientIdAsync();
        var identity = new RespireClient.TrackedConnectionIdentity(endpoint, 42, Connection: original);
        await client.FenceCorrectionConnectionAsync(identity).AsTask().WaitAsync(Limit);
        await Assert.That(replacement.GetConnection().IsConnected).IsTrue();
        await Assert.That(original.DrainedSuccessfully).IsTrue();
        await Assert.That(server.ReceivedCommands).DoesNotContain("CLIENT KILL ID 42");
        await Assert.That(Count(router, "_correctionPools")).IsEqualTo(0);
        await Assert.That(Count(router, "_ownedPools")).IsEqualTo(0);
    }

    [Test]
    public async Task CorrectionReservationSurvivesTopologyRemovalBeforeRent()
    {
        await using var server = new FakeRespServer(2, FakeRespServer.PongReply);
        await using var client = CreateClient();
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("127.0.0.1", server.Port);
        Publish(router, endpoint, "old", 1);
        var old = router.GetMultiplexer(endpoint);
        await old.EnsureConnectedAsync();
        // Complete a round trip before retiring: TCP connect can finish before the fake
        // listener accepts, and Windows AcceptEx can fail if that socket closes first.
        using (var ready = await old.SendAsync(new RawCommand(FakeRespServer.PingFrame)))
            await Assert.That(ready.AsString()).IsEqualTo("PONG");
        var lease = router.GetCorrectionLease(old.GetConnection());
        Publish(router, endpoint, "new", 2);
        await router.WaitForRetirementAsync().WaitAsync(Limit);
        var control = await lease.Pool.RentAsync(default);
        using (var reply = await control.SendAsync(new RawCommand(FakeRespServer.PingFrame)))
            await Assert.That(reply.AsString()).IsEqualTo("PONG");
        lease.Pool.Return(control);
        await lease.DisposeAsync();
        await Assert.That(control.IsConnected).IsFalse();
        await Assert.That(Count(router, "_ownedPools")).IsEqualTo(0);
    }

    [Test]
    public async Task PhysicalPeerChangesPrunePoolsButSamePeerReconnectReusesPool()
    {
        await using var firstServer = new FakeRespServer(4, ":42\r\n"u8.ToArray());
        await using var secondServer = new FakeRespServer(2, ":42\r\n"u8.ToArray());
        await using var client = CreateClient();
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("dns.invalid");
        Publish(router, endpoint, "stable", 1);
        var node = router.GetMultiplexer(endpoint);
        await using var original = await RespireConnection.ConnectAsync("127.0.0.1", firstServer.Port);
        await original.EnsureServerClientIdAsync();
        InstallPhysicalConnection(node, original);
        var firstLease = router.GetCorrectionLease(original);
        var firstPool = firstLease.Pool;
        var control = await firstPool.RentAsync(default);
        using (var ready = await control.SendAsync(new RawCommand(FakeRespServer.PingFrame)))
            await Assert.That(ready.AsInteger()).IsEqualTo(42);
        firstPool.Return(control);
        await firstLease.DisposeAsync();
        await using var reconnected = await RespireConnection.ConnectAsync("127.0.0.1", firstServer.Port);
        await reconnected.EnsureServerClientIdAsync();
        InstallPhysicalConnection(node, reconnected);
        await using (var samePeer = router.GetCorrectionLease(reconnected))
            await Assert.That(ReferenceEquals(samePeer.Pool, firstPool)).IsTrue();
        await using var replacement = await RespireConnection.ConnectAsync("127.0.0.1", secondServer.Port);
        await replacement.EnsureServerClientIdAsync();
        InstallPhysicalConnection(node, replacement);
        var handlers = (Action<int, RespireConnectionStateChange>?)typeof(RespireConnectionMultiplexer)
            .GetField("SlotStateChanged", Private)!.GetValue(node);
        handlers?.Invoke(0, new(endpoint, RespireConnectionState.Connected, null));
        await Assert.That(Count(router, "_correctionPools")).IsEqualTo(0);
        await Assert.That(Count(router, "_correctionStateHandlers")).IsEqualTo(0);
        await using (var newPeer = router.GetCorrectionLease(replacement))
            await Assert.That(ReferenceEquals(newPeer.Pool, firstPool)).IsFalse();
        // Await the already-started cleanup rather than depending on background timing.
        await firstPool.RetireAsync();
        await Assert.That(control.IsConnected).IsFalse();
        await client.FenceCorrectionConnectionAsync(new(endpoint, 42, Connection: original));
        await Assert.That(firstServer.ReceivedCommands).Contains("CLIENT KILL ID 42");
        await Assert.That(secondServer.ReceivedCommands).DoesNotContain("CLIENT KILL ID 42");
        await Assert.That(replacement.IsConnected).IsTrue();
        await Assert.That(Count(router, "_correctionPools")).IsEqualTo(1);
    }

    [Test]
    public async Task RetirementFenceUsesCapturedNetworkPeerInsteadOfResolvingHostname()
    {
        await using var server = new FakeRespServer(2, ":42\r\n"u8.ToArray());
        await using var node = RespireConnectionMultiplexer.Create("unresolvable.invalid");
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        await connection.EnsureServerClientIdAsync();
        InstallPhysicalConnection(node, connection);
        await connection.DisposeAsync();
        await node.RetireAsync().WaitAsync(Limit);
        await Assert.That(server.ReceivedCommands).Contains("CLIENT KILL ID 42");
        await Assert.That(node.HasPendingCorrectionFences).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FailedFenceRetainsGenerationUntilAcknowledged(bool disposeBeforeAcknowledgement)
    {
        var firstSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retrySeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var kills = 0;
        await using var server = new FakeRespServer(4, ":42\r\n"u8.ToArray(), "-ERR try again\r\n"u8.ToArray())
        {
            SuppressReply = command =>
            {
                if (command != "CLIENT KILL ID 42") return false;
                if (Interlocked.Increment(ref kills) == 1) firstSeen.TrySetResult();
                else retrySeen.TrySetResult();
                return true;
            },
        };
        await using var client = CreateClient();
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("127.0.0.1", server.Port);
        Publish(router, endpoint, "old", 1);
        var old = router.GetMultiplexer(endpoint);
        await old.EnsureConnectedAsync();
        var connection = old.GetConnection();
        await connection.EnsureServerClientIdAsync();
        await connection.DisposeAsync();
        Publish(router, endpoint, "new", 2);
        await firstSeen.Task.WaitAsync(Limit);
        await server.SendRawAsync("-ERR try again\r\n"u8.ToArray(), 1);
        await retrySeen.Task.WaitAsync(Limit);
        await Assert.That(old.HasPendingCorrectionFences).IsTrue();
        await Assert.That(RetainedNodes(router)).IsEqualTo(3);
        await Assert.That(router.WaitForRetirementAsync().IsCompleted).IsFalse();
        var snapshot = client.GetClusterRetirementSnapshot()!;
        await Assert.That(snapshot.RetiringGenerationCount).IsEqualTo(1);
        await Assert.That(snapshot.PendingCorrectionFenceCount).IsEqualTo(1);
        await Assert.That(snapshot.AwaitingFenceGenerationCount).IsEqualTo(1);
        await Assert.That(snapshot.UndrainedGenerationCount).IsEqualTo(0);
        await Assert.That(snapshot.CleanupFailedGenerationCount).IsEqualTo(0);
        await Assert.That(snapshot.OldestRetirementAge).IsGreaterThan(TimeSpan.Zero);
        var later = client.GetClusterRetirementSnapshot()!;
        await Assert.That(later.OldestRetirementAge).IsGreaterThanOrEqualTo(snapshot.OldestRetirementAge);
        if (disposeBeforeAcknowledgement)
        {
            await client.DisposeAsync().AsTask().WaitAsync(Limit);
            await Assert.That(() => client.GetClusterRetirementSnapshot()).ThrowsExactly<ObjectDisposedException>();
            await Assert.That(snapshot.PendingCorrectionFenceCount).IsEqualTo(1);
            return;
        }
        await server.SendRawAsync(":1\r\n"u8.ToArray(), 2);
        await router.WaitForRetirementAsync().WaitAsync(Limit);
        await Assert.That(RetainedNodes(router)).IsEqualTo(2);
        await Assert.That(old.HasPendingCorrectionFences).IsFalse();
        await Assert.That(client.GetClusterRetirementSnapshot()!.PendingCorrectionFenceCount).IsEqualTo(0);
        await Assert.That(client.GetClusterRetirementSnapshot()!.RetiringGenerationCount).IsEqualTo(0);
        await Assert.That(snapshot.PendingCorrectionFenceCount).IsEqualTo(1);
        // The shared retirement task still contains its first failure after the router's
        // successful fence retry. A later correction must use the now-safe original peer.
        await Assert.That(old.RetireAsync().IsFaulted).IsTrue();
        await client.ExecuteOnAllConnectionsAsync(RespireScript.Create("return 1"), ["key"], [],
            new(endpoint, 42, Connection: connection)).AsTask().WaitAsync(Limit);
        await Assert.That(server.ReceivedCommands).Contains("EVAL return 1 1 key");
        await Assert.That(kills).IsEqualTo(2);
        await Assert.That(Count(router, "_ownedPools")).IsEqualTo(0);
    }

    // Model DNS resolution changes without mutating machine-wide DNS. Every installed
    // connection has a real socket, captured network peer and server-local client identity.
    private static void InstallPhysicalConnection(RespireConnectionMultiplexer node, RespireConnection connection)
    {
        var connections = (RespireConnection?[])typeof(RespireConnectionMultiplexer).GetField("_connections", Private)!.GetValue(node)!;
        connections[0] = connection;
        connection.Multiplexer = node;
    }

    private static RespireClient CreateClient(ILoggerFactory? loggerFactory = null, int maxInflightCommands = 16384,
        bool allowAdmin = false) => RespireClient.Create(new RespireOptions
    {
        UseCluster = true, Connections = 1, Endpoints = { new RespireEndpoint("seed.invalid") },
        LoggerFactory = loggerFactory,
        MaxInflightCommands = maxInflightCommands,
        AllowAdmin = allowAdmin,
    });

    private sealed class FailingPoolDisconnectLogger(bool failRetirementLog = false, bool failNodeDisconnect = false) : ILoggerFactory, ILogger
    {
        internal readonly InvalidOperationException Failure = new("Test dedicated disconnect failure.");
        internal readonly InvalidOperationException RetirementFailure = new("Test retirement failure.");
        internal readonly TaskCompletionSource RetirementFailureSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ILogger CreateLogger(string categoryName) => categoryName.Contains(".Blocking.", StringComparison.Ordinal)
            || (failNodeDisconnect && categoryName.StartsWith("Respire.Cluster.127.", StringComparison.Ordinal))
            || (failRetirementLog && categoryName == "Respire.Cluster") ? this : NullLogger.Instance;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (failRetirementLog && logLevel == LogLevel.Debug
                && formatter(state, exception).StartsWith("Cluster generation retirement needs", StringComparison.Ordinal))
            {
                RetirementFailureSeen.TrySetResult();
                throw RetirementFailure;
            }
            if (logLevel == LogLevel.Debug && formatter(state, exception).StartsWith("Disconnected from", StringComparison.Ordinal))
                throw Failure;
        }
    }

    private static ClusterNodeIdentityIndex Identities(ClusterRouter router)
        => (ClusterNodeIdentityIndex)typeof(ClusterRouter).GetField("_identities", Private)!.GetValue(router)!;

    private static int RetainedNodes(ClusterRouter router)
    {
        lock (router.NodeStateGate) return Identities(router).All.Count();
    }

    private static int Count(ClusterRouter router, string field)
    {
        lock (router.NodeStateGate)
        {
            var value = typeof(ClusterRouter).GetField(field, Private)!.GetValue(router)!;
            return (int)value.GetType().GetProperty("Count")!.GetValue(value)!;
        }
    }

    private static void Publish(ClusterRouter router, RespireEndpoint endpoint, string id, long generation)
    {
        var version = (long)typeof(ClusterRouter).GetField("_topologyVersion", Private)!.GetValue(router)!;
        List<ClusterTopologyRange> ranges = [new(0, 16383, endpoint, id, [new("alias.invalid", endpoint.Port)])];
        typeof(ClusterRouter).GetMethod("ApplyTopology", Private)!.Invoke(router, [ranges, version, generation]);
    }
}

