using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

// Circuit admission composed with immediate, batch and transaction retries (#1256): an endpoint
// opened or saturated by an attempt is never resent to, while a retry that moves to another
// endpoint acquires fresh admission there.
public partial class TopologyCommandRetryTests
{
    [Test]
    [Arguments(false, "immediate")]
    [Arguments(false, "batch")]
    [Arguments(false, "transaction")]
    [Arguments(true, "immediate")]
    [Arguments(true, "batch")]
    [Arguments(true, "transaction")]
    public async Task RetryNeverResendsToTheEndpointItsFailureOpened(bool cluster, string shape)
    {
        var events = new ConcurrentDictionary<string, long>();
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var listener = RetryListener(events);
        var sends = 0;
        await using var server = CircuitUnitServer(shape, current =>
            Interlocked.Increment(ref sends) == 1 ? Close(current) : UnitValue(shape));
        await using var client = await RespireClient.ConnectAsync(CircuitRetryOptions(server, cluster));
        await Assert.That(await client.GetStringAsync("warm")).IsEqualTo("value");
        var error = await Failure(async () => await ReadCircuitUnitAsync(client, shape, default));
        await Assert.That(error).IsTypeOf<RespireCircuitOpenException>();
        await Assert.That(((RespireCircuitOpenException)error).Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", server.Port));
        await Assert.That(((RespireCircuitOpenException)error).RetryAfter).IsNotNull();
        await Assert.That(Volatile.Read(ref sends)).IsEqualTo(1);
        await Assert.That(CircuitOf(client, server).Snapshot().State).IsEqualTo(EndpointCircuitState.Open);
        // The rejected retry sent nothing, so it is neither an attempt nor a retried outcome.
        await Assert.That(events.IsEmpty).IsTrue();
    }

    [Test]
    [Arguments(false, "immediate")]
    [Arguments(false, "batch")]
    [Arguments(false, "transaction")]
    [Arguments(true, "immediate")]
    [Arguments(true, "batch")]
    [Arguments(true, "transaction")]
    public async Task RetryNeverResendsWhileEveryHalfOpenProbeIsTaken(bool cluster, string shape)
    {
        var events = new ConcurrentDictionary<string, long>();
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var listener = RetryListener(events);
        var sends = 0;
        var failing = 0;
        await using var server = CircuitUnitServer(shape, _ => Volatile.Read(ref failing) == 1
            && Interlocked.Increment(ref sends) == 1
                ? shape == "transaction" ? "*1\r\n-TRYAGAIN injected member error\r\n"u8.ToArray() : TryAgain
                : UnitValue(shape));
        await using var client = await RespireClient.ConnectAsync(CircuitRetryOptions(server, cluster));
        await Assert.That(await client.GetStringAsync("warm")).IsEqualTo("value");
        var circuit = CircuitOf(client, server);
        var clock = new CircuitClock();
        CircuitTimeProvider(circuit) = clock;
        OpenCircuit(client, server);
        clock.Advance(TimeSpan.FromMinutes(1));
        // Another operation owns one of the two recovery probes. The first attempt takes the other;
        // its TRYAGAIN reply proves health, so all recovery capacity is spoken for at the retry.
        var held = client.Core.Circuits!.Acquire(new("127.0.0.1", server.Port), default);
        try
        {
            Volatile.Write(ref failing, 1);
            var error = await Failure(async () => await ReadCircuitUnitAsync(client, shape, default));
            await Assert.That(error).IsTypeOf<RespireCircuitOpenException>();
            await Assert.That(((RespireCircuitOpenException)error).RetryAfter).IsNull();
            await Assert.That(Volatile.Read(ref sends)).IsEqualTo(1);
            var snapshot = circuit.Snapshot();
            await Assert.That(snapshot.State).IsEqualTo(EndpointCircuitState.HalfOpen);
            await Assert.That(snapshot.ActiveProbes).IsEqualTo(1);
            await Assert.That(snapshot.SuccessfulProbes).IsEqualTo(1);
            await Assert.That(events.IsEmpty).IsTrue();
        }
        finally { held.Dispose(); }
        // Releasing the held probe lets a later operation finish recovery with its own admission.
        await Assert.That(await client.GetStringAsync("after")).IsEqualTo("value");
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
    }

    [Test]
    [Arguments("immediate")]
    [Arguments("batch")]
    [Arguments("transaction")]
    public async Task SentinelRetryAcquiresFreshAdmissionOnTheReplacementPrimary(string shape)
    {
        var failed = 0;
        await using var replacement = CircuitUnitServer(shape, _ => UnitValue(shape));
        await using var former = CircuitUnitServer(shape, current =>
        {
            Volatile.Write(ref failed, 1);
            return Close(current);
        }, role: () => Volatile.Read(ref failed) == 0 ? "master" : "slave");
        await using var sentinel = Sentinel(() => Volatile.Read(ref failed) == 0 ? former.Port : replacement.Port);
        await using var client = await RespireClient.ConnectAsync(SentinelOptions(sentinel) with
        {
            CircuitBreaker = CircuitOptions(),
            RetryPolicy = new() { MaxAttempts = 3, InitialBackoff = TimeSpan.Zero },
        });
        await Assert.That(await ReadCircuitUnitAsync(client, shape, default)).IsEqualTo("value");
        await Assert.That(former.ReceivedCommands.Count(command => command == UnitCommand(shape))).IsEqualTo(1);
        await Assert.That(replacement.ReceivedCommands.Count(command => command == UnitCommand(shape))).IsEqualTo(1);
        // The former primary's failure opened only its own circuit; the replacement admitted the retry.
        await Assert.That(CircuitOf(client, former).Snapshot().State).IsEqualTo(EndpointCircuitState.Open);
        var fresh = CircuitOf(client, replacement).Snapshot();
        await Assert.That(fresh.State).IsEqualTo(EndpointCircuitState.Closed);
        await Assert.That(fresh.SampleCount).IsGreaterThanOrEqualTo(1);
        await Assert.That(fresh.FailureCount).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ClusterRetryRedirectAcquiresAdmissionOnTheTarget(bool targetOpen)
    {
        var events = new ConcurrentDictionary<string, long>();
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var listener = RetryListener(events);
        await using var target = ClusterNode((command, _) => command == "GET key" ? Value : null);
        var reads = 0;
        // "key" hashes to slot 12539. The source fails transiently, then the retry is redirected.
        await using var source = ClusterNode((command, _) => command == "GET key"
            ? Interlocked.Increment(ref reads) == 1 ? TryAgain : Encoding.ASCII.GetBytes($"-MOVED 12539 127.0.0.1:{target.Port}\r\n")
            : command.StartsWith("GET ", StringComparison.Ordinal) ? Value : null);
        await using var client = await RespireClient.ConnectAsync(ClusterOptions(source) with
        {
            CircuitBreaker = CircuitOptions(),
            RetryPolicy = new() { MaxAttempts = 3, InitialBackoff = TimeSpan.Zero },
        });
        await Assert.That(await client.GetStringAsync("warm")).IsEqualTo("value");
        if (targetOpen) OpenCircuit(client, target);
        if (targetOpen)
        {
            var error = await Failure(async () => await client.GetStringAsync("key"));
            await Assert.That(error).IsTypeOf<RespireCircuitOpenException>();
            await Assert.That(((RespireCircuitOpenException)error).Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", target.Port));
            await Assert.That(target.ReceivedCommands.Contains("GET key")).IsFalse();
            // The rejected target ended the operation without another attempt or retried outcome.
            await Assert.That(events.IsEmpty).IsTrue();
        }
        else
        {
            await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
            await Assert.That(target.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
            var fresh = CircuitOf(client, target).Snapshot();
            await Assert.That(fresh.State).IsEqualTo(EndpointCircuitState.Closed);
            await Assert.That(fresh.SampleCount).IsEqualTo(1);
            await Assert.That(events.GetValueOrDefault("attempted")).IsEqualTo(1L);
            await Assert.That(events.GetValueOrDefault("succeeded")).IsEqualTo(1L);
        }
        await Assert.That(reads).IsEqualTo(2);
    }

    [Test]
    [Arguments(true, "immediate")]
    [Arguments(true, "batch")]
    [Arguments(true, "transaction")]
    [Arguments(false, "immediate")]
    [Arguments(false, "batch")]
    [Arguments(false, "transaction")]
    public async Task ClusterRetryAcquiresFreshAdmissionOnTheNewSlotOwner(bool slotMoves, string shape)
    {
        var failedOver = 0;
        Task? stopping = null;
        await using var survivor = CircuitUnitServer(shape, _ => UnitValue(shape));
        await using var owner = CircuitUnitServer(shape, current =>
        {
            Volatile.Write(ref failedOver, 1);
            stopping = current.DisposeAsync().AsTask();
            current.CloseConnections();
            return null;
        });
        // Both nodes report the split map until the owner fails; a moved slot then names the survivor.
        byte[] Map() => Volatile.Read(ref failedOver) == 0 || !slotMoves ? SplitSlots(survivor.Port, owner.Port) : Slots(survivor.Port);
        var survivorReply = survivor.ReplyOverride!;
        survivor.ReplyOverride = (id, command) => command == "CLUSTER SLOTS" ? Map() : survivorReply(id, command);
        var ownerReply = owner.ReplyOverride!;
        owner.ReplyOverride = (id, command) => command == "CLUSTER SLOTS" ? Map() : ownerReply(id, command);
        await using var client = await RespireClient.ConnectAsync(ClusterOptions(owner) with
        {
            CommandTimeout = TimeSpan.FromSeconds(10),
            CircuitBreaker = CircuitOptions(),
            RetryPolicy = new() { MaxAttempts = 3, InitialBackoff = TimeSpan.Zero },
        });
        var decisive = UnitCommand(shape);
        if (!slotMoves)
        {
            // Without a topology change, the open owner rejects the retry after the refresh window.
            var error = await Failure(async () => await ReadCircuitUnitAsync(client, shape, default));
            if (stopping is not null) await stopping;
            await Assert.That(error).IsTypeOf<RespireCircuitOpenException>();
            await Assert.That(((RespireCircuitOpenException)error).Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", owner.Port));
            await Assert.That(owner.ReceivedCommands.Count(command => command == decisive)).IsEqualTo(1);
            await Assert.That(survivor.ReceivedCommands.Contains(decisive)).IsFalse();
            return;
        }
        await Assert.That(await ReadCircuitUnitAsync(client, shape, default)).IsEqualTo("value");
        if (stopping is not null) await stopping;
        await Assert.That(owner.ReceivedCommands.Count(command => command == decisive)).IsEqualTo(1);
        await Assert.That(survivor.ReceivedCommands.Count(command => command == decisive)).IsEqualTo(1);
        await Assert.That(CircuitOf(client, owner).Snapshot().State).IsEqualTo(EndpointCircuitState.Open);
        var fresh = CircuitOf(client, survivor).Snapshot();
        await Assert.That(fresh.State).IsEqualTo(EndpointCircuitState.Closed);
        await Assert.That(fresh.FailureCount).IsEqualTo(0);
        await Assert.That(fresh.SampleCount).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task ClusterRetryPreservesRejectionWhenOpenDurationExpiresDuringRefresh()
    {
        await using var survivor = CircuitUnitServer("immediate", _ => Value);
        await using var server = CircuitUnitServer("immediate", _ => Value);
        var survivorReply = survivor.ReplyOverride!;
        survivor.ReplyOverride = (id, command) => command == "CLUSTER SLOTS"
            ? SplitSlots(survivor.Port, server.Port) : survivorReply(id, command);
        var ownerReply = server.ReplyOverride!;
        server.ReplyOverride = (id, command) => command == "CLUSTER SLOTS"
            ? SplitSlots(survivor.Port, server.Port) : ownerReply(id, command);
        await using var client = await RespireClient.ConnectAsync(CircuitRetryOptions(server, cluster: true));
        var router = client.Core.Cluster!;
        var owner = router.GetKnownSlotOwner(12539)!;
        var clock = new CircuitClock();
        CircuitTimeProvider(CircuitOf(client, server)) = clock;
        var refresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        survivor.ReadGate = refresh.Task;
        server.ReadGate = handshake.Task;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await owner.GetConnection().DisposeAsync();
            OpenCircuit(client, server);
            var wait = router.WaitForRouteReplacementAsync(12539, cancellation.Token).AsTask();
            await Assert.That(wait.IsCompleted).IsFalse();
            clock.Advance(TimeSpan.FromMinutes(1));
            refresh.TrySetResult();
            var error = await Failure(async () => await wait);
            await Assert.That(error).IsTypeOf<RespireCircuitOpenException>();
            await Assert.That(((RespireCircuitOpenException)error).RetryAfter).IsEqualTo(TimeSpan.FromMinutes(1));
            await Assert.That(CircuitOf(client, server).Snapshot().State).IsEqualTo(EndpointCircuitState.Open);
            await Assert.That(server.ReceivedCommands.Contains("GET key")).IsFalse();
        }
        finally
        {
            cancellation.Cancel();
            refresh.TrySetResult();
            handshake.TrySetResult();
        }
    }

    [Test]
    public async Task ClusterRetryRefreshContinuesPastAStaleMaster()
    {
        await using var stale = CircuitUnitServer("immediate", _ => Value);
        await using var survivor = CircuitUnitServer("immediate", _ => Value);
        await using var server = CircuitUnitServer("immediate", _ => Value);
        byte[] Map(int highPort) => Encoding.ASCII.GetBytes(
            $"*3\r\n*3\r\n:0\r\n:4095\r\n*2\r\n$9\r\n127.0.0.1\r\n:{stale.Port}\r\n"
            + $"*3\r\n:4096\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{survivor.Port}\r\n"
            + $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{highPort}\r\n");
        var staleReply = stale.ReplyOverride!;
        stale.ReplyOverride = (id, command) => command == "CLUSTER SLOTS" ? Map(server.Port) : staleReply(id, command);
        var survivorReply = survivor.ReplyOverride!;
        survivor.ReplyOverride = (id, command) => command == "CLUSTER SLOTS" ? Map(survivor.Port) : survivorReply(id, command);
        var ownerReply = server.ReplyOverride!;
        server.ReplyOverride = (id, command) => command == "CLUSTER SLOTS" ? Map(server.Port) : ownerReply(id, command);
        await using var client = await RespireClient.ConnectAsync(CircuitRetryOptions(server, cluster: true));
        var router = client.Core.Cluster!;
        var owner = router.GetKnownSlotOwner(12539)!;
        var handshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ReadGate = handshake.Task;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            OpenCircuit(client, server);
            await owner.GetConnection().DisposeAsync();
            await router.WaitForRouteReplacementAsync(12539, cancellation.Token);
            await Assert.That(router.GetKnownSlotOwner(12539)!.ActiveConnectionEndpoint)
                .IsEqualTo(new RespireEndpoint("127.0.0.1", survivor.Port));
            await Assert.That(stale.ReceivedCommands.Contains("CLUSTER SLOTS")).IsTrue();
            await Assert.That(survivor.ReceivedCommands.Contains("CLUSTER SLOTS")).IsTrue();
            await Assert.That(server.ReceivedCommands.Contains("GET key")).IsFalse();
        }
        finally
        {
            cancellation.Cancel();
            handshake.TrySetResult();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FailoverGroupRetryNeedsFreshAdmissionOnTheActiveMember(bool switchMember)
    {
        var events = new ConcurrentDictionary<string, long>();
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var listener = RetryListener(events);
        var down = 0;
        FakeRespServer? primaryServer = null;
        await using var primary = new FakeRespServer(8, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) =>
            {
                if (command == "PING" && Volatile.Read(ref down) == 1) return "-ERR down\r\n"u8.ToArray();
                if (command != "GET key") return null;
                primaryServer!.CloseConnections();
                return null;
            },
        };
        primaryServer = primary;
        await using var secondary = new FakeRespServer(8, FakeRespServer.PongReply)
        { ReplyOverride = (_, command) => command == "GET key" ? Value : null };
        await using var group = await RespireFailoverGroup.ConnectAsync(
            [CircuitCandidate(primary, 0), CircuitCandidate(secondary, 1)], GroupOptions() with
            { RetryPolicy = new() { MaxAttempts = 3, InitialBackoff = TimeSpan.Zero } });
        var clients = new List<RespireClient>();
        var secondarySamples = -1;
        static int Samples(IRespireClient client, FakeRespServer server)
            => ((RespireClient)client).Core.Circuits!.GetForTests(new("127.0.0.1", server.Port)).Snapshot().SampleCount;
        ValueTask<string?> Execute() => group.ExecuteAsync(async (client, token) =>
        {
            clients.Add((RespireClient)client);
            try { return await client.GetStringAsync("key", token); }
            catch (RespireConnectionException) when (switchMember && clients.Count == 1)
            {
                // The probe moves the group while this attempt still holds the failed member.
                Volatile.Write(ref down, 1);
                await group.ForTests.ProbeAsync(0, token);
                await group.ForTests.SelectActiveAsync();
                secondarySamples = Samples(group.ActiveClient, secondary);
                throw;
            }
        }, RespireCommandRetryCategory.ReadOnly);
        var primaryEndpoint = new RespireEndpoint("127.0.0.1", primary.Port);
        if (switchMember)
        {
            await Assert.That(await Execute()).IsEqualTo("value");
            await Assert.That(primary.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
            await Assert.That(clients[0].Core.Circuits!.GetForTests(primaryEndpoint).Snapshot().State).IsEqualTo(EndpointCircuitState.Open);
            await Assert.That(ReferenceEquals(clients[^1], group.ActiveClient)).IsTrue();
            var fresh = clients[^1].Core.Circuits!.GetForTests(new("127.0.0.1", secondary.Port)).Snapshot();
            await Assert.That(fresh.State).IsEqualTo(EndpointCircuitState.Closed);
            await Assert.That(fresh.FailureCount).IsEqualTo(0);
            // The retry acquired, and completed, its own admission on the newly active member.
            await Assert.That(fresh.SampleCount).IsEqualTo(secondarySamples + 1);
            await Assert.That(secondary.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
            await Assert.That(events.GetValueOrDefault("attempted")).IsEqualTo(1L);
            await Assert.That(events.GetValueOrDefault("succeeded")).IsEqualTo(1L);
        }
        else
        {
            var error = await Failure(async () => await Execute());
            await Assert.That(primary.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
            await Assert.That(clients[0].Core.Circuits!.GetForTests(primaryEndpoint).Snapshot().State).IsEqualTo(EndpointCircuitState.Open);
            await Assert.That(error).IsTypeOf<RespireCircuitOpenException>();
            await Assert.That(((RespireCircuitOpenException)error).Endpoint).IsEqualTo(primaryEndpoint);
            await Assert.That(clients.TrueForAll(client => ReferenceEquals(client, clients[0]))).IsTrue();
            // The group cannot see inside the operation, which may have run other commands before
            // the member rejected one, so the rejected resend still counts as attempted.
            await Assert.That(clients.Count).IsEqualTo(2);
            await Assert.That(events.GetValueOrDefault("attempted")).IsEqualTo(1L);
            await Assert.That(events.GetValueOrDefault("exhausted")).IsEqualTo(1L);
            await Assert.That(events.ContainsKey("succeeded")).IsFalse();
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task DeclaredScriptRetryCountsARejectionOnlyAfterItsResendStarted(bool cluster, bool rejectFallback)
    {
        var events = new ConcurrentDictionary<string, long>();
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var listener = RetryListener(events);
        var sends = 0;
        RespireClient? client = null;
        Func<string, FakeRespServer, byte[]?> reply = (command, current) =>
        {
            if (!command.StartsWith("EVALSHA ", StringComparison.Ordinal)) return null;
            if (Interlocked.Increment(ref sends) == 1)
            {
                if (rejectFallback) return TryAgain;
                current.CloseConnections();
                return null;
            }
            // The resend's EVALSHA executed; the circuit opens before its EVAL fallback is admitted.
            OpenCircuit(client!, current);
            return "-NOSCRIPT No matching script.\r\n"u8.ToArray();
        };
        await using var server = cluster ? ClusterNode(reply) : Standalone(reply);
        client = await RespireClient.ConnectAsync(CircuitRetryOptions(server, cluster));
        await using var owned = client;
        var script = RespireScript.Create("return 7").WithRetryCategory(RespireCommandRetryCategory.ReadOnly);
        var error = await Failure(async () => await client.Scripts.ExecuteIntegerAsync(script, ["key"]));
        await Assert.That(error).IsTypeOf<RespireCircuitOpenException>();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("EVAL ", StringComparison.Ordinal))).IsFalse();
        if (rejectFallback || cluster)
        {
            // The resend ran EVALSHA before its fallback was rejected, so it counts as attempted. Cluster
            // routing admits the resend inside the operation, so a rejection before any send counts too.
            await Assert.That(Volatile.Read(ref sends)).IsEqualTo(rejectFallback ? 2 : 1);
            await Assert.That(events.GetValueOrDefault("attempted")).IsEqualTo(1L);
            await Assert.That(events.GetValueOrDefault("exhausted")).IsEqualTo(1L);
            await Assert.That(events.ContainsKey("succeeded")).IsFalse();
        }
        else
        {
            // The failure opened the circuit, so the resend's preparation rejected it before any send.
            await Assert.That(Volatile.Read(ref sends)).IsEqualTo(1);
            await Assert.That(events.IsEmpty).IsTrue();
        }
    }

    private static RespireCircuitBreakerOptions CircuitOptions() => new()
    {
        MinimumFailureCount = 1, FailureRateThreshold = 0.01, HalfOpenProbeCount = 2, OpenDuration = TimeSpan.FromMinutes(1),
    };

    private static RespireOptions CircuitRetryOptions(FakeRespServer server, bool cluster)
        => (cluster ? ClusterOptions(server) : StandaloneOptions(server) with { Connections = 1 }) with
        {
            CircuitBreaker = CircuitOptions(),
            RetryPolicy = new() { MaxAttempts = 3, InitialBackoff = TimeSpan.Zero },
        };

    private static RespireFailoverCandidate CircuitCandidate(FakeRespServer server, int priority)
    {
        var candidate = Candidate(server, priority);
        return candidate with { Options = candidate.Options with { CircuitBreaker = CircuitOptions() } };
    }

    private static EndpointCircuitBreaker CircuitOf(RespireClient client, FakeRespServer server)
        => client.Core.Circuits!.GetForTests(new("127.0.0.1", server.Port));

    // Records one endpoint failure directly, opening the circuit without a real timeout.
    private static void OpenCircuit(RespireClient client, FakeRespServer server)
    {
        var admission = client.Core.Circuits!.Acquire(new("127.0.0.1", server.Port), default);
        try { admission.Failed(new RespireConnectionException("Injected endpoint failure."), default); }
        finally { admission.Dispose(); }
    }

    private static string UnitCommand(string shape) => shape == "transaction" ? "EXEC" : "GET key";
    private static byte[] UnitValue(string shape) => shape == "transaction" ? "*1\r\n$5\r\nvalue\r\n"u8.ToArray() : Value;

    // Serves standalone, Sentinel-primary and single-node Cluster roles. The unit's decisive command
    // (GET key, or EXEC for a transaction) is answered by the test; warm-up reads return a value.
    private static FakeRespServer CircuitUnitServer(string shape, Func<FakeRespServer, byte[]?> reply, Func<string>? role = null)
    {
        var server = new FakeRespServer(32, FakeRespServer.OkReply);
        var transactions = new ConcurrentDictionary<int, bool>();
        server.ReplyOverride = (id, command) =>
        {
            if (command == "CLIENT ID") return ":1\r\n"u8.ToArray();
            if (command == "CLUSTER SLOTS") return Slots(server.Port);
            if (command == "ROLE") return RoleReply(role?.Invoke() ?? "master");
            if (command == "MULTI") { transactions[id] = true; return FakeRespServer.OkReply; }
            if (command == "EXEC") { transactions[id] = false; return reply(server); }
            if (transactions.GetValueOrDefault(id)) return "+QUEUED\r\n"u8.ToArray();
            if (command == "GET key") return shape == "transaction" ? Value : reply(server);
            return command.StartsWith("GET ", StringComparison.Ordinal) ? Value : null;
        };
        return server;
    }

    private static async ValueTask<string?> ReadCircuitUnitAsync(IRespireClient client, string shape, CancellationToken token)
        => shape == "immediate" ? await client.GetStringAsync("key", token) : await ReadUnitAsync(client, shape == "transaction", token);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_clock")]
    private static extern ref TimeProvider CircuitTimeProvider(EndpointCircuitBreaker circuit);

    private sealed class CircuitClock : TimeProvider
    {
        private long _timestamp = TimeProvider.System.GetTimestamp();
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;
        internal void Advance(TimeSpan duration) => Interlocked.Add(ref _timestamp, (long)(duration.TotalSeconds * TimestampFrequency));
    }
}
