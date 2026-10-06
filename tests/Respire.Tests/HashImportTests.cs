using Respire.Commands;
using Respire.Networking;
using Respire.Testing;
using Respire.Tests.Networking;
using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class HashImportTests
{
    [Test]
    [Arguments(false, false, 2)]
    [Arguments(false, true, 2)]
    [Arguments(true, false, 2)]
    [Arguments(true, true, 2)]
    [Arguments(false, false, 3)]
    [Arguments(false, true, 3)]
    [Arguments(true, false, 3)]
    [Arguments(true, true, 3)]
    public async Task FullRingFailurePreservesPreparedFieldsets(bool transaction, bool cancelCaller, int protocol)
    {
        var limit = TimeSpan.FromSeconds(10);
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with
        {
            Protocol = (RespProtocol)protocol, MaxInflightCommands = 2,
            CommandTimeout = TimeSpan.FromSeconds(2),
        });
        await using var session = await client.Hashes.CreateImportSessionAsync();
        await session.PrepareAsync("schema", "field");
        var gate = new RespireFakeGate();
        using var pause = server.InjectFault("PING", RespireFakeFault.Pause(gate));
        using var multiProbe = server.InjectFault("MULTI", RespireFakeFault.Loading());
        using var importProbe = server.InjectFault("HIMPORT", RespireFakeFault.Loading(), firstArgument: "SET"u8.ToArray());
        var ping = new RawCommand(FakeRespServer.PingFrame);
        var blockers = new[]
        {
            session.Connection.SendAsync(ping, armCommandDeadline: false).AsTask(),
            session.Connection.SendAsync(ping, armCommandDeadline: false).AsTask(),
        };
        await pause.Matched.WaitAsync(limit);
        await Assert.That(session.Connection.Inflight.Count).IsEqualTo(2);
        using var caller = new CancellationTokenSource();
        try
        {
            await using var multi = transaction ? session.CreateTransaction() : null;
            var pending = multi?.Hashes.Import("unsent", "schema", "value");
            Task operation = multi is null
                ? session.SetAsync("unsent", "schema", ["value"], caller.Token).AsTask()
                : multi.CommitAsync(caller.Token).AsTask();
            if (cancelCaller)
            {
                caller.Cancel();
                var error = await Assert.That(async () => await operation.WaitAsync(limit))
                    .Throws<RespireCommandNotSubmittedException>();
                await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
                if (pending is not null) await Assert.That(pending.Error).IsSameReferenceAs(error);
            }
            else
            {
                var error = await Assert.That(async () => await operation.WaitAsync(limit))
                    .ThrowsExactly<RespireTimeoutException>();
                await Assert.That(error!.Diagnostics.Stage).IsEqualTo(RespireCommandStage.WaitingForCapacity);
                await Assert.That(error.Diagnostics.InflightCount).IsEqualTo(2);
                await Assert.That(error.IsCommandNotSubmitted).IsTrue();
                if (pending is not null) await Assert.That(pending.Error).IsSameReferenceAs(error);
            }
        }
        finally
        {
            gate.Release();
            foreach (var reply in await Task.WhenAll(blockers).WaitAsync(limit))
            {
                using (reply) await Assert.That(reply.AsString()).IsEqualTo("PONG");
            }
        }
        await Assert.That(multiProbe.MatchedCount).IsEqualTo(0);
        await Assert.That(importProbe.MatchedCount).IsEqualTo(0);
        importProbe.Dispose();
        // These values depend on the previously prepared connection-local schema.
        await Assert.That(await session.SetAsync("after", "schema", "retained")).IsTrue();
        await Assert.That(await client.Hashes.GetStringAsync("after", "field")).IsEqualTo("retained");

    }

    [Test]
    [Arguments(false, 2)]
    [Arguments(true, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 3)]
    public async Task DeadlineAfterAdmissionExpiresSession(bool transaction, int protocol)
    {
        var limit = TimeSpan.FromSeconds(10);
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with
        {
            Protocol = (RespProtocol)protocol, CommandTimeout = TimeSpan.FromSeconds(2),
        });
        await using var session = await client.Hashes.CreateImportSessionAsync();
        await session.PrepareAsync("schema", "field");
        var gate = new RespireFakeGate();
        using var pause = server.InjectFault(transaction ? "MULTI" : "HIMPORT",
            RespireFakeFault.Pause(gate, afterExecution: true));
        await using var multi = transaction ? session.CreateTransaction() : null;
        var pending = multi?.Hashes.Import("key", "schema", "value");
        Task operation = multi is null ? session.SetAsync("key", "schema", "value").AsTask() : multi.CommitAsync().AsTask();
        try
        {
            await pause.Matched.WaitAsync(limit);
            var error = await Assert.That(async () => await operation.WaitAsync(limit))
                .ThrowsExactly<RespireTimeoutException>();
            await Assert.That(error!.IsCommandNotSubmitted).IsFalse();
            if (pending is not null) await Assert.That(pending.Error).IsSameReferenceAs(error);
            await Assert.That(async () => await session.SetAsync("later", "schema", "value"))
                .Throws<ObjectDisposedException>();
            await Assert.That(pause.MatchedCount).IsEqualTo(1);
            await Assert.That(await client.Hashes.GetStringAsync("key", "field"))
                .IsEqualTo(transaction ? null : "value");
        }
        finally { gate.Release(); }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task FieldsetsStayOnDedicatedConnectionAndRemainIsolated(int protocol)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with
            { Protocol = (RespProtocol)protocol, Connections = 3 });
        await using var first = await client.Hashes.CreateImportSessionAsync();
        await using var second = await client.Hashes.CreateImportSessionAsync();
        await first.PrepareAsync("schema", "z", "a");
        await Assert.That(async () => await second.SetAsync("absent", "schema", "x", "y")).Throws<RespireServerException>();
        await second.PrepareAsync("schema", "different");
        for (var index = 0; index < 10; index++)
        {
            using var pong = await client.ExecuteAsync("PING");
            await first.SetAsync($"first:{index}", "schema", "z-value", "a-value");
        }
        await second.SetAsync("second", "schema", "other");
        await Assert.That(await client.Hashes.GetStringAsync("first:9", "z")).IsEqualTo("z-value");
        await Assert.That(await client.Hashes.GetStringAsync("first:9", "a")).IsEqualTo("a-value");
        await Assert.That(await client.Hashes.GetStringAsync("second", "different")).IsEqualTo("other");
        await first.DisposeAsync();
        await Assert.That(await client.Hashes.GetStringAsync("first:9", "a")).IsEqualTo("a-value");
        await Assert.That(await second.DiscardAllAsync()).IsEqualTo(1);
    }

    [Test]
    [Arguments(false, 2)]
    [Arguments(true, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 3)]
    public async Task QueuesSnapshotBinaryArgumentsAndApplyOnlyKeyPrefix(bool transaction, int protocol)
    {
        await using var server = new RespireFakeServer();
        await using var root = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
        var client = root.WithKeyPrefix("tenant:");
        await using var session = await client.Hashes.CreateImportSessionAsync();
        using var batch = transaction ? null : session.CreateBatch();
        await using var multi = transaction ? session.CreateTransaction() : null;
        IRespireCommandQueue queue = multi ?? (IRespireCommandQueue)batch!;
        byte[] key = [0, 255];
        byte[] name = [128, 0];
        byte[] field = [255, 0];
        byte[] value = [0, 128, 255];
        var prepared = queue.Hashes.PrepareImport(name, field);
        var imported = queue.Hashes.Import(new RespireKey(key), name, value);
        Array.Fill(key, (byte)1);
        Array.Fill(name, (byte)2);
        Array.Fill(field, (byte)3);
        Array.Fill(value, (byte)4);
        if (multi is not null) await multi.CommitAsync();
        else await batch!.ExecuteAsync();
        await Assert.That(prepared.Result).IsTrue();
        await Assert.That(imported.Result).IsTrue();
        var result = await client.Hashes.GetBytesAsync(new RespireKey(new byte[] { 0, 255 }), new RespireKey(new byte[] { 255, 0 }));
        await Assert.That(result).IsEquivalentTo(new byte[] { 0, 128, 255 });
    }

    [Test]
    [Arguments(1, 2)]
    [Arguments(2, 2)]
    [Arguments(4, 2)]
    [Arguments(5, 2)]
    [Arguments(1, 3)]
    [Arguments(2, 3)]
    [Arguments(4, 3)]
    [Arguments(5, 3)]
    public async Task BatchesPreserveFieldsetOrderBeyondInflightCapacity(int capacity, int protocol)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with
            { MaxInflightCommands = capacity, Protocol = (RespProtocol)protocol });
        await using var session = await client.Hashes.CreateImportSessionAsync();
        using var batch = session.CreateBatch();
        for (var index = 0; index < 32; index++)
        {
            _ = batch.Hashes.PrepareImport("schema", $"field:{index}");
            _ = batch.Hashes.Import($"key:{index}", "schema", $"value:{index}");
            _ = batch.Hashes.DiscardImport("schema");
        }
        var gate = new RespireFakeGate();
        using var pause = server.InjectFault("HIMPORT", RespireFakeFault.Pause(gate));
        var execute = batch.ExecuteAsync().AsTask();
        try
        {
            await pause.Matched.WaitAsync(TimeSpan.FromSeconds(5));
            gate.Release();
            await execute.WaitAsync(TimeSpan.FromSeconds(10));
            for (var index = 0; index < 32; index++)
                await Assert.That(await client.Hashes.GetStringAsync($"key:{index}", $"field:{index}"))
                    .IsEqualTo($"value:{index}");
            await Assert.That(await session.DiscardAllAsync()).IsEqualTo(0);
        }
        finally { gate.Release(); }
    }

    [Test]
    [Arguments("immediate")]
    [Arguments("batch")]
    [Arguments("transaction")]
    public async Task ReadOnlyFailoverExpiresSessionWithoutReplay(string mode)
    {
        await using var server = new FakeRespServer(20, FakeRespServer.OkReply);
        var topology = Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{server.Port}\r\n");
        var inMulti = false;
        server.ReplyOverride = (_, command) =>
        {
            if (command == "CLUSTER SLOTS") return topology;
            if (command == "MULTI") { inMulti = true; return FakeRespServer.OkReply; }
            if (command == "EXEC")
            {
                inMulti = false;
                return "*2\r\n-ERR unknown fieldset\r\n-READONLY demoted primary\r\n"u8.ToArray();
            }
            if (inMulti) return "+QUEUED\r\n"u8.ToArray();
            return command.StartsWith("HIMPORT SET", StringComparison.Ordinal)
                ? "-READONLY demoted primary\r\n"u8.ToArray() : FakeRespServer.OkReply;
        };
        await using var client = RespireClient.Create(new RespireOptions
            { UseCluster = true, Protocol = RespProtocol.Resp2, Connections = 1, Endpoints = [new("127.0.0.1", server.Port)] });
        await using var session = await client.Hashes.CreateImportSessionAsync("{one}:anchor");
        await session.PrepareAsync("schema", "field");
        if (mode == "immediate")
            await Assert.That(async () => await session.SetAsync("{one}:key", "schema", "value")).Throws<RespireServerException>();
        else if (mode == "batch")
        {
            using var batch = session.CreateBatch();
            _ = batch.Hashes.Import("{one}:key", "schema", "value");
            await Assert.That(async () => await batch.ExecuteAsync()).Throws<RespireServerException>();
        }
        else
        {
            await using var transaction = session.CreateTransaction();
            _ = transaction.Hashes.Import("{one}:unknown", "unknown", "value");
            var rejected = transaction.Hashes.Import("{one}:key", "schema", "value");
            await transaction.CommitAsync();
            await Assert.That(rejected.Error is RespireServerException { Code: RespireErrorCodes.ReadOnly }).IsTrue();
        }
        await Assert.That(server.ReceivedCommands.Count(command => command == "HIMPORT SET {one}:key schema value")).IsEqualTo(1);
        await Assert.That(async () => await session.SetAsync("{one}:later", "schema", "value")).Throws<ObjectDisposedException>();
        await Assert.That(client.Core.Cluster!.GetKnownSlotOwner(session.ClusterSlot!.Value)).IsNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UncertainBatchChunkFaultsUnsentImports(bool afterExecution)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { MaxInflightCommands = 1 });
        await using var session = await client.Hashes.CreateImportSessionAsync();
        using var batch = session.CreateBatch();
        var prepared = batch.Hashes.PrepareImport("schema", "field");
        var uncertain = batch.Hashes.Import("first", "schema", "value");
        var unsent = batch.Hashes.Import("later", "schema", "value");
        using var fault = server.InjectFault("HIMPORT", RespireFakeFault.Disconnect(afterExecution),
            firstArgument: "SET"u8.ToArray());
        await Assert.That(async () => await batch.ExecuteAsync()).Throws<RespireException>();
        await Assert.That(prepared.Result).IsTrue();
        await Assert.That(uncertain.Status).IsEqualTo(RespirePendingStatus.Faulted);
        await Assert.That(unsent.Status).IsEqualTo(RespirePendingStatus.Faulted);
        await Assert.That(await client.ExistsAsync("first")).IsEqualTo(afterExecution);
        await Assert.That(await client.ExistsAsync("later")).IsFalse();
        await Assert.That(fault.MatchedCount).IsEqualTo(1);
        await Assert.That(async () => await session.SetAsync("retry", "schema", "value")).Throws<ObjectDisposedException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TransactionPermissionFailuresDoNotEscapeTransaction(bool denyExec)
    {
        await using var server = new FakeRespServer(20, FakeRespServer.OkReply);
        var inMulti = false;
        server.ReplyOverride = (_, command) =>
        {
            if (command == "MULTI")
            {
                if (!denyExec) return "-NOPERM MULTI denied\r\n"u8.ToArray();
                inMulti = true;
                return FakeRespServer.OkReply;
            }
            if (command == "EXEC") return "-NOPERM EXEC denied\r\n"u8.ToArray();
            return inMulti ? "+QUEUED\r\n"u8.ToArray() : FakeRespServer.OkReply;
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var session = await client.Hashes.CreateImportSessionAsync();
        await session.PrepareAsync("schema", "field");
        await using var transaction = session.CreateTransaction();
        var pending = transaction.Hashes.Import("key", "schema", "value");
        await Assert.That(async () => await transaction.CommitAsync()).Throws<RespireServerException>();
        await Assert.That(pending.Status).IsEqualTo(RespirePendingStatus.Faulted);
        if (denyExec)
            await Assert.That(async () => await session.PrepareAsync("later", "field")).Throws<ObjectDisposedException>();
        else
        {
            await Assert.That(server.ReceivedCommands.Contains("HIMPORT SET key schema value")).IsFalse();
            await Assert.That(server.ReceivedCommands.Contains("EXEC")).IsFalse();
            await Assert.That(await session.PrepareAsync("later", "field")).IsTrue();
        }
    }

    [Test]
    [Arguments(2, "aborted")]
    [Arguments(3, "aborted")]
    [Arguments(2, "denied")]
    [Arguments(3, "denied")]
    [Arguments(2, "scalar")]
    [Arguments(3, "scalar")]
    public async Task QueueTimeErrorsPreserveSessionsOnlyAfterConfirmedExecAbort(int protocol, string outcome)
    {
        await using var server = new FakeRespServer(20, FakeRespServer.OkReply);
        var inMulti = false;
        server.ReplyOverride = (_, command) =>
        {
            if (command.StartsWith("HELLO ", StringComparison.Ordinal))
                return "%1\r\n+proto\r\n:3\r\n"u8.ToArray();
            if (command == "MULTI") { inMulti = true; return FakeRespServer.OkReply; }
            if (command == "EXEC")
            {
                inMulti = false;
                return outcome switch
                {
                    "aborted" => "-EXECABORT Transaction discarded because of previous errors.\r\n"u8.ToArray(),
                    "denied" => "-EXECABORT Transaction discarded because of: NOPERM EXEC denied\r\n"u8.ToArray(),
                    _ => "+OK\r\n"u8.ToArray(),
                };
            }
            return inMulti ? "-NOPERM HIMPORT SET denied\r\n"u8.ToArray() : FakeRespServer.OkReply;
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
            { Protocol = (RespProtocol)protocol, Endpoints = [new("127.0.0.1", server.Port)] });
        await using var session = await client.Hashes.CreateImportSessionAsync();
        await session.PrepareAsync("schema", "field");
        await using var transaction = session.CreateTransaction();
        var pending = transaction.Hashes.Import("key", "schema", "value");
        var error = await Assert.That(async () => await transaction.CommitAsync()).Throws<RespireServerException>();
        await Assert.That(error?.Code).IsEqualTo("NOPERM");
        await Assert.That(pending.Status).IsEqualTo(RespirePendingStatus.Faulted);
        if (outcome == "aborted")
            await Assert.That(await session.SetAsync("later", "schema", "retained")).IsTrue();
        else
            await Assert.That(async () => await session.SetAsync("later", "schema", "retained")).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task OversizedTransactionPreservesPreparedFieldsets()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { MaxInflightCommands = 2 });
        await using var session = await client.Hashes.CreateImportSessionAsync();
        await session.PrepareAsync("schema", "field");
        await using var transaction = session.CreateTransaction();
        _ = transaction.Hashes.Import("first", "schema", "one");
        _ = transaction.Hashes.Import("second", "schema", "two");
        await Assert.That(async () => await transaction.CommitAsync()).Throws<ArgumentOutOfRangeException>();
        await Assert.That(await client.ExistsAsync("first")).IsFalse();
        await Assert.That(await client.ExistsAsync("second")).IsFalse();
        await session.SetAsync("after", "schema", "retained");
        await Assert.That(await client.Hashes.GetStringAsync("after", "field")).IsEqualTo("retained");
    }

    [Test]
    public async Task CredentialRenewalPreservesBatchAdmissionOrder()
    {
        var clock = new Respire.Testing.CredentialTestClock();
        var provider = new ImportCredentials(new("user", "first", clock.GetUtcNow().AddSeconds(30)));
        await using var server = new FakeRespServer(256, FakeRespServer.OkReply);
        server.SuppressReply = command => command == "AUTH user second";
        server.ReplyOverride = (_, command) => command.StartsWith("HIMPORT DISCARD", StringComparison.Ordinal)
            ? ":1\r\n"u8.ToArray() : FakeRespServer.OkReply;
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, MaxInflightCommands = 128,
            Endpoints = [new("127.0.0.1", server.Port)], CredentialProvider = provider,
            CredentialTimeProvider = clock, CredentialRefreshBeforeExpiry = TimeSpan.FromSeconds(10),
        });
        await using var session = await client.Hashes.CreateImportSessionAsync();
        await session.PrepareAsync("bootstrap", "field");
        var commands = server.ReceivedCommands.ToArray();
        var connectionId = server.ReceivedConnectionIds[Array.IndexOf(commands, "HIMPORT PREPARE bootstrap field")];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!clock.HasDelay(TimeSpan.FromSeconds(20))) await Task.Delay(5, deadline.Token);
        provider.Current = new("user", "second", clock.GetUtcNow().AddSeconds(60));
        clock.Advance(TimeSpan.FromSeconds(20));
        while (true)
        {
            commands = server.ReceivedCommands.ToArray();
            if (commands.Select((command, index) => (command, index)).Any(item => item.command == "AUTH user second"
                && server.ReceivedConnectionIds[item.index] == connectionId)) break;
            await Task.Delay(5, deadline.Token);
        }
        using var batch = session.CreateBatch();
        var expected = new List<string> { "HIMPORT PREPARE bootstrap field" };
        for (var index = 0; index < 32; index++)
        {
            _ = batch.Hashes.PrepareImport($"schema:{index}", "field");
            _ = batch.Hashes.Import($"key:{index}", $"schema:{index}", "value");
            _ = batch.Hashes.DiscardImport($"schema:{index}");
            expected.Add($"HIMPORT PREPARE schema:{index} field");
            expected.Add($"HIMPORT SET key:{index} schema:{index} value");
            expected.Add($"HIMPORT DISCARD schema:{index}");
        }
        var execute = batch.ExecuteAsync(deadline.Token).AsTask();
        await Assert.That(execute.IsCompleted).IsFalse();
        await server.SendRawAsync(FakeRespServer.OkReply, connectionId);
        await execute.WaitAsync(deadline.Token);
        var actual = server.ReceivedCommands.Where(command => command.StartsWith("HIMPORT ", StringComparison.Ordinal));
        await Assert.That(string.Join('|', actual)).IsEqualTo(string.Join('|', expected));
    }

    private sealed class ImportCredentials(RespireCredentials current) : IRespireCredentialProvider
    {
        public RespireCredentials Current = current;
        public int Calls;
        public ValueTask<RespireCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return ValueTask.FromResult(Current);
        }
    }

    [Test]
    public async Task PreCanceledBatchPreservesPreparedFieldsets()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        await using var session = await client.Hashes.CreateImportSessionAsync();
        await session.PrepareAsync("schema", "field");
        using var batch = session.CreateBatch();
        var unsent = batch.Hashes.Import("unsent", "schema", "value");
        await Assert.That(async () => await batch.ExecuteAsync(new(true))).Throws<OperationCanceledException>();
        await Assert.That(unsent.Status).IsEqualTo(RespirePendingStatus.Faulted);
        await Assert.That(await client.ExistsAsync("unsent")).IsFalse();
        await session.SetAsync("later", "schema", "retained");
        await Assert.That(await client.Hashes.GetStringAsync("later", "field")).IsEqualTo("retained");
    }

    [Test]
    public async Task BatchCanceledDuringRenewalPreservesSession()
    {
        var clock = new Respire.Testing.CredentialTestClock();
        var provider = new ImportCredentials(new("user", "first", clock.GetUtcNow().AddSeconds(30)));
        await using var server = new FakeRespServer(20, FakeRespServer.OkReply);
        server.SuppressReply = command => command == "AUTH user second";
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, Endpoints = [new("127.0.0.1", server.Port)],
            CredentialProvider = provider, CredentialTimeProvider = clock,
            CredentialRefreshBeforeExpiry = TimeSpan.FromSeconds(10),
        });
        await using var session = await client.Hashes.CreateImportSessionAsync();
        await session.PrepareAsync("schema", "field");
        var commands = server.ReceivedCommands.ToArray();
        var connectionId = server.ReceivedConnectionIds[Array.IndexOf(commands, "HIMPORT PREPARE schema field")];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!clock.HasDelay(TimeSpan.FromSeconds(20))) await Task.Delay(5, deadline.Token);
        provider.Current = new("user", "second", clock.GetUtcNow().AddSeconds(60));
        clock.Advance(TimeSpan.FromSeconds(20));
        while (!server.ReceivedCommands.Select((command, index) => (command, index)).Any(item =>
                   item.command == "AUTH user second" && server.ReceivedConnectionIds[item.index] == connectionId))
            await Task.Delay(5, deadline.Token);
        using var batch = session.CreateBatch();
        _ = batch.Hashes.Import("unsent", "schema", "value");
        using var cancel = new CancellationTokenSource();
        var execute = batch.ExecuteAsync(cancel.Token).AsTask();
        await Assert.That(execute.IsCompleted).IsFalse();
        cancel.Cancel();
        await Assert.That(async () => await execute.WaitAsync(deadline.Token)).Throws<OperationCanceledException>();
        await Assert.That(server.ReceivedCommands.Contains("HIMPORT SET unsent schema value")).IsFalse();
        await server.SendRawAsync(FakeRespServer.OkReply, connectionId);
        await Assert.That(await session.SetAsync("later", "schema", ["retained"], deadline.Token)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CredentialRenewalWaitsUntilImportTransactionFinishes(bool expireCredentials)
    {
        var clock = new Respire.Testing.CredentialTestClock();
        var provider = new ImportCredentials(new("user", "first", clock.GetUtcNow().AddSeconds(30)));
        await using var server = new FakeRespServer(30, FakeRespServer.OkReply);
        var inMulti = false;
        var importConnection = 0;
        server.SuppressReply = command =>
        {
            if (command != "MULTI") return false;
            inMulti = true;
            return true;
        };
        server.ReplyOverride = (connectionId, command) =>
        {
            if (connectionId != importConnection) return FakeRespServer.OkReply;
            if (command == "EXEC") { inMulti = false; return "*1\r\n+OK\r\n"u8.ToArray(); }
            return inMulti ? "+QUEUED\r\n"u8.ToArray() : FakeRespServer.OkReply;
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, Endpoints = [new("127.0.0.1", server.Port)],
            CredentialProvider = provider, CredentialTimeProvider = clock,
            CredentialRefreshBeforeExpiry = TimeSpan.FromSeconds(10),
        });
        await using var session = await client.Hashes.CreateImportSessionAsync();
        await session.PrepareAsync("schema", "field");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!clock.HasDelay(TimeSpan.FromSeconds(20))) await Task.Delay(5, deadline.Token);
        await using var transaction = session.CreateTransaction();
        var imported = transaction.Hashes.Import("key", "schema", "value");
        var commit = transaction.CommitAsync(deadline.Token).AsTask();
        while (!server.ReceivedCommands.Contains("MULTI")) await Task.Delay(5, deadline.Token);
        var commands = server.ReceivedCommands.ToArray();
        var connectionId = server.ReceivedConnectionIds[Array.IndexOf(commands, "MULTI")];
        importConnection = connectionId;
        var previousCalls = Volatile.Read(ref provider.Calls);
        provider.Current = new("user", "second", clock.GetUtcNow().AddSeconds(60));
        clock.Advance(TimeSpan.FromSeconds(20));
        while (Volatile.Read(ref provider.Calls) == previousCalls) await Task.Delay(5, deadline.Token);
        // Leave MULTI's reply withheld while the independent refresh worker runs.
        await Task.Delay(100, deadline.Token);
        await Assert.That(server.ReceivedCommands.Select((command, index) => (command, index)).Any(item =>
            item.command == "AUTH user second" && server.ReceivedConnectionIds[item.index] == connectionId)).IsFalse();
        if (expireCredentials)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            await Assert.That(async () => await commit.WaitAsync(deadline.Token)).Throws<RespireException>();
            await Assert.That(async () => await session.SetAsync("later", "schema", "value")).Throws<ObjectDisposedException>();
            return;
        }
        await server.SendRawAsync(FakeRespServer.OkReply, connectionId);
        await commit.WaitAsync(deadline.Token);
        await Assert.That(imported.Result).IsTrue();
        while (!server.ReceivedCommands.Select((command, index) => (command, index)).Any(item =>
                   item.command == "AUTH user second" && server.ReceivedConnectionIds[item.index] == connectionId))
            await Task.Delay(5, deadline.Token);
        commands = server.ReceivedCommands.Select((command, index) => (command, index))
            .Where(item => server.ReceivedConnectionIds[item.index] == connectionId)
            .Select(item => item.command).ToArray();
        await Assert.That(Array.IndexOf(commands, "AUTH user second")).IsGreaterThan(Array.IndexOf(commands, "EXEC"));
        await Assert.That(await session.SetAsync("later", "schema", ["value"], deadline.Token)).IsTrue();
    }

    [Test]
    public async Task OrdinaryQueuesRejectConnectionLocalImports()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        await Assert.That(() => batch.Hashes.PrepareImport("schema", "field")).Throws<InvalidOperationException>();
        await Assert.That(() => transaction.Hashes.Import("key", "schema", "value")).Throws<InvalidOperationException>();
        await Assert.That(batch.Count).IsEqualTo(0);
        await Assert.That(transaction.Count).IsEqualTo(0);
        await using var session = await client.Hashes.CreateImportSessionAsync();
        using var importBatch = session.CreateBatch();
        await Assert.That(() => importBatch.Set("key", "ordinary")).Throws<NotSupportedException>();
        await Assert.That(importBatch.Count).IsEqualTo(0);
        _ = importBatch.Hashes.PrepareImport("schema", "field");
        await Assert.That(async () => await importBatch.ExecuteAndWaitForReplicationAsync(1, TimeSpan.FromSeconds(1))).Throws<NotSupportedException>();
        await Assert.That(async () => await importBatch.ExecuteAndWaitForAofAsync(false, 1, TimeSpan.FromSeconds(1))).Throws<NotSupportedException>();
        await Assert.That(importBatch.IsSent).IsFalse();
        await importBatch.ExecuteAsync();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ValidationAndServerErrorsPreserveExistingFieldsets(int protocol)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
        await using var session = await client.Hashes.CreateImportSessionAsync();
        await session.PrepareAsync("schema", "field");
        await Assert.That(async () => await session.PrepareAsync("schema", "duplicate", "duplicate")).Throws<ArgumentException>();
        await Assert.That(async () => await session.PrepareAsync("schema", "field", "field"u8.ToArray())).Throws<ArgumentException>();
        await Assert.That(async () => await session.PrepareAsync(default, "field")).Throws<ArgumentException>();
        await Assert.That(async () => await session.PrepareAsync("schema", [default])).Throws<ArgumentException>();
        await Assert.That(async () => await session.SetAsync("key", default, "value")).Throws<ArgumentException>();
        await Assert.That(async () => await session.SetAsync("key", "schema", [default])).Throws<ArgumentException>();
        await Assert.That(async () => await session.DiscardAsync(default)).Throws<ArgumentException>();
        await Assert.That(async () => await session.PrepareAsync("empty", [])).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await session.SetAsync("key", "schema", [])).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await session.SetAsync("key", "schema", "too", "many")).Throws<RespireServerException>();
        await session.SetAsync("key", "schema", "valid");
        await Assert.That(await client.Hashes.GetStringAsync("key", "field")).IsEqualTo("valid");
        await Assert.That(await session.DiscardAsync("schema")).IsTrue();
        await Assert.That(await session.DiscardAsync("schema")).IsFalse();
        await Assert.That(async () => await session.SetAsync("key", "schema", "after-discard")).Throws<RespireServerException>();
        await Assert.That(await session.DiscardAllAsync()).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task QueueErrorsPreserveOtherResultsAndSession(bool transaction)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { MaxInflightCommands = transaction ? 16 : 1 });
        await using var session = await client.Hashes.CreateImportSessionAsync();
        await session.PrepareAsync("schema", "field");
        using var batch = transaction ? null : session.CreateBatch();
        await using var multi = transaction ? session.CreateTransaction() : null;
        IRespireCommandQueue queue = multi ?? (IRespireCommandQueue)batch!;
        var failed = queue.Hashes.Import("not-written", "unknown", "value");
        var applied = queue.Hashes.Import("written", "schema", "value");
        if (multi is not null) await multi.CommitAsync();
        else await Assert.That(async () => await batch!.ExecuteAsync()).Throws<RespireServerException>();
        await Assert.That(failed.Status).IsEqualTo(RespirePendingStatus.Faulted);
        await Assert.That(failed.Error is RespireServerException).IsTrue();
        await Assert.That(applied.Result).IsTrue();
        await Assert.That(await client.ExistsAsync("not-written")).IsFalse();
        await Assert.That(await client.Hashes.GetStringAsync("written", "field")).IsEqualTo("value");
        await Assert.That(await session.DiscardAllAsync()).IsEqualTo(1);
    }

    [Test]
    [Arguments("immediate", false, 2)]
    [Arguments("immediate", true, 2)]
    [Arguments("batch", false, 2)]
    [Arguments("batch", true, 2)]
    [Arguments("transaction", false, 2)]
    [Arguments("transaction", true, 2)]
    [Arguments("immediate", false, 3)]
    [Arguments("immediate", true, 3)]
    [Arguments("batch", false, 3)]
    [Arguments("batch", true, 3)]
    [Arguments("transaction", false, 3)]
    [Arguments("transaction", true, 3)]
    public async Task DisconnectedImportsNeverReplay(string mode, bool afterExecution, int protocol)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
        await using var session = await client.Hashes.CreateImportSessionAsync();
        await session.PrepareAsync("schema", "field");
        using var fault = server.InjectFault(mode == "transaction" ? "EXEC" : "HIMPORT",
            RespireFakeFault.Disconnect(afterExecution), occurrences: null,
            firstArgument: mode == "transaction" ? null : (ReadOnlyMemory<byte>?)"SET"u8.ToArray());
        async Task Send()
        {
            if (mode == "immediate") { await session.SetAsync("key", "schema", "value"); return; }
            using var batch = mode == "batch" ? session.CreateBatch() : null;
            await using var multi = mode == "transaction" ? session.CreateTransaction() : null;
            IRespireCommandQueue queue = multi ?? (IRespireCommandQueue)batch!;
            _ = queue.Hashes.Import("key", "schema", "value");
            if (multi is not null) await multi.CommitAsync();
            else await batch!.ExecuteAsync();
        }
        await Assert.That(Send).Throws<RespireException>();
        await Assert.That(fault.MatchedCount).IsEqualTo(1);
        await Assert.That(await client.Hashes.GetStringAsync("key", "field")).IsEqualTo(afterExecution ? "value" : null);
        await Assert.That(async () => await session.SetAsync("later", "schema", "value")).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task DisposalReturnsBorrowedCountToZeroAndIsIdempotent()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        var session = await client.Hashes.CreateImportSessionAsync();
        await session.PrepareAsync("schema", "field");
        await Assert.That(client.Core.DedicatedPool.CaptureRetirementState().Borrowed).IsEqualTo(1);
        await session.DisposeAsync();
        await session.DisposeAsync();
        await Assert.That(client.Core.DedicatedPool.CaptureRetirementState().Borrowed).IsEqualTo(0);
        await Assert.That(async () => await session.PrepareAsync("schema", "field")).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task CancellationBeforeImmediateSendPreservesSession()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        await using var session = await client.Hashes.CreateImportSessionAsync();
        await session.PrepareAsync("schema", "field");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () => await session.SetAsync("not-written", "schema", ["value"], cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(await client.ExistsAsync("not-written")).IsFalse();
        await session.SetAsync("written", "schema", "value");
        await Assert.That(await client.Hashes.GetStringAsync("written", "field")).IsEqualTo("value");
    }

    [Test]
    [Arguments(false, 2)]
    [Arguments(true, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 3)]
    public async Task CancellationAfterSendExpiresSessionAndNeverReplays(bool afterExecution, int protocol)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
        await using var session = await client.Hashes.CreateImportSessionAsync();
        await session.PrepareAsync("schema", "field");
        var gate = new RespireFakeGate();
        using var fault = server.InjectFault("HIMPORT", RespireFakeFault.Pause(gate, afterExecution), firstArgument: "SET"u8.ToArray());
        using var cancellation = new CancellationTokenSource();
        var send = session.SetAsync("key", "schema", ["value"], cancellation.Token).AsTask();
        try
        {
            await fault.Matched.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(async () => await session.SetAsync("concurrent", "schema", "value")).Throws<InvalidOperationException>();
            cancellation.Cancel();
            await Assert.That(async () => await send.WaitAsync(TimeSpan.FromSeconds(5))).Throws<OperationCanceledException>();
            await Assert.That(async () => await session.SetAsync("later", "schema", "value")).Throws<ObjectDisposedException>();
            await Assert.That(await client.Hashes.GetStringAsync("key", "field")).IsEqualTo(afterExecution ? "value" : null);
            await Assert.That(fault.MatchedCount).IsEqualTo(1);
        }
        finally { gate.Release(); }
    }

    [Test]
    public async Task ParentDisposalInvalidatesOutstandingSession()
    {
        await using var server = new RespireFakeServer();
        var client = await RespireClient.ConnectAsync(server.CreateOptions());
        await using var session = await client.Hashes.CreateImportSessionAsync();
        await session.PrepareAsync("schema", "field");
        await client.DisposeAsync();
        await Assert.That(async () => await session.SetAsync("key", "schema", "value")).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task WireUsesSeparateSubcommandTokensAndBinaryArguments()
    {
        await using var server = new FakeRespServer(20, FakeRespServer.OkReply);
        server.ReplyOverride = (_, command) => command.StartsWith("HIMPORT DISCARD", StringComparison.Ordinal)
            ? ":1\r\n"u8.ToArray() : FakeRespServer.OkReply;
        await using var root = await RespireClient.ConnectAsync(new RespireOptions
            { Protocol = RespProtocol.Resp2, Endpoints = [new("127.0.0.1", server.Port)] });
        await using var session = await root.WithKeyPrefix("prefix:").Hashes.CreateImportSessionAsync();
        byte[] name = [0, 255];
        byte[] field = [128, 0];
        byte[] value = [255, 0, 128];
        await session.PrepareAsync(name, field);
        await session.SetAsync("key", name, value);
        await session.DiscardAsync(name);
        await session.DiscardAllAsync();
        byte[][][] expected =
        [
            ["HIMPORT"u8.ToArray(), "PREPARE"u8.ToArray(), name, field],
            ["HIMPORT"u8.ToArray(), "SET"u8.ToArray(), "prefix:key"u8.ToArray(), name, value],
            ["HIMPORT"u8.ToArray(), "DISCARD"u8.ToArray(), name],
            ["HIMPORT"u8.ToArray(), "DISCARDALL"u8.ToArray()],
        ];
        var commands = server.ReceivedArguments.Where(args => args[0].AsSpan().SequenceEqual("HIMPORT"u8)).ToArray();
        await Assert.That(commands.Length).IsEqualTo(expected.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            await Assert.That(commands[index].Length).IsEqualTo(expected[index].Length);
            for (var argument = 0; argument < expected[index].Length; argument++)
                await Assert.That(commands[index][argument]).IsEquivalentTo(expected[index][argument]);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ClusterRejectsOtherSlotsAndExpiresRedirectsWithoutReplay(bool ask)
    {
        await using var server = new FakeRespServer(20, FakeRespServer.OkReply);
        var topology = Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{server.Port}\r\n");
        server.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? topology : FakeRespServer.OkReply;
        await using var root = RespireClient.Create(new RespireOptions
            { UseCluster = true, Protocol = RespProtocol.Resp2, Connections = 1, Endpoints = [new("127.0.0.1", server.Port)] });
        var client = root.WithKeyPrefix("tenant:");
        await Assert.That(async () => await client.Hashes.CreateImportSessionAsync()).Throws<InvalidOperationException>();
        await using var session = await client.Hashes.CreateImportSessionAsync("{one}:anchor");
        await session.PrepareAsync("schema", "field");
        await Assert.That(async () => await session.SetAsync("{two}:key", "schema", "value")).Throws<InvalidOperationException>();
        using var batch = session.CreateBatch();
        await Assert.That(() => batch.Hashes.Import("{two}:key", "schema", "value")).Throws<InvalidOperationException>();
        await using var multi = session.CreateTransaction();
        await Assert.That(() => multi.Hashes.Import("{two}:key", "schema", "value")).Throws<InvalidOperationException>();
        _ = multi.Hashes.PrepareImport("unrelated-name", "field");
        _ = multi.Hashes.Import("{one}:queued", "unrelated-name", "value");
        _ = multi.Hashes.DiscardImport("unrelated-name");
        _ = multi.Hashes.DiscardAllImports();
        await Assert.That(multi.Count).IsEqualTo(4);
        await session.SetAsync("{one}:key", "schema", "value");
        var slot = session.ClusterSlot!.Value;
        server.ReplyOverride = (_, command) => command.StartsWith("HIMPORT SET", StringComparison.Ordinal)
            ? Encoding.ASCII.GetBytes($"-{(ask ? "ASK" : "MOVED")} {slot} 127.0.0.1:{server.Port}\r\n")
            : command == "CLUSTER SLOTS" ? topology : FakeRespServer.OkReply;
        await Assert.That(async () => await session.SetAsync("{one}:redirect", "schema", "value")).Throws<RespireServerException>();
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("HIMPORT SET tenant:{one}:redirect", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(async () => await session.SetAsync("{one}:later", "schema", "value")).Throws<ObjectDisposedException>();
    }

    [Test]
    [Arguments("null")]
    [Arguments("scalar")]
    [Arguments("missing")]
    [Arguments("extra")]
    [Arguments("mixed-errors")]
    public async Task InvalidExecRepliesFaultPendingsAndExpireSession(string shape)
    {
        await using var server = new FakeRespServer(20, FakeRespServer.OkReply);
        var inMulti = false;
        var reply = shape switch
        {
            "null" => "*-1\r\n",
            "scalar" => "+OK\r\n",
            "missing" => "*1\r\n+OK\r\n",
            "extra" => "*3\r\n+OK\r\n+OK\r\n+OK\r\n",
            _ => "*2\r\n-ERR unknown fieldset\r\n:1\r\n",
        };
        server.ReplyOverride = (_, command) =>
        {
            if (command == "MULTI") { inMulti = true; return FakeRespServer.OkReply; }
            if (command == "EXEC") { inMulti = false; return Encoding.ASCII.GetBytes(reply); }
            return inMulti ? "+QUEUED\r\n"u8.ToArray() : FakeRespServer.OkReply;
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
            { Protocol = RespProtocol.Resp2, Endpoints = [new("127.0.0.1", server.Port)] });
        await using var session = await client.Hashes.CreateImportSessionAsync();
        await session.PrepareAsync("schema", "field");
        await using var multi = session.CreateTransaction();
        var first = multi.Hashes.Import("first", "schema", "value");
        var second = multi.Hashes.Import("second", "schema", "value");
        if (shape == "mixed-errors") await multi.CommitAsync();
        else await Assert.That(async () => await multi.CommitAsync()).Throws<RespireProtocolException>();
        await Assert.That(first.Status).IsEqualTo(RespirePendingStatus.Faulted);
        await Assert.That(second.Status).IsEqualTo(RespirePendingStatus.Faulted);
        await Assert.That(async () => await session.SetAsync("later", "schema", "value")).Throws<ObjectDisposedException>();
    }

    [Test]
    [Arguments("PREPARE", "+NOT-OK\r\n")]
    [Arguments("SET", ":1\r\n")]
    [Arguments("DISCARD", ":2\r\n")]
    [Arguments("DISCARD", "+OK\r\n")]
    [Arguments("DISCARDALL", ":-1\r\n")]
    [Arguments("DISCARDALL", "+OK\r\n")]
    public async Task InvalidSubcommandRepliesExpireSession(string subcommand, string reply)
    {
        await using var server = new FakeRespServer(20, Encoding.ASCII.GetBytes(reply));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
            { Protocol = RespProtocol.Resp2, Endpoints = [new("127.0.0.1", server.Port)] });
        await using var session = await client.Hashes.CreateImportSessionAsync();
        async Task Send()
        {
            switch (subcommand)
            {
                case "PREPARE": await session.PrepareAsync("schema", "field"); break;
                case "SET": await session.SetAsync("key", "schema", "value"); break;
                case "DISCARD": await session.DiscardAsync("schema"); break;
                default: await session.DiscardAllAsync(); break;
            }
        }
        await Assert.That(Send).Throws<RespireException>();
        await Assert.That(async () => await session.DiscardAllAsync()).Throws<ObjectDisposedException>();
    }
}
