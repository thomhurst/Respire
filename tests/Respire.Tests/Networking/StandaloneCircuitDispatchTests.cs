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
public class StandaloneCircuitDispatchTests
{
    [Test]
    public async Task CacheHitsBypassAdmissionAndRejectedMissSendsNoTrackingPrelude()
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            Protocol = RespProtocol.Resp3, ClientSideCache = new(),
        });
        var (circuit, clock) = await Prepare(client, server);
        await client.GetStringAsync("cached");
        await Trip(client, server);
        var commands = server.CommandsSeen;
        await Assert.That(await client.GetStringAsync("cached")).IsEqualTo("cached");
        await Assert.That(await Failure(() => Send(client, "string", "missing"))).IsTypeOf<RespireCircuitOpenException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(commands);
        clock.Advance(TimeSpan.FromMinutes(1));
        await Task.WhenAll(Send(client, "string", "full-1"), Send(client, "string", "full-2"));
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WireTransportFailuresOpenCircuitAndFailedProbeStartsNewDelay(bool protocol)
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var (circuit, clock) = await Prepare(client, server);
        if (!protocol) server.CloseConnectionAfterCommand = server.CommandsSeen + 1;
        var error = await Failure(() => Send(client, "string", protocol ? "malformed" : "disconnect"));
        await Assert.That(error is RespireConnectionException or RespireProtocolException).IsTrue();
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Open);
        await Assert.That(await Failure(() => Send(client, "raw", "rejected"))).IsTypeOf<RespireCircuitOpenException>();
        // Recovery is demand-driven and independent of circuit timing; no application
        // command retries while the replacement socket is being established.
        client.Core.Multiplexer.ScheduleReconnect(0);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!client.Core.Multiplexer.IsConnected) await Task.Delay(1, deadline.Token);
        clock.Advance(TimeSpan.FromMinutes(1));
        await Trip(client, server);
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Open);
        await Assert.That(await Failure(() => Send(client, "string", "rejected-again"))).IsTypeOf<RespireCircuitOpenException>();
        clock.Advance(TimeSpan.FromMinutes(1));
        await Task.WhenAll(Send(client, "string", "full-1"), Send(client, "raw", "full-2"));
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
    }

    [Test]
    [Arguments("string")]
    [Arguments("bytes")]
    [Arguments("integer")]
    [Arguments("raw")]
    [Arguments("fire-and-forget")]
    public async Task OpenEndpointRejectsWithoutWriting(string shape)
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var (circuit, clock) = await Prepare(client, server);
        await Trip(client, server);
        var commands = server.CommandsSeen;
        var error = await Failure(() => Send(client.WithKeyPrefix("view:"), shape, "rejected"));
        await Assert.That(error).IsTypeOf<RespireCircuitOpenException>();
        var rejected = (RespireCircuitOpenException)error;
        await Assert.That(rejected.Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", server.Port));
        await Assert.That(rejected.RetryAfter).IsEqualTo(TimeSpan.FromMinutes(1));
        await Assert.That(server.CommandsSeen).IsEqualTo(commands);
        clock.Advance(TimeSpan.FromMinutes(1));
        await Task.WhenAll(Send(client, shape, "recovery"), Send(client, "string", "recovery-2"));
        // Discarded fire-and-forget replies do not close a recovery probe.
        if (shape == "fire-and-forget") await Send(client, "string", "recovery-3");
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
        await Assert.That(server.ReceivedCommands.Any(command => command.Contains("rejected", StringComparison.Ordinal))).IsFalse();
        await Assert.That(server.ReceivedCommands.Count(command => command == "GET fail")).IsEqualTo(1);
    }

    [Test]
    public async Task ConcurrentHalfOpenProbesRejectOverflowAndRecoverInFifoOrder()
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var (circuit, clock) = await Prepare(client, server);
        await Trip(client, server);
        clock.Advance(TimeSpan.FromMinutes(1));
        server.SuppressReply = command => command.StartsWith("GET probe", StringComparison.Ordinal);
        var first = client.GetStringAsync("probe-1").AsTask();
        var second = client.ExecuteAsync("GET", "probe-2").AsTask();
        await Received(server, "GET probe-2");
        await Assert.That(circuit.Snapshot().ActiveProbes).IsEqualTo(2);
        await Assert.That(await Failure(() => Send(client, "string", "overflow"))).IsTypeOf<RespireCircuitOpenException>();
        await server.SendRawAsync("$3\r\none\r\n$3\r\ntwo\r\n"u8.ToArray());
        await Assert.That(await first).IsEqualTo("one");
        using (var reply = await second) await Assert.That(reply.AsString()).IsEqualTo("two");
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
        server.SuppressReply = null;
        await Assert.That(await client.GetStringAsync("after")).IsEqualTo("after");
        await Assert.That(server.ReceivedCommands.Any(command => command.Contains("overflow", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    [Arguments("cancel")]
    [Arguments("pre-cancel")]
    [Arguments("writer")]
    [Arguments("capacity-timeout")]
    public async Task IgnoredExitPathsReleaseRecoveryCapacity(string exit)
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server) with { MaxInflightCommands = 1 });
        var (circuit, clock) = await Prepare(client, server);
        await Trip(client, server);
        clock.Advance(TimeSpan.FromMinutes(1));
        server.SuppressReply = command => command == "GET parked";
        using var cancellation = new CancellationTokenSource();
        Task? parked = null;
        if (exit is "cancel" or "capacity-timeout")
        {
            parked = exit == "capacity-timeout"
                ? HoldCapacity()
                : Send(client, "string", "parked", cancellation.Token);
            await Received(server, "GET parked");
        }
        async Task HoldCapacity()
        {
            using var reply = await client.Core.Multiplexer.GetConnection().SendWithoutResponseTimeoutAsync(
                new Cmd1(Verbs.Get, "parked"), cancellation.Token);
        }
        switch (exit)
        {
            case "cancel":
                cancellation.Cancel();
                await Assert.That(await Failure(() => parked!)).IsTypeOf<OperationCanceledException>();
                break;
            case "pre-cancel":
                cancellation.Cancel();
                await Assert.That(await Failure(() => Send(client, "string", "never", cancellation.Token))).IsTypeOf<OperationCanceledException>();
                break;
            case "writer":
                await Assert.That(await Failure(async () => { using var reply = await client.SendAsync("GET", new ThrowingCommand(), default); }))
                    .IsTypeOf<IOException>();
                break;
            case "capacity-timeout":
                await Assert.That(await Failure(() => Send(client, "raw", "never"))).IsTypeOf<RespireTimeoutException>();
                cancellation.Cancel();
                await Failure(() => parked!);
                break;
        }
        await Assert.That(circuit.Snapshot().ActiveProbes).IsEqualTo(0);
        if (parked is not null) await server.SendRawAsync("$4\r\nlate\r\n"u8.ToArray());
        server.SuppressReply = null;
        // Every ignored path leaves enough capacity for the entire later probe batch.
        await Assert.That(await client.GetStringAsync("full-1")).IsEqualTo("full-1");
        await Assert.That(await client.GetStringAsync("full-2")).IsEqualTo("full-2");
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
        await Assert.That(server.ReceivedCommands.Any(command => command.Contains("never", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task RedisErrorsAreHealthyOutcomesAndValidationDoesNotEnterCircuit()
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var (circuit, clock) = await Prepare(client, server);
        await Assert.That(await Failure(() => Send(client, "string", "wrongtype"))).IsTypeOf<RespireServerException>();
        await Assert.That(circuit.Snapshot().FailureCount).IsEqualTo(0);
        await Trip(client, server);
        clock.Advance(TimeSpan.FromMinutes(1));
        await Assert.That(await Failure(async () => await client.ExecuteFireAndForgetAsync("BLPOP", "x", "1"))).IsTypeOf<NotSupportedException>();
        await Assert.That(circuit.Snapshot().ActiveProbes).IsEqualTo(0);
        await Assert.That(await Failure(() => Send(client, "string", "wrongtype"))).IsTypeOf<RespireServerException>();
        await Send(client, "raw", "healthy");
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
    }

    [Test]
    public async Task RejectedDispatchDoesNotChangeBatchOrTransactionAdmission()
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var (circuit, _) = await Prepare(client, server);
        await Trip(client, server);
        using var batch = client.CreateBatch();
        var pending = batch.Strings.GetString("batch");
        await batch.ExecuteAsync();
        await Assert.That(await pending).IsEqualTo("batch");
        await using var transaction = client.CreateTransaction();
        var transactional = transaction.Strings.GetString("transaction");
        await transaction.CommitAsync();
        await Assert.That(await transactional).IsEqualTo("transaction");
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Open);
    }

    [Test]
    [Arguments("stream")]
    [Arguments("upload")]
    [Arguments("blocking")]
    public async Task DedicatedCommandsRejectBeforeTheirApplicationFrame(string shape)
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server));
        await Prepare(client, server);
        await Trip(client, server);
        var error = await Failure(async () =>
        {
            if (shape == "stream") await using (var stream = await client.Strings.GetStreamAsync("rejected")) { }
            else if (shape == "upload")
            {
                using var payload = new MemoryStream("value"u8.ToArray());
                await client.Strings.SetAsync("rejected", payload, 5);
            }
            else using (var result = await client.ExecuteAsync("BLPOP", "rejected", "1")) { }
        });
        await Assert.That(error).IsTypeOf<RespireCircuitOpenException>();
        await Assert.That(server.ReceivedCommands.Any(command => command.Contains("rejected", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    [Arguments("cancel")]
    [Arguments("dispose")]
    [Arguments("read-cancel")]
    public async Task StreamCancellationOrDisposalReleasesIncompleteFrameProbe(string exit)
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var (circuit, clock) = await Prepare(client, server);
        await Trip(client, server);
        clock.Advance(TimeSpan.FromMinutes(1));
        server.SuppressReply = command => command == "GET stream";
        using var cancellation = new CancellationTokenSource();
        var pending = client.Strings.GetStreamAsync("stream", cancellation.Token).AsTask();
        await Received(server, "GET stream");
        await server.SendRawAsync("$100\r\nx"u8.ToArray());
        await using var stream = await pending;
        await Assert.That(circuit.Snapshot().ActiveProbes).IsEqualTo(1);
        if (exit == "cancel") cancellation.Cancel();
        else if (exit == "dispose") await stream!.DisposeAsync();
        else
        {
            var buffer = new byte[100];
            await Assert.That(await stream!.ReadAsync(buffer)).IsEqualTo(1);
            using var readCancellation = new CancellationTokenSource();
            var read = stream.ReadAsync(buffer, readCancellation.Token);
            readCancellation.Cancel();
            await Assert.That(await Failure(async () => await read)).IsTypeOf<OperationCanceledException>();
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (circuit.Snapshot().ActiveProbes != 0) await Task.Delay(1, deadline.Token);
        // Complete the discarded frame before later replies: the same connection keeps FIFO.
        await server.SendRawAsync(Encoding.UTF8.GetBytes(new string('x', 99) + "\r\n"));
        server.SuppressReply = null;
        await Send(client, "string", "full-1");
        await Send(client, "string", "full-2");
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
    }

    [Test]
    public async Task StreamFrameCompletionRacesCancellationAndDisposalWithoutCompletingReplacementProbes()
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var (circuit, clock) = await Prepare(client, server);
        for (var i = 0; i < 8; i++)
        {
            await Trip(client, server);
            clock.Advance(TimeSpan.FromMinutes(1));
            server.SuppressReply = command => command == "GET stream";
            using var cancellation = new CancellationTokenSource();
            var pending = client.Strings.GetStreamAsync("stream", cancellation.Token).AsTask();
            await Received(server, "GET stream", i + 1);
            await server.SendRawAsync("$2\r\nx"u8.ToArray());
            await using var stream = await pending;
            await Task.WhenAll(server.SendRawAsync("y\r\n"u8.ToArray()), Task.Run(async () =>
            {
                cancellation.Cancel();
                await stream!.DisposeAsync();
            }));
            await Assert.That(circuit.Snapshot().ActiveProbes).IsEqualTo(0);
            server.SuppressReply = null;
            // If frame completion wins, it has already satisfied one probe. At most
            // one more concurrent admission is allowed until the circuit closes.
            await Send(client, "string", "race-full-1");
            await Send(client, "raw", "race-full-2");
            await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
        }
    }

    [Test]
    public async Task TopologyConfigurationsRejectCircuitOption()
    {
        foreach (var options in new[]
        {
            new RespireOptions { Endpoints = [new("localhost")], UseCluster = true },
            new RespireOptions { Endpoints = [new("localhost")], SentinelPrimaryName = "primary" },
            new RespireOptions { Endpoints = [new("localhost")], ReplicaEndpoints = [new("replica")] },
        })
            await Assert.That(() => RespireClient.Create(options with { CircuitBreaker = new() }))
                .Throws<RespireConfigurationException>();
        await Assert.That(() => RespireClient.Create(new RespireOptions { Endpoints = [new("localhost")], CircuitBreaker = new() { HalfOpenProbeCount = 0 } }))
            .Throws<ArgumentOutOfRangeException>();
    }

    private static FakeRespServer Server() => new(32, FakeRespServer.OkReply)
    {
        ReplyOverride = (_, command) => command switch
        {
            _ when command.StartsWith("HELLO", StringComparison.Ordinal) => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
            "CLIENT ID" => ":1\r\n"u8.ToArray(),
            "GET wrongtype" => "-WRONGTYPE expected string\r\n"u8.ToArray(),
            "GET malformed" => ":invalid\r\n"u8.ToArray(),
            "GET transaction" => "+QUEUED\r\n"u8.ToArray(),
            _ when command.StartsWith("GET ", StringComparison.Ordinal) => Bulk(command[4..]),
            _ when command.StartsWith("STRLEN ", StringComparison.Ordinal) => ":5\r\n"u8.ToArray(),
            "MULTI" => FakeRespServer.OkReply,
            "EXEC" => "*1\r\n$11\r\ntransaction\r\n"u8.ToArray(),
            _ => null,
        },
    };

    private static byte[] Bulk(string value) => Encoding.UTF8.GetBytes($"${Encoding.UTF8.GetByteCount(value)}\r\n{value}\r\n");

    private static RespireOptions Options(FakeRespServer server) => new()
    {
        Endpoints = [new("127.0.0.1", server.Port)], Connections = 1, Protocol = RespProtocol.Resp2,
        ThreadPoolMonitoring = false, CommandTimeout = TimeSpan.FromMilliseconds(200),
        CircuitBreaker = new() { MinimumFailureCount = 1, FailureRateThreshold = 0.25, HalfOpenProbeCount = 2, OpenDuration = TimeSpan.FromMinutes(1) },
    };

    private static async Task<(EndpointCircuitBreaker Circuit, Clock Clock)> Prepare(RespireClient client, FakeRespServer server)
    {
        await client.GetStringAsync("warm");
        var circuit = client.Core.Circuits!.GetForTests(new("127.0.0.1", server.Port));
        var clock = new Clock();
        CircuitClock(circuit) = clock;
        return (circuit, clock);
    }

    private static async Task Trip(RespireClient client, FakeRespServer server)
    {
        server.SuppressReply = command => command == "GET fail";
        await Assert.That(await Failure(() => Send(client, "raw", "fail"))).IsTypeOf<RespireTimeoutException>();
        await server.SendRawAsync("$4\r\nlate\r\n"u8.ToArray(), server.ReceivedConnectionIds[^1]);
        server.SuppressReply = null;
    }

    private static async Task Send(IRespireClient client, string shape, string key, CancellationToken cancellationToken = default)
    {
        switch (shape)
        {
            case "string": await client.GetStringAsync(key, cancellationToken); break;
            case "bytes": await client.GetBytesAsync(key, cancellationToken); break;
            case "integer": await client.Strings.LengthAsync(key, cancellationToken); break;
            case "raw": using (var result = await client.ExecuteAsync("GET", [key], cancellationToken: cancellationToken)) { } break;
            case "fire-and-forget": await client.ExecuteFireAndForgetAsync("GET", [key], cancellationToken: cancellationToken); break;
        }
    }

    private static async Task<Exception> Failure(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected dispatch failure.");
    }

    private static async Task Received(FakeRespServer server, string command, int occurrences = 1)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.ReceivedCommands.Count(received => received == command) < occurrences) await Task.Delay(1, deadline.Token);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_clock")]
    private static extern ref TimeProvider CircuitClock(EndpointCircuitBreaker circuit);

    private sealed class Clock : TimeProvider
    {
        private long _timestamp = TimeProvider.System.GetTimestamp();
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;
        internal void Advance(TimeSpan duration) => Interlocked.Add(ref _timestamp, (long)(duration.TotalSeconds * TimestampFrequency));
    }

    private readonly struct ThrowingCommand : IRespCommand
    {
        public ReadCommandKind ReadKind => ReadCommandKind.None;
        public void Write(ref RespWriter writer) => throw new IOException("Application command writer failed before dispatch.");
    }
}
