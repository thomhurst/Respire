using System.Runtime.CompilerServices;
using System.Text;
using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

[NotInParallel]
public class ClusterCircuitDispatchTests
{
    [Test]
    [Arguments("string")]
    [Arguments("bytes")]
    [Arguments("integer")]
    [Arguments("converted")]
    [Arguments("raw")]
    [Arguments("fire-and-forget")]
    [Arguments("batch")]
    [Arguments("transaction")]
    public async Task OpenPrimaryRejectsEveryDispatchShapeWhileOtherPrimaryWorks(string shape)
    {
        using var metrics = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.None });
        await using var source = Server();
        await using var target = Server();
        await using var client = await RespireClient.ConnectAsync(Options(source));
        await Send(client, "string", "{open}warm");
        var router = client.Core.Cluster!;
        router.SetSlotOwner(ClusterHash.GetSlot("{healthy}key"), router.GetMultiplexer(Endpoint(target)));
        Open(client, source);
        var error = await Failure(() => Send(client, shape, "{open}rejected"));
        await Assert.That(error).IsTypeOf<RespireCircuitOpenException>();
        var rejection = (RespireCircuitOpenException)error;
        await Assert.That(rejection.Endpoint).IsEqualTo(Endpoint(source));
        await Assert.That(rejection.RetryAfter > TimeSpan.Zero).IsTrue();
        await Assert.That(source.ReceivedCommands.Any(command => command.Contains("rejected", StringComparison.Ordinal))).IsFalse();
        await Assert.That(source.ReceivedCommands.Contains("MULTI")).IsFalse();
        await Send(client, shape, "{healthy}key");
        await Received(target, shape == "integer" ? "STRLEN {healthy}key" : "GET {healthy}key");
        await Assert.That(Circuit(client, source).Snapshot().State).IsEqualTo(EndpointCircuitState.Open);
        await Assert.That(Circuit(client, target).Snapshot().FailureCount).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PrimaryAndReplicaAdmissionAreIndependent(bool openReplica)
    {
        await using var primary = Server();
        await using var replica = Server();
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? Slots(primary.Port, replica.Port) : Reply(command);
        await using var client = await RespireClient.ConnectAsync(Options(primary));
        var replicaClient = client.WithReadFrom(RespireReadFrom.Replica);
        await client.GetStringAsync("warm");
        await replicaClient.GetStringAsync("warm");
        Open(client, openReplica ? replica : primary);
        IRespireClient rejected = openReplica ? replicaClient : client;
        IRespireClient healthy = openReplica ? client : replicaClient;
        var error = await Failure(async () => await rejected.GetStringAsync("rejected"));
        await Assert.That(error).IsTypeOf<RespireCircuitOpenException>();
        await Assert.That(((RespireCircuitOpenException)error).Endpoint).IsEqualTo(Endpoint(openReplica ? replica : primary));
        await Assert.That(await healthy.GetStringAsync("healthy")).IsEqualTo("value");
        await Assert.That((openReplica ? replica : primary).ReceivedCommands.Contains("GET rejected")).IsFalse();
        await Assert.That((openReplica ? primary : replica).ReceivedCommands.Contains("GET healthy")).IsTrue();
    }

    [Test]
    [Arguments("MOVED", "string", false)]
    [Arguments("MOVED", "converted", true)]
    [Arguments("MOVED", "batch", true)]
    [Arguments("MOVED", "transaction", true)]
    [Arguments("ASK", "raw", false)]
    [Arguments("ASK", "bytes", true)]
    [Arguments("ASK", "batch", true)]
    public async Task RedirectAcquiresTargetAdmission(string code, string shape, bool targetOpen)
    {
        using var metrics = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.None });
        await using var source = Server();
        await using var target = Server();
        await using var client = await RespireClient.ConnectAsync(Options(source));
        await client.GetStringAsync("warm");
        if (targetOpen) Open(client, target);
        var key = "redirect";
        var redirect = Encoding.ASCII.GetBytes($"-{code} {ClusterHash.GetSlot(key)} 127.0.0.1:{target.Port}\r\n");
        source.ReplyOverride = (_, command) => shape == "transaction" ? command switch
        {
            "MULTI" => FakeRespServer.OkReply,
            "GET redirect" => "+QUEUED\r\n"u8.ToArray(),
            "EXEC" => redirect,
            "CLUSTER SLOTS" => Slots(source.Port),
            _ => Reply(command),
        } : command == "GET redirect" ? redirect
            : command == "CLUSTER SLOTS" ? Slots(source.Port) : Reply(command);
        if (targetOpen)
        {
            var error = await Failure(() => Send(client, shape, key));
            await Assert.That(error).IsTypeOf<RespireCircuitOpenException>();
            await Assert.That(((RespireCircuitOpenException)error).Endpoint).IsEqualTo(Endpoint(target));
        }
        else await Send(client, shape, key);
        await Assert.That(target.ReceivedCommands.Contains("GET redirect")).IsEqualTo(!targetOpen);
        await Assert.That(target.ReceivedCommands.Contains("ASKING")).IsEqualTo(code == "ASK" && !targetOpen);
        await Assert.That(target.ReceivedCommands.Contains("MULTI")).IsFalse();
        await Assert.That(Circuit(client, source).Snapshot().FailureCount).IsEqualTo(0);
        await Assert.That(source.ReceivedCommands.Count(command => command == "GET redirect")).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RetiredGenerationRequiresReplacementAdmission(bool targetOpen)
    {
        await using var source = Server();
        await using var target = Server();
        await using var client = await RespireClient.ConnectAsync(Options(source));
        await client.GetStringAsync("warm");
        if (targetOpen) Open(client, target);
        var router = client.Core.Cluster!;
        var original = await router.GetConnectionAsync(ClusterHash.GetSlot("retired"), default, null);
        router.SetSlotOwner(ClusterHash.GetSlot("retired"), router.GetMultiplexer(Endpoint(target)));
        await original.Multiplexer!.RetireAsync();
        var command = new Cmd1(Verbs.Get, "retired");
        var send = client.ResumeRetiredClusterSendAsync("GET", command, original,
            new RespireConnectionRetiredException("127.0.0.1", source.Port), RespireReadFrom.Primary, default);
        if (targetOpen)
        {
            await Assert.That(await Failure(async () => { using var response = await send; })).IsTypeOf<RespireCircuitOpenException>();
        }
        else
        {
            using var response = await send;
            await Assert.That(ResponseReader.StringOrNull(in response)).IsEqualTo("value");
        }
        await Assert.That(source.ReceivedCommands.Contains("GET retired")).IsFalse();
        await Assert.That(target.ReceivedCommands.Contains("GET retired")).IsEqualTo(!targetOpen);
        await Assert.That(Circuit(client, source).Snapshot().FailureCount).IsEqualTo(0);
    }

    [Test]
    [Arguments("string", false)]
    [Arguments("batch", false)]
    [Arguments("transaction", false)]
    [Arguments("string", true)]
    [Arguments("batch", true)]
    [Arguments("transaction", true)]
    public async Task CancelledOrFailedRecoveryReleasesCapacityForFullBatch(string shape, bool timeout)
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            CommandTimeout = timeout ? TimeSpan.FromMilliseconds(300) : TimeSpan.FromSeconds(10),
        });
        await client.GetStringAsync("warm");
        var circuit = Circuit(client, server);
        var clock = new Clock();
        CircuitClock(circuit) = clock;
        Open(client, server);
        clock.Advance();
        server.SuppressReply = command => command == (shape == "transaction" ? "EXEC" : "GET probe");
        using var cancellation = new CancellationTokenSource();
        var pending = Send(client, shape, "probe", cancellation.Token);
        await Received(server, shape == "transaction" ? "EXEC" : "GET probe");
        if (!timeout) cancellation.Cancel();
        var error = await Failure(async () => await pending);
        await Assert.That(error.GetType()).IsEqualTo(timeout ? typeof(RespireTimeoutException) : typeof(OperationCanceledException));
        await Assert.That(circuit.Snapshot().ActiveProbes).IsEqualTo(0);
        // Keep the accepted response placeholder, then drain it before dispatching the next batch.
        await server.SendRawAsync(shape == "transaction" ? "*1\r\n$5\r\nvalue\r\n"u8.ToArray() : Bulk,
            server.ReceivedConnectionIds[^1]);
        // Suppressed EXEC did not invoke the fake server's transaction state transition.
        server.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? Slots(server.Port) : Reply(command);
        server.SuppressReply = null;
        if (timeout) clock.Advance();
        using var batch = client.CreateBatch();
        var first = batch.GetString("{full}first");
        var second = batch.GetString("{full}second");
        await batch.ExecuteAsync();
        await Assert.That(await first).IsEqualTo("value");
        await Assert.That(await second).IsEqualTo("value");
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
        await Assert.That(server.ReceivedCommands.Count(command => command == "GET probe")).IsEqualTo(1);
    }

    [Test]
    public async Task ConverterFailureKeepsHealthyOutcomeAndNeverReplays()
    {
        using var metrics = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.None });
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server));
        await client.GetStringAsync("warm");
        var circuit = Circuit(client, server);
        CircuitClock(circuit) = new Clock();
        Open(client, server);
        ((Clock)CircuitClock(circuit)).Advance();
        var expected = new RespireServerException($"MOVED {ClusterHash.GetSlot("convert")} 127.0.0.1:{server.Port}");
        var error = await Failure(async () => await client.ConvertResponseAsync<Cmd1, RespireServerException, int>(
            "GET", new Cmd1(Verbs.Get, "convert"), default,
            expected, static (RespireServerException error, in RespValue _) => throw error));
        await Assert.That(error).IsSameReferenceAs(expected);
        await Assert.That(circuit.Snapshot().SuccessfulProbes).IsEqualTo(1);
        await Assert.That(circuit.Snapshot().ActiveProbes).IsEqualTo(0);
        await Assert.That(server.ReceivedCommands.Count(command => command == "GET convert")).IsEqualTo(1);
    }

    [Test]
    public async Task LiveClusterNodesKeepOpenHistoryBeyondStandaloneIdleLimit()
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            UseCluster = true, Endpoints = [new("127.0.0.1", 9000)], CircuitBreaker = new() { MinimumFailureCount = 1 },
            ThreadPoolMonitoring = false,
        });
        var circuits = client.Core.Circuits!;
        var endpoints = Enumerable.Range(0, 32).Select(i => new RespireEndpoint("127.0.0.1", 9000 + i)).ToArray();
        foreach (var endpoint in endpoints)
        {
            client.Core.Cluster!.GetMultiplexer(endpoint);
            var admission = circuits.Acquire(endpoint, default);
            try { admission.Failed(new RespireConnectionException("Injected failure."), default); }
            finally { admission.Dispose(); }
        }
        await Assert.That(circuits.CountForTests).IsEqualTo(32);
        foreach (var endpoint in endpoints)
            await Assert.That(() => circuits.Acquire(endpoint, default)).Throws<RespireCircuitOpenException>();
    }

    private static readonly byte[] Bulk = "$5\r\nvalue\r\n"u8.ToArray();
    private static byte[]? Reply(string command) => command.StartsWith("GET ", StringComparison.Ordinal) ? Bulk
        : command.StartsWith("STRLEN ", StringComparison.Ordinal) ? ":5\r\n"u8.ToArray() : null;

    private static FakeRespServer Server()
    {
        var server = new FakeRespServer(32, FakeRespServer.OkReply);
        var inTransaction = false;
        server.ReplyOverride = (_, command) =>
        {
            if (command == "CLUSTER SLOTS") return Slots(server.Port);
            if (command == "MULTI") { inTransaction = true; return FakeRespServer.OkReply; }
            if (command == "EXEC") { inTransaction = false; return "*1\r\n$5\r\nvalue\r\n"u8.ToArray(); }
            if (inTransaction) return "+QUEUED\r\n"u8.ToArray();
            return Reply(command);
        };
        return server;
    }

    private static byte[] Slots(int primary, int? replica = null) => Encoding.ASCII.GetBytes(
        $"*1\r\n*{(replica is null ? 3 : 4)}\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{primary}\r\n"
        + (replica is { } port ? $"*2\r\n$9\r\n127.0.0.1\r\n:{port}\r\n" : ""));

    private static RespireOptions Options(FakeRespServer server) => new()
    {
        Endpoints = [Endpoint(server)], UseCluster = true, Connections = 1, Protocol = RespProtocol.Resp2,
        ThreadPoolMonitoring = false, ClusterTopologyRefreshInterval = null, CommandTimeout = TimeSpan.FromSeconds(5),
        CircuitBreaker = new() { MinimumFailureCount = 1, FailureRateThreshold = 0.01,
            HalfOpenProbeCount = 2, OpenDuration = TimeSpan.FromMinutes(1) },
    };

    private static RespireEndpoint Endpoint(FakeRespServer server) => new("127.0.0.1", server.Port);
    private static EndpointCircuitBreaker Circuit(RespireClient client, FakeRespServer server)
        => client.Core.Circuits!.GetForTests(Endpoint(server));
    private static void Open(RespireClient client, FakeRespServer server)
    {
        var admission = client.Core.Circuits!.Acquire(Endpoint(server), default);
        try { admission.Failed(new RespireConnectionException("Injected node failure."), default); }
        finally { admission.Dispose(); }
    }

    private static async Task Send(RespireClient client, string shape, string key, CancellationToken cancellationToken = default)
    {
        switch (shape)
        {
            case "string": await client.GetStringAsync(key, cancellationToken); break;
            case "bytes": await client.GetBytesAsync(key, cancellationToken); break;
            case "integer": await client.Strings.LengthAsync(key, cancellationToken); break;
            case "converted": await client.ConvertResponseAsync("GET", new Cmd1(Verbs.Get, key), cancellationToken,
                0, static (int _, in RespValue response) => ResponseReader.StringOrNull(in response)); break;
            case "raw": using (var response = await client.ExecuteAsync("GET", [key], cancellationToken: cancellationToken)) { } break;
            case "fire-and-forget": await client.ExecuteFireAndForgetAsync("GET", [key], cancellationToken: cancellationToken); break;
            case "batch":
                using (var batch = client.CreateBatch())
                {
                    var pending = batch.GetString(key);
                    await batch.ExecuteAsync(cancellationToken);
                    await Assert.That(await pending).IsEqualTo("value");
                }
                break;
            case "transaction":
                await using (var transaction = client.CreateTransaction())
                {
                    var pending = transaction.GetString(key);
                    await transaction.CommitAsync(cancellationToken);
                    await Assert.That(await pending).IsEqualTo("value");
                }
                break;
            default: throw new ArgumentOutOfRangeException(nameof(shape));
        }
    }

    private static async Task<Exception> Failure(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected dispatch failure.");
    }

    private static async Task Received(FakeRespServer server, string command)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!server.ReceivedCommands.Contains(command)) await Task.Delay(1, timeout.Token);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_clock")]
    private static extern ref TimeProvider CircuitClock(EndpointCircuitBreaker circuit);
    private sealed class Clock : TimeProvider
    {
        private long _timestamp = TimeProvider.System.GetTimestamp();
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;
        internal void Advance() => Interlocked.Add(ref _timestamp, TimestampFrequency * 60);
    }
}
