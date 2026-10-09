using System.Diagnostics;
using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public partial class StandaloneCircuitDispatchTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task QueueSelectionFailureOpensCircuitBeforeDispatch(bool transactional)
    {
        await using var server = QueueServer();
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            ReconnectPolicy = new() { InitialDelay = TimeSpan.FromMinutes(1), MaxDelay = TimeSpan.FromMinutes(1) },
        });
        var (circuit, clock) = await Prepare(client, server);
        var connection = client.Core.Multiplexer.GetConnection();
        server.CloseConnections();
        await connection.Closed.WaitAsync(TimeSpan.FromSeconds(5));
        var commands = server.CommandsSeen;
        await Assert.That(await Execute()).IsTypeOf<RespireConnectionException>();
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Open);
        await Assert.That(await Execute()).IsTypeOf<RespireCircuitOpenException>();
        clock.Advance(TimeSpan.FromMinutes(1));
        await Assert.That(await Execute()).IsTypeOf<RespireConnectionException>();
        await Assert.That(circuit.Snapshot().ActiveProbes).IsEqualTo(0);
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Open);
        await Assert.That(server.CommandsSeen).IsEqualTo(commands);

        async Task<Exception> Execute()
        {
            using var batch = client.CreateBatch();
            await using var transaction = client.CreateTransaction();
            IRespireCommandQueue queue = transactional ? transaction : batch;
            var pending = queue.Strings.GetString("never");
            var error = await Failure(async () =>
            {
                if (transactional) await transaction.CommitAsync();
                else await batch.ExecuteAsync();
            });
            await Assert.That(pending.Error).IsSameReferenceAs(error);
            return error;
        }
    }

    [Test]
    public async Task WatchSetupRejectsOpenCircuitAndCompletesHalfOpenProbe()
    {
        await using var server = QueueServer();
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var (circuit, clock) = await Prepare(client, server);
        Open(client, server);
        await Assert.That(await Failure(async () =>
        {
            await using var rejected = await client.CreateTransactionAsync(["watched"]);
        })).IsTypeOf<RespireCircuitOpenException>();
        await Assert.That(server.ReceivedCommands.Contains("WATCH watched")).IsFalse();
        clock.Advance(TimeSpan.FromMinutes(1));
        await using var accepted = await client.CreateTransactionAsync(["watched"]);
        await Assert.That(circuit.Snapshot().ActiveProbes).IsEqualTo(0);
        await Assert.That(circuit.Snapshot().SuccessfulProbes).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands.Count(command => command == "WATCH watched")).IsEqualTo(1);
    }

    [Test]
    public async Task QueuedReplyReleasesProbeBeforeCallerConsumesResult()
    {
        await using var server = QueueServer();
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var (circuit, clock) = await Prepare(client, server);
        Open(client, server);
        clock.Advance(TimeSpan.FromMinutes(1));
        server.SuppressReply = command => command == "GET probe";
        var reply = await QueuedCircuitDispatch.EnqueueAsync(client.Core.Circuits!,
            client.Core.Multiplexer.GetConnection(), new Cmd1(Verbs.Get, "probe"), "GET", default);
        await Received(server, "GET probe");
        await Assert.That(circuit.Snapshot().ActiveProbes).IsEqualTo(1);
        await server.SendRawAsync(Bulk("probe"));
        var deadline = Stopwatch.StartNew();
        while (circuit.Snapshot().ActiveProbes != 0)
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Reply observer did not release its probe.");
            await Task.Yield();
        }
        await Assert.That(circuit.Snapshot().SuccessfulProbes).IsEqualTo(1);
        using var response = await reply;
        await Assert.That(ResponseReader.StringOrNull(in response)).IsEqualTo("probe");
    }

    [Test]
    [Arguments("batch")]
    [Arguments("transaction")]
    [Arguments("watched")]
    [Arguments("import-batch")]
    [Arguments("import-transaction")]
    [Arguments("durability")]
    public async Task QueuesAcquireAtExecutionAndOpenRejectionWritesNothing(string shape)
    {
        await using var server = QueueServer();
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var (circuit, clock) = await Prepare(client, server);
        // WATCH and the import lease exist before the circuit opens. Queuing itself takes no permit.
        await using var session = shape.StartsWith("import", StringComparison.Ordinal)
            ? await client.Hashes.CreateImportSessionAsync() : null;
        await using RespireTransactionBase transaction = shape == "watched" ? await client.CreateTransactionAsync(["watched"])
            : shape == "import-transaction" ? session!.CreateTransaction() : client.CreateTransaction();
        using var batch = shape == "import-batch" ? session!.CreateBatch() : client.CreateBatch();
        IRespireCommandQueue queue = shape.Contains("transaction", StringComparison.Ordinal) || shape == "watched" ? transaction : batch;
        await Trip(client, server);
        var commands = server.CommandsSeen;
        var pending = session is null ? queue.Strings.GetString("transaction") : null;
        var prepared = session is not null ? queue.Hashes.PrepareImport("schema", "field") : null;
        await Assert.That(circuit.Snapshot().ActiveProbes).IsEqualTo(0);
        var error = await Failure(async () =>
        {
            if (ReferenceEquals(queue, transaction)) await CommitQueue(transaction);
            else if (shape == "durability") await batch.ExecuteAndWaitForReplicationAsync(1, TimeSpan.FromSeconds(1));
            else await batch.ExecuteAsync();
        });
        await Assert.That(error).IsTypeOf<RespireCircuitOpenException>();
        await Assert.That(pending?.Error ?? prepared!.Error).IsTypeOf<RespireCircuitOpenException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(commands);
        await Assert.That(circuit.Snapshot().ActiveProbes).IsEqualTo(0);
        clock.Advance(TimeSpan.FromMinutes(1));
        // Rejection must not expire connection-local fieldsets on an import session.
        if (session is not null) await Assert.That(await session.PrepareAsync("schema", "field")).IsTrue();
        await client.GetStringAsync("full-1");
        await client.GetStringAsync("full-2");
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
    }

    [Test]
    public async Task HalfOpenBatchRejectsOnlyUnadmittedEntriesAndPreservesAcceptedFifo()
    {
        await using var server = QueueServer();
        await using var client = await RespireClient.ConnectAsync(Options(server) with { CommandTimeout = TimeSpan.FromSeconds(10) });
        var (circuit, clock) = await Prepare(client, server);
        Open(client, server);
        clock.Advance(TimeSpan.FromMinutes(1));
        using var batch = client.CreateBatch();
        var first = batch.GetString("probe-1");
        var second = batch.GetString("probe-2");
        var rejected = batch.GetString("never");
        server.SuppressReply = command => command.StartsWith("GET probe-", StringComparison.Ordinal);
        var executing = batch.TryExecuteAsync().AsTask();
        await Received(server, "GET probe-2");
        await Assert.That(circuit.Snapshot().ActiveProbes).IsEqualTo(2);
        await Assert.That(await Failure(async () => await client.GetStringAsync("also-never"))).IsTypeOf<RespireCircuitOpenException>();
        await server.SendRawAsync("$5\r\nfirst\r\n$6\r\nsecond\r\n"u8.ToArray());
        var result = await executing;
        await Assert.That(result.Failures.Count).IsEqualTo(1);
        await Assert.That(await first).IsEqualTo("first");
        await Assert.That(await second).IsEqualTo("second");
        await Assert.That(rejected.Error).IsTypeOf<RespireCircuitOpenException>();
        await Assert.That(server.ReceivedCommands.Any(command => command.Contains("never", StringComparison.Ordinal))).IsFalse();
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
        await Assert.That(await client.GetStringAsync("after")).IsEqualTo("after");
    }

    [Test]
    [Arguments("batch", false)]
    [Arguments("batch", true)]
    [Arguments("transaction", false)]
    [Arguments("transaction", true)]
    public async Task CanceledQueueReleasesProbesAndLaterRepliesKeepFifo(string shape, bool beforeDispatch)
    {
        await using var server = QueueServer();
        await using var client = await RespireClient.ConnectAsync(Options(server) with { CommandTimeout = TimeSpan.FromSeconds(10) });
        var (circuit, clock) = await Prepare(client, server);
        Open(client, server);
        clock.Advance(TimeSpan.FromMinutes(1));
        using var cancellation = new CancellationTokenSource();
        if (beforeDispatch) cancellation.Cancel();
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        IRespireCommandQueue queue = shape == "batch" ? batch : transaction;
        var pending = queue.Strings.GetString("transaction");
        server.SuppressReply = command => command == (shape == "batch" ? "GET transaction" : "EXEC");
        var executing = Failure(async () =>
        {
            if (shape == "batch") await batch.ExecuteAsync(cancellation.Token);
            else await transaction.CommitAsync(cancellation.Token);
        });
        if (!beforeDispatch)
        {
            await Received(server, shape == "batch" ? "GET transaction" : "EXEC");
            await Assert.That(circuit.Snapshot().ActiveProbes).IsEqualTo(1);
            cancellation.Cancel();
        }
        await Assert.That(await executing).IsTypeOf<OperationCanceledException>();
        await Assert.That(pending.Error).IsTypeOf<OperationCanceledException>();
        await Assert.That(circuit.Snapshot().ActiveProbes).IsEqualTo(0);
        if (!beforeDispatch)
            await server.SendRawAsync(shape == "batch" ? Bulk("late") : "*1\r\n$4\r\nlate\r\n"u8.ToArray());
        else
            await Assert.That(server.ReceivedCommands.Contains("GET transaction")).IsFalse();
        server.SuppressReply = null;
        server.ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal) ? Bulk(command[4..]) : null;
        await Assert.That(await client.GetStringAsync("full-1")).IsEqualTo("full-1");
        await Assert.That(await client.GetStringAsync("full-2")).IsEqualTo("full-2");
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
    }

    [Test]
    public async Task ConcurrentTransactionsBoundHalfOpenSequences()
    {
        await using var server = QueueServer();
        await using var client = await RespireClient.ConnectAsync(Options(server) with { CommandTimeout = TimeSpan.FromSeconds(10) });
        var (circuit, clock) = await Prepare(client, server);
        Open(client, server);
        clock.Advance(TimeSpan.FromMinutes(1));
        server.SuppressReply = command => command is "MULTI" or "EXEC" or "GET transaction";
        await using var first = client.CreateTransaction();
        await using var second = client.CreateTransaction();
        await using var rejected = client.CreateTransaction();
        var firstResult = first.GetString("transaction");
        var secondResult = second.GetString("transaction");
        var never = rejected.GetString("never");
        var firstCommit = first.CommitAsync().AsTask();
        var secondCommit = second.CommitAsync().AsTask();
        await Received(server, "EXEC", 2);
        await Assert.That(circuit.Snapshot().ActiveProbes).IsEqualTo(2);
        await Assert.That(await Failure(async () => await rejected.CommitAsync())).IsTypeOf<RespireCircuitOpenException>();
        await Assert.That(never.Error).IsTypeOf<RespireCircuitOpenException>();
        // Prefix replies for the second sequence also sit behind the first EXEC in FIFO.
        // QueueServer suppresses all transaction replies; inject each complete sequence here.
        await server.SendRawAsync("+OK\r\n+QUEUED\r\n*1\r\n$5\r\nfirst\r\n+OK\r\n+QUEUED\r\n*1\r\n$6\r\nsecond\r\n"u8.ToArray());
        await Task.WhenAll(firstCommit, secondCommit);
        await Assert.That(await firstResult).IsEqualTo("first");
        await Assert.That(await secondResult).IsEqualTo("second");
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
        await Assert.That(server.ReceivedCommands.Contains("GET never")).IsFalse();
    }

    [Test]
    public async Task EmptyQueuesAndFailedDispatchLeaveFullRecoveryCapacity()
    {
        await using var server = QueueServer();
        await using var client = await RespireClient.ConnectAsync(Options(server) with { MaxInflightCommands = 3 });
        var (circuit, clock) = await Prepare(client, server);
        Open(client, server);
        clock.Advance(TimeSpan.FromMinutes(1));
        using var batch = client.CreateBatch();
        await batch.ExecuteAsync();
        await using var empty = client.CreateTransaction();
        await empty.CommitAsync();
        await using var oversized = client.CreateTransaction();
        var first = oversized.GetString("transaction");
        var second = oversized.GetString("transaction");
        var third = oversized.GetString("transaction");
        await Assert.That(await Failure(async () => await oversized.CommitAsync())).IsTypeOf<ArgumentOutOfRangeException>();
        await Assert.That(first.Error).IsTypeOf<ArgumentOutOfRangeException>();
        await Assert.That(second.Error).IsTypeOf<ArgumentOutOfRangeException>();
        await Assert.That(third.Error).IsTypeOf<ArgumentOutOfRangeException>();
        // A writer exception must release admission before propagating its source failure.
        using var failedBatch = client.CreateBatch();
        var writerFailure = ((IPendingSink)failedBatch).Add<ThrowingCommand, bool>("GET", new ThrowingCommand(), static (_, _) => true);
        await Assert.That(await Failure(async () => await failedBatch.ExecuteAsync())).IsTypeOf<IOException>();
        await Assert.That(writerFailure.Error).IsTypeOf<IOException>();
        await Assert.That(circuit.Snapshot().ActiveProbes).IsEqualTo(0);
        await Assert.That(circuit.Snapshot().SuccessfulProbes).IsEqualTo(0);
        await client.GetStringAsync("full-1");
        await client.GetStringAsync("full-2");
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
    }

    [Test]
    [Arguments("batch")]
    [Arguments("transaction")]
    public async Task QueueTimeoutOpensCircuitAndLateReplyAllowsFullRecovery(string shape)
    {
        await using var server = QueueServer();
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var (circuit, clock) = await Prepare(client, server);
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        IRespireCommandQueue queue = shape == "batch" ? batch : transaction;
        var pending = queue.Strings.GetString("transaction");
        server.SuppressReply = command => command == (shape == "batch" ? "GET transaction" : "EXEC");
        var error = await Failure(async () =>
        {
            if (shape == "batch") await batch.ExecuteAsync();
            else await transaction.CommitAsync();
        });
        await Assert.That(error).IsTypeOf<RespireTimeoutException>();
        await Assert.That(pending.Error).IsTypeOf<RespireTimeoutException>();
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Open);
        await Assert.That(circuit.Snapshot().ActiveProbes).IsEqualTo(0);
        await server.SendRawAsync(shape == "batch" ? Bulk("late") : "*1\r\n$4\r\nlate\r\n"u8.ToArray());
        server.SuppressReply = null;
        server.ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal) ? Bulk(command[4..]) : null;
        clock.Advance(TimeSpan.FromMinutes(1));
        await Assert.That(await client.GetStringAsync("full-1")).IsEqualTo("full-1");
        await Assert.That(await client.GetStringAsync("full-2")).IsEqualTo("full-2");
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
    }

    [Test]
    [Arguments("batch")]
    [Arguments("transaction")]
    [Arguments("ordered")]
    public async Task CapacityTimeoutBeforeQueueDispatchReleasesIgnoredProbe(string shape)
    {
        await using var server = QueueServer();
        await using var client = await RespireClient.ConnectAsync(Options(server) with { MaxInflightCommands = 4 });
        var (circuit, clock) = await Prepare(client, server);
        Open(client, server);
        clock.Advance(TimeSpan.FromMinutes(1));
        server.SuppressReply = command => command.StartsWith("GET held-", StringComparison.Ordinal);
        var connection = client.Core.Multiplexer.GetConnection();
        using var cancellation = new CancellationTokenSource();
        var holders = Enumerable.Range(0, 4).Select(async index =>
        {
            using var reply = await connection.SendWithoutResponseTimeoutAsync(new Cmd1(Verbs.Get, $"held-{index}"), cancellation.Token);
        }).ToArray();
        await Received(server, "GET held-3");
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        var error = await Failure(async () =>
        {
            if (shape == "batch") { _ = batch.GetString("never"); await batch.ExecuteAsync(); }
            else if (shape == "transaction") { _ = transaction.GetString("never"); await transaction.CommitAsync(); }
            else
            {
                var response = await QueuedCircuitDispatch.EnqueueAsync(client.Core.Circuits!, connection,
                    new Cmd1(Verbs.Get, "never"), "GET", default);
                using var reply = await response;
            }
        });
        await Assert.That(error).IsTypeOf<RespireTimeoutException>();
        await Assert.That(circuit.Snapshot().ActiveProbes).IsEqualTo(0);
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.HalfOpen);
        cancellation.Cancel();
        foreach (var holder in holders) await Failure(() => holder);
        await server.SendRawAsync("$1\r\na\r\n$1\r\nb\r\n$1\r\nc\r\n$1\r\nd\r\n"u8.ToArray());
        server.SuppressReply = null;
        await client.GetStringAsync("full-1");
        await client.GetStringAsync("full-2");
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
        await Assert.That(server.ReceivedCommands.Contains("GET never")).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TransactionAfterMaintenancePublicationUsesActualEndpointCircuit(bool targetOpen)
    {
        await using var target = QueueServer();
        await using var source = QueueServer();
        await using var client = await RespireClient.ConnectAsync(Options(source) with
        {
            Protocol = RespProtocol.Resp3, MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled,
            CommandTimeout = TimeSpan.FromSeconds(10),
        });
        var (sourceCircuit, _) = await Prepare(client, source);
        var multiplexer = client.Core.Multiplexer;
        var original = multiplexer.GetConnection();
        Open(client, source);
        var endpoint = new RespireEndpoint("127.0.0.1", target.Port);
        var targetAdmission = client.Core.Circuits!.Acquire(endpoint, default);
        if (targetOpen) targetAdmission.Failed(new RespireConnectionException("Injected target failure."), default);
        targetAdmission.Dispose();
        var announcement = multiplexer.CaptureMovingAnnouncement(0, original,
            new MaintenanceNotification("MOVING", 1, 10, endpoint));
        using var published = new ManualResetEventSlim();
        multiplexer.MovingHandoffPublished += published.Set;
        var handoffs = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Respire",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity =>
            {
                if (activity.OperationName != "GET" || Interlocked.Increment(ref handoffs) != 1) return;
                multiplexer.QueueDedicatedMovingHandoff(original, announcement, static () => true);
                if (!published.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Transaction handoff did not publish.");
            },
        };
        ActivitySource.AddActivityListener(listener);
        var commands = source.CommandsSeen;
        await using var transaction = client.CreateTransaction();
        var pending = transaction.GetString("transaction");
        if (targetOpen)
        {
            var error = await Failure(async () => await transaction.CommitAsync());
            await Assert.That(error).IsTypeOf<RespireCircuitOpenException>();
            await Assert.That(((RespireCircuitOpenException)error).Endpoint).IsEqualTo(endpoint);
        }
        else
        {
            await transaction.CommitAsync();
            await Assert.That(await pending).IsEqualTo("transaction");
            await Assert.That(transaction.ExecutingConnection!.Port).IsEqualTo(target.Port);
        }
        await Assert.That(handoffs).IsEqualTo(1);
        await Assert.That(source.CommandsSeen).IsEqualTo(commands);
        await Assert.That(target.ReceivedCommands.Count(command => command == "MULTI")).IsEqualTo(targetOpen ? 0 : 1);
        await Assert.That(sourceCircuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Open);
        await Assert.That(sourceCircuit.Snapshot().ActiveProbes).IsEqualTo(0);
        await Assert.That(client.Core.Circuits.GetForTests(endpoint).Snapshot().ActiveProbes).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task QueueConversionFailureCompletesHealthyReplyAdmission(bool transactional)
    {
        await using var server = QueueServer();
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var (circuit, clock) = await Prepare(client, server);
        Open(client, server);
        clock.Advance(TimeSpan.FromMinutes(1));
        server.ReplyOverride = (_, command) => command switch
        {
            "GET invalid" => transactional ? "+QUEUED\r\n"u8.ToArray() : Bulk("bad"),
            "EXEC" => "*1\r\n$3\r\nbad\r\n"u8.ToArray(),
            _ when command.StartsWith("GET ", StringComparison.Ordinal) => Bulk(command[4..]),
            _ => null,
        };
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        IRespireCommandQueue queue = transactional ? transaction : batch;
        var pending = ((IPendingSink)queue).Add<Cmd1, long>("GET", new Cmd1(Verbs.Get, "invalid"),
            static (_, _) => throw new FormatException("Injected response conversion failure."));
        if (transactional) await transaction.CommitAsync();
        else await Assert.That(await Failure(async () => await batch.ExecuteAsync())).IsTypeOf<FormatException>();
        await Assert.That(pending.Error).IsTypeOf<FormatException>();
        await Assert.That(circuit.Snapshot().ActiveProbes).IsEqualTo(0);
        await Assert.That(circuit.Snapshot().SuccessfulProbes).IsEqualTo(1);
        await Assert.That(circuit.Snapshot().FailureCount).IsEqualTo(0);
        await client.GetStringAsync("full");
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
    }

    private static void Open(RespireClient client, FakeRespServer server)
    {
        var admission = client.Core.Circuits!.Acquire(new("127.0.0.1", server.Port), default);
        try { admission.Failed(new RespireConnectionException("Injected endpoint failure."), default); }
        finally { admission.Dispose(); }
    }

    private static async Task CommitQueue(RespireTransactionBase transaction)
    {
        if (transaction is RespireWatchedTransaction watched) await watched.CommitAsync();
        else await ((RespireTransaction)transaction).CommitAsync();
    }

    private static FakeRespServer QueueServer()
    {
        var server = Server();
        var transaction = false;
        server.ReplyOverride = (_, command) =>
        {
            if (command.StartsWith("HELLO", StringComparison.Ordinal)) return "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray();
            if (command == "CLIENT ID") return ":1\r\n"u8.ToArray();
            if (command == "MULTI") { transaction = true; return FakeRespServer.OkReply; }
            if (command == "EXEC") { transaction = false; return "*1\r\n$11\r\ntransaction\r\n"u8.ToArray(); }
            if (transaction) return "+QUEUED\r\n"u8.ToArray();
            if (command.StartsWith("GET ", StringComparison.Ordinal)) return Bulk(command[4..]);
            return null;
        };
        return server;
    }
}
