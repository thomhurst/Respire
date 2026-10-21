using System.Collections.Concurrent;
using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public partial class TopologyCommandRetryTests
{
    [Test]
    [Arguments(RespireCommandRetryCategory.ReadOnly, 1)]
    [Arguments(RespireCommandRetryCategory.WriteAccumulating, 2)]
    public async Task ClusterLostExecReplyChecksEveryMember(RespireCommandRetryCategory maximum, int expected)
    {
        var execs = 0;
        await using var server = UnitServer((_, current) => Interlocked.Increment(ref execs) == 1
            ? Close(current) : "*2\r\n$5\r\nvalue\r\n:2\r\n"u8.ToArray());
#pragma warning disable RESP010 // Test-owned client isolates batch and transaction replay, slot owners, and retry policy for this scenario.
        await using var client = await RespireClient.ConnectAsync(ClusterOptions(server) with
        { RetryPolicy = new() { MaxAttempts = 2, InitialBackoff = TimeSpan.Zero, MaxCommandRetryCategory = maximum } });
#pragma warning restore RESP010
        await using var transaction = client.CreateTransaction();
        var read = transaction.GetString("{key}:read");
        var increment = transaction.Increment("{key}:counter");
        if (expected == 1)
            await Assert.That(async () => await transaction.CommitAsync()).Throws<RespireConnectionException>();
        else
        {
            await transaction.CommitAsync();
            await Assert.That(await read).IsEqualTo("value");
            await Assert.That(await increment).IsEqualTo(2L);
        }
        await Assert.That(execs).IsEqualTo(expected);
        await Assert.That(server.ReceivedCommands.Count(c => c == "GET {key}:read")).IsEqualTo(expected);
    }

    [Test]
    [Arguments("MOVED")]
    [Arguments("ASK")]
    public async Task ClusterTransactionHandlesRedirectInsideTransientRetry(string redirect)
    {
        var events = new ConcurrentDictionary<string, long>();
        using var listener = RetryListener(events);
        await using var target = UnitServer((_, _) => "*1\r\n$5\r\nvalue\r\n"u8.ToArray());
        var queues = 0;
        await using var source = UnitServer((_, _) => "-EXECABORT discarded\r\n"u8.ToArray(), command =>
        {
            if (command != "GET key") return null;
            return Interlocked.Increment(ref queues) == 1 ? TryAgain
                : Encoding.ASCII.GetBytes($"-{redirect} {ClusterHash.GetSlot("key")} 127.0.0.1:{target.Port}\r\n");
        });
#pragma warning disable RESP010 // Test-owned client isolates batch and transaction replay, slot owners, and retry policy for this scenario.
        await using var client = await RespireClient.ConnectAsync(ClusterOptions(source) with
        { RetryPolicy = new() { MaxAttempts = 2, InitialBackoff = TimeSpan.Zero } });
#pragma warning restore RESP010
        await using var transaction = client.CreateTransaction();
        var read = transaction.GetString("key");
        if (redirect == "ASK")
        {
            await Assert.That(async () => await transaction.CommitAsync()).Throws<RespireConnectionException>();
            await Assert.That(target.ReceivedCommands.Contains("ASKING")).IsFalse();
            await Assert.That(target.ReceivedCommands.Contains("MULTI")).IsFalse();
        }
        else
        {
            await transaction.CommitAsync();
            await Assert.That(await read).IsEqualTo("value");
        }
        await Assert.That(events.GetValueOrDefault("attempted")).IsEqualTo(1L);
        await Assert.That(events.GetValueOrDefault(redirect == "ASK" ? "exhausted" : "succeeded")).IsEqualTo(1L);
    }

    [Test]
    [Arguments(RespireCommandRetryCategory.ReadOnly, 1)]
    [Arguments(RespireCommandRetryCategory.WriteLastWins, 2)]
    public async Task ClusterBatchRetriesCompleteSlotGroupIndependently(RespireCommandRetryCategory maximum, int expected)
    {
        var events = new ConcurrentDictionary<string, long>();
        using var listener = RetryListener(events);
        var writes = 0;
        await using var server = ClusterNode((command, _) => command switch
        {
            "GET {key}:read" => Volatile.Read(ref writes) == 0 ? "$3\r\nold\r\n"u8.ToArray() : Value,
            "SET {key}:write value" => Interlocked.Increment(ref writes) == 1 ? TryAgain : FakeRespServer.OkReply,
            "GET bar" => Value,
            _ => null,
        });
#pragma warning disable RESP010 // Test-owned client isolates batch and transaction replay, slot owners, and retry policy for this scenario.
        await using var client = await RespireClient.ConnectAsync(ClusterOptions(server) with
        { RetryPolicy = new() { MaxAttempts = 2, InitialBackoff = TimeSpan.Zero, MaxCommandRetryCategory = maximum } });
#pragma warning restore RESP010
        using var batch = client.CreateBatch();
        var read = batch.GetString("{key}:read");
        var write = batch.Set("{key}:write", "value");
        var other = batch.GetString("bar");
        var result = await batch.TryExecuteAsync();
        await Assert.That(result.FailureCount).IsEqualTo(expected == 2 ? 0 : 1);
        await Assert.That(read.Result).IsEqualTo(expected == 2 ? "value" : "old");
        await Assert.That(other.Result).IsEqualTo("value");
        await Assert.That(write.HasResult).IsEqualTo(expected == 2);
        await Assert.That(server.ReceivedCommands.Count(c => c == "GET {key}:read")).IsEqualTo(expected);
        await Assert.That(server.ReceivedCommands.Count(c => c == "GET bar")).IsEqualTo(1);
        await Assert.That(writes).IsEqualTo(expected);
        await Assert.That(events.GetValueOrDefault("attempted")).IsEqualTo((long)expected - 1);
        await Assert.That(events.GetValueOrDefault("succeeded")).IsEqualTo((long)expected - 1);
    }

    [Test]
    [Arguments("MOVED")]
    [Arguments("ASK")]
    public async Task ClusterBatchRedirectDuringRetryKeepsOriginalArguments(string redirect)
    {
        await using var target = ClusterNode((command, _) => command == "SET key first" ? FakeRespServer.OkReply : null);
        var writes = 0;
        await using var source = ClusterNode((command, _) => command == "SET key first"
            ? Interlocked.Increment(ref writes) == 1 ? TryAgain
                : Encoding.ASCII.GetBytes($"-{redirect} {ClusterHash.GetSlot("key")} 127.0.0.1:{target.Port}\r\n") : null);
#pragma warning disable RESP010 // Test-owned client isolates batch and transaction replay, slot owners, and retry policy for this scenario.
        await using var client = await RespireClient.ConnectAsync(ClusterOptions(source) with
        { RetryPolicy = new() { MaxAttempts = 2, InitialBackoff = TimeSpan.Zero, MaxCommandRetryCategory = RespireCommandRetryCategory.WriteLastWins } });
#pragma warning restore RESP010
        using var batch = client.CreateBatch();
        var bytes = "first"u8.ToArray();
        var write = batch.Set("key", bytes);
        var running = batch.ExecuteAsync();
        "later"u8.CopyTo(bytes);
        await running;
        await Assert.That(write.Result).IsTrue();
        await Assert.That(writes).IsEqualTo(2);
        await Assert.That(target.ReceivedCommands.Count(c => c == "SET key first")).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ClusterUnitRetryFollowsReplacementOfLostOwner(bool transaction)
    {
        var failedOver = 0;
        Task? stopping = null;
        await using var survivor = UnitServer((_, _) => "*1\r\n$5\r\nvalue\r\n"u8.ToArray());
        await using var owner = UnitServer((_, _) => null);
        byte[] Map() => Volatile.Read(ref failedOver) == 0 ? SplitSlots(survivor.Port, owner.Port) : Slots(survivor.Port);
        var survivorReply = survivor.ReplyOverride!;
        survivor.ReplyOverride = (id, command) => command == "CLUSTER SLOTS" ? Map()
            : command == "GET key" && !transaction ? Value : survivorReply(id, command);
        var ownerReply = owner.ReplyOverride!;
        owner.ReplyOverride = (id, command) =>
        {
            if (command == "CLUSTER SLOTS") return Map();
            if (command != (transaction ? "EXEC" : "GET key")) return ownerReply(id, command);
            Volatile.Write(ref failedOver, 1);
            stopping = owner.DisposeAsync().AsTask();
            owner.CloseConnections();
            return null;
        };
#pragma warning disable RESP010 // Test-owned client isolates batch and transaction replay, slot owners, and retry policy for this scenario.
        await using var client = await RespireClient.ConnectAsync(ClusterOptions(owner) with
        { RetryPolicy = new() { MaxAttempts = 2, InitialBackoff = TimeSpan.Zero } });
#pragma warning restore RESP010
        await Assert.That(await ReadUnitAsync(client, transaction, default)).IsEqualTo("value");
        if (stopping is not null) await stopping;
        await Assert.That(owner.ReceivedCommands.Count(c => c == "GET key")).IsEqualTo(1);
        await Assert.That(survivor.ReceivedCommands.Count(c => c == "GET key")).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FailoverUnitRetryUsesNewActiveMember(bool transaction)
    {
        var events = new ConcurrentDictionary<string, long>();
        using var listener = RetryListener(events);
        var down = 0;
        await using var primary = UnitServer((_, _) => "-LOADING busy\r\n"u8.ToArray());
        var primaryReply = primary.ReplyOverride!;
        primary.ReplyOverride = (id, command) => command switch
        {
            "PING" when Volatile.Read(ref down) != 0 => "-ERR down\r\n"u8.ToArray(),
            "GET key" when !transaction => "-LOADING busy\r\n"u8.ToArray(),
            _ => primaryReply(id, command),
        };
        await using var secondary = UnitServer((_, _) => "*1\r\n$5\r\nvalue\r\n"u8.ToArray());
        var secondaryReply = secondary.ReplyOverride!;
        secondary.ReplyOverride = (id, command) => command == "GET key" && !transaction ? Value : secondaryReply(id, command);
        await using var group = await RespireFailoverGroup.ConnectAsync([Candidate(primary, 0), Candidate(secondary, 1)], GroupOptions());
        var clients = new List<IRespireClient>();
        var result = await group.ExecuteAsync(async (client, token) =>
        {
            clients.Add(client);
            try { return await ReadUnitAsync(client, transaction, token); }
            catch (RespireServerException)
            {
                Volatile.Write(ref down, 1);
                await group.ForTests.ProbeAsync(0, token);
                await group.ForTests.SelectActiveAsync();
                throw;
            }
        }, RespireCommandRetryCategory.ReadOnly);
        await Assert.That(result).IsEqualTo("value");
        await Assert.That(clients.Count).IsEqualTo(2);
        await Assert.That(ReferenceEquals(clients[1], group.ActiveClient)).IsTrue();
        await Assert.That(primary.ReceivedCommands.Count(c => c == "GET key")).IsEqualTo(1);
        await Assert.That(events.GetValueOrDefault("attempted")).IsEqualTo(1L);
        await Assert.That(events.GetValueOrDefault("succeeded")).IsEqualTo(1L);
    }

    private static async ValueTask<string?> ReadUnitAsync(IRespireClient client, bool transaction, CancellationToken token)
    {
        if (transaction)
        {
            await using var unit = client.CreateTransaction();
            var read = unit.GetString("key");
            await unit.CommitAsync(token);
            return await read;
        }
        using var batch = client.CreateBatch();
        var value = batch.GetString("key");
        await batch.ExecuteAsync(token);
        return value.Result;
    }

    private static FakeRespServer UnitServer(Func<string, FakeRespServer, byte[]?> exec, Func<string, byte[]?>? queued = null)
    {
        var server = new FakeRespServer(32, FakeRespServer.OkReply);
        var transactions = new ConcurrentDictionary<int, bool>();
        server.ReplyOverride = (id, command) =>
        {
            if (command == "PING") return FakeRespServer.PongReply;
            if (command == "CLUSTER SLOTS") return Slots(server.Port);
            if (command == "MULTI") { transactions[id] = true; return FakeRespServer.OkReply; }
            if (command == "EXEC") { transactions[id] = false; return exec(command, server); }
            return transactions.GetValueOrDefault(id) ? queued?.Invoke(command) ?? "+QUEUED\r\n"u8.ToArray() : null;
        };
        return server;
    }

    private static byte[]? Close(FakeRespServer server) { server.CloseConnections(); return null; }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task UnitCircuitRejectionIsNeverRetried(bool transaction, bool failoverGroup)
    {
        var events = new ConcurrentDictionary<string, long>();
        using var listener = RetryListener(events);
        await using var server = UnitServer((_, _) => "*1\r\n$5\r\nvalue\r\n"u8.ToArray());
        var options = ClusterOptions(server) with
        {
            UseCluster = !failoverGroup,
            CircuitBreaker = new() { MinimumFailureCount = 1, FailureRateThreshold = 0.01, OpenDuration = TimeSpan.FromMinutes(1) },
        };
        await using var group = failoverGroup
            ? await RespireFailoverGroup.ConnectAsync([new(options)], GroupOptions()) : null;
#pragma warning disable RESP010 // Test-owned client isolates batch and transaction replay, slot owners, and retry policy for this scenario.
        await using var cluster = failoverGroup ? null : await RespireClient.ConnectAsync(options);
#pragma warning restore RESP010
        var client = cluster ?? (RespireClient)group!.ActiveClient;
        // Warm the Cluster owner before opening that endpoint's circuit.
        if (!failoverGroup)
        {
            using var warm = client.CreateBatch();
            _ = warm.GetString("key");
            await warm.TryExecuteAsync();
        }
        var sent = server.ReceivedCommands.Count(c => c == "GET key");
        var admission = client.Core.Circuits!.Acquire(new("127.0.0.1", server.Port), default);
        try { admission.Failed(new RespireConnectionException("Injected failure."), default); }
        finally { admission.Dispose(); }
        await Assert.That(async () =>
        {
            if (group is null) await ReadUnitAsync(client, transaction, default);
            else await group.ExecuteAsync((member, token) => ReadUnitAsync(member, transaction, token), RespireCommandRetryCategory.ReadOnly);
        }).ThrowsExactly<RespireCircuitOpenException>();
        await Assert.That(server.ReceivedCommands.Count(c => c == "GET key")).IsEqualTo(sent);
        await Assert.That(events.GetValueOrDefault("attempted")).IsEqualTo(0L);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ClusterUnitExhaustionHasOneTerminalRetryObservation(bool transaction)
    {
        using var configuration = new MetricConfigurationScope();
        using var evidence = new CompositionEvidence(throwAfterRecording: true);
        await using var server = UnitServer((_, _) => TryAgain);
        var reply = server.ReplyOverride!;
        server.ReplyOverride = (id, command) => command == "GET key" && !transaction ? TryAgain : reply(id, command);
#pragma warning disable RESP010 // Test-owned client isolates batch and transaction replay, slot owners, and retry policy for this scenario.
        await using var client = await RespireClient.ConnectAsync(ClusterOptions(server) with
        { RetryPolicy = new() { MaxAttempts = 2, InitialBackoff = TimeSpan.Zero } });
#pragma warning restore RESP010
        await Assert.That(async () => await ReadUnitAsync(client, transaction, default)).ThrowsExactly<RespireServerException>();
        await Assert.That(evidence.Retries.GetValueOrDefault("attempted")).IsEqualTo(1L);
        await Assert.That(evidence.Retries.GetValueOrDefault("exhausted")).IsEqualTo(1L);
        await Assert.That(evidence.Retries.GetValueOrDefault("succeeded")).IsEqualTo(0L);
        await Assert.That(evidence.FinalErrors).IsEqualTo(1L);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ClusterUnitWaitsForSlowOwnerReplacement(bool transaction)
    {
        var attempts = 0;
        await using var server = UnitServer((_, current) => Interlocked.Increment(ref attempts) == 1
            ? Close(current) : "*1\r\n$5\r\nvalue\r\n"u8.ToArray());
        var reply = server.ReplyOverride!;
        server.ReplyOverride = (id, command) => command == "GET key" && !transaction
            ? Interlocked.Increment(ref attempts) == 1 ? Close(server) : Value : reply(id, command);
#pragma warning disable RESP010 // Test-owned client isolates batch and transaction replay, slot owners, and retry policy for this scenario.
        await using var client = await RespireClient.ConnectAsync(ClusterOptions(server) with
        {
            ReconnectPolicy = new() { InitialDelay = TimeSpan.FromMilliseconds(1500), MaxDelay = TimeSpan.FromMilliseconds(1500), JitterRatio = 0 },
            RetryPolicy = new() { MaxAttempts = 2, InitialBackoff = TimeSpan.Zero },
        });
#pragma warning restore RESP010
        await Assert.That(await ReadUnitAsync(client, transaction, default)).IsEqualTo("value");
        await Assert.That(attempts).IsEqualTo(2);
    }
}
