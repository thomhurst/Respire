using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Respire.Internal;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

[NotInParallel]
public class DeferredExecutionOwnershipTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UnwatchedNullExecFaultsEveryPendingWithOriginalProtocolError(bool commandMetrics)
    {
        using var configuration = Configure(commandMetrics);
        await using var server = new FakeRespServer(FakeRespServer.OkReply,
            "+QUEUED\r\n"u8.ToArray(), "+QUEUED\r\n"u8.ToArray(), "*-1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var transaction = client.CreateTransaction();
        var first = transaction.Set("one", "value");
        var second = transaction.Set("two", "value");
        using var capture = new Capture(commandMetrics);
        var error = await Assert.That(async () => await transaction.CommitAsync()).Throws<RespireProtocolException>();
        await Assert.That(first.Status).IsEqualTo(RespirePendingStatus.Faulted);
        await Assert.That(ReferenceEquals(first.Error, error)).IsTrue();
        await Assert.That(ReferenceEquals(second.Error, error)).IsTrue();
        await InspectFailure(first, error!);
        await InspectFailure(second, error!);
        await Assert.That(capture.Items.Count).IsEqualTo(2);
        await Assert.That(capture.Items.All(item => !item.Internal && item.Attempts == 0
            && item.Type == typeof(RespireProtocolException).FullName)).IsTrue();
    }

    [Test]
    [MatrixDataSource]
    public async Task DeferredRoutesKeepPhysicalDisconnectSeparateFromPendingFailures(
        [Matrix(false, true)] bool transaction,
        [Matrix("idle", "pending", "cancelled", "success")] string outcome,
        [Matrix(false, true)] bool commandMetrics)
    {
        using var configuration = Configure(commandMetrics);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "EXEC" => "*2\r\n+OK\r\n+OK\r\n"u8.ToArray(),
                _ when transaction && command.StartsWith("SET ", StringComparison.Ordinal)
                    => "+QUEUED\r\n"u8.ToArray(),
                _ => null,
            },
            SuppressReply = command =>
            {
                var ready = transaction ? command == "EXEC"
                    : command.StartsWith("SET ", StringComparison.Ordinal) && Interlocked.Increment(ref writes) == 2;
                if (ready) received.TrySetResult();
                return outcome is "pending" or "cancelled"
                    && (transaction ? command == "EXEC" : command.StartsWith("SET ", StringComparison.Ordinal));
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var connection = client.Core.Multiplexer.GetConnection();
        using var batch = transaction ? null : client.CreateBatch();
        await using var commit = transaction ? client.CreateTransaction() : null;
        var queue = (IRespireCommandQueue?)commit ?? batch!;
        var first = queue.Set("one", "value");
        var second = queue.Set("two", "value");
        using var capture = new Capture(commandMetrics);
        using var cancellation = new CancellationTokenSource();
        RespireBatchResult result = default;
        Exception? executionError = null;
        async Task Execute()
        {
            try
            {
                if (commit is not null) await commit.CommitAsync(cancellation.Token);
                else result = await batch!.TryExecuteAsync(cancellation.Token);
            }
            catch (Exception error) { executionError = error; }
        }
        Task? execution = null;
        if (outcome != "idle")
        {
            execution = Execute();
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (outcome == "cancelled") cancellation.Cancel();
            if (outcome is "cancelled" or "success") await execution.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await server.DisposeAsync();
        await connection.Closed.WaitAsync(TimeSpan.FromSeconds(5));
        if (execution is not null) await execution.WaitAsync(TimeSpan.FromSeconds(5));
        if (outcome is "pending" or "cancelled")
        {
            await Assert.That(first.Status).IsEqualTo(RespirePendingStatus.Faulted);
            await Assert.That(second.Status).IsEqualTo(RespirePendingStatus.Faulted);
            if (transaction) await Assert.That(ReferenceEquals(executionError, first.Error)).IsTrue();
            else
            {
                await Assert.That(result.FailureCount).IsEqualTo(2);
                await Assert.That(ReferenceEquals(result.FirstError, first.Error)).IsTrue();
                var thrown = await Assert.That(result.ThrowIfAnyFailed).Throws<Exception>();
                await Assert.That(ReferenceEquals(thrown, first.Error)).IsTrue();
            }
            // Concurrent inspection is independent of the execution owner and never republishes.
            await Task.WhenAll(InspectFailure(first, first.Error!), InspectFailure(first, first.Error!),
                InspectFailure(second, second.Error!));
            if (outcome == "cancelled")
            {
                await Assert.That(((OperationCanceledException)first.Error!).CancellationToken).IsEqualTo(cancellation.Token);
                await Assert.That(((OperationCanceledException)second.Error!).CancellationToken).IsEqualTo(cancellation.Token);
            }
            else await Assert.That(first.Error).IsTypeOf<RespireConnectionException>();
        }
        else if (outcome == "success")
        {
            await Assert.That(first.Result).IsTrue();
            await Assert.That(second.Result).IsTrue();
        }
        else await Assert.That(first.Status).IsEqualTo(RespirePendingStatus.Pending);
        var items = capture.Items.ToArray();
        await Assert.That(items.Count(item => item.Internal)).IsEqualTo(1);
        await Assert.That(items.Count(item => !item.Internal)).IsEqualTo(outcome is "pending" or "cancelled" ? 2 : 0);
        await Assert.That(items.All(item => item.Attempts == 0)).IsTrue();
    }

    [Test]
    [MatrixDataSource]
    public async Task ExecutionPublishesCompletedNestedRetriesWithoutSharingIndependentPendingOwners(
        [Matrix(false, true)] bool transaction, [Matrix(false, true)] bool success)
    {
        using var configuration = Configure(commandMetrics: false);
        var reply = success ? FakeRespServer.OkReply : "-WRONGTYPE terminal\r\n"u8.ToArray();
        await using var server = new FakeRespServer(transaction
            ? [FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), "+QUEUED\r\n"u8.ToArray(),
                [.. "*2\r\n"u8, .. reply, .. reply]] : [reply, reply]);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var batch = transaction ? null : client.CreateBatch();
        await using var commit = transaction ? client.CreateTransaction() : null;
        var queue = (IRespireCommandQueue?)commit ?? batch!;
        var first = queue.Set("one", "value");
        var second = queue.Set("two", "value");
        using var capture = new Capture(commandMetrics: false);
        var outer = first.Observation;
        var nested = outer;
        outer.Retry();
        nested.Handled(new IOException("recovered outer"));
        nested.Retry();
        if (!transaction) second.Observation.Handled(new IOException("independent retry"));
        var beforeExecution = capture.Items.Count;
        var rentals = 0;
        ErrorObservation.RentalObserverForTests = () => rentals++;
        try
        {
            if (commit is not null) await commit.CommitAsync();
            else await batch!.TryExecuteAsync();
        }
        finally { ErrorObservation.RentalObserverForTests = null; }
        await Assert.That(rentals).IsEqualTo(success ? 0 : transaction ? 1 : 0);
        await Assert.That(outer.Attempts).IsEqualTo(3);
        await Assert.That(second.Observation.Attempts).IsEqualTo(transaction ? 3 : 1);
        outer.Handled(new IOException("too late"));
        nested.Retry();
        if (success)
        {
            await Assert.That(first.Result).IsTrue();
            await Assert.That(second.Result).IsTrue();
        }
        else
        {
            await InspectFailure(first, first.Error!);
            await InspectFailure(second, second.Error!);
        }
        var final = capture.Items.Skip(beforeExecution).ToArray();
        await Assert.That(final.Length).IsEqualTo(success ? 0 : 2);
        if (!success)
            await Assert.That(final.Select(item => item.Attempts).ToArray()).IsEquivalentTo(transaction ? new[] { 3, 3 } : new[] { 3, 1 });
        await Assert.That(final.All(item => !item.Internal)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DiscardedQueuesPublishOnlyAtFirstInspectionOfEachPending(bool transaction)
    {
        using var configuration = Configure(commandMetrics: false);
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespireProtocol.Resp2,
            Endpoints = [new("127.0.0.1", 6379)],
        });
        using var capture = new Capture(commandMetrics: false);
        var batch = transaction ? null : client.CreateBatch();
        var commit = transaction ? client.CreateTransaction() : null;
        var queue = (IRespireCommandQueue?)commit ?? batch!;
        var first = queue.Set("one", "value");
        var second = queue.Set("two", "value");
        if (commit is not null) await commit.DisposeAsync();
        else batch!.Dispose();
        await Assert.That(capture.Items.Count).IsEqualTo(0);
        await Task.WhenAll(InspectFailure(first, first.Error!), InspectFailure(first, first.Error!),
            InspectFailure(second, second.Error!));
        await Assert.That(capture.Items.Count).IsEqualTo(2);
        await Assert.That(capture.Items.All(item => !item.Internal && item.Attempts == 0)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SuccessfulExecutionDoesNotRentErrorObservationStorage(bool transaction)
    {
        using var configuration = Configure(commandMetrics: false);
        await using var server = new FakeRespServer(transaction
            ? [FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), "*1\r\n+OK\r\n"u8.ToArray()]
            : [FakeRespServer.OkReply]);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var batch = transaction ? null : client.CreateBatch();
        await using var commit = transaction ? client.CreateTransaction() : null;
        var queue = (IRespireCommandQueue?)commit ?? batch!;
        var pending = queue.Set("key", "value");
        var rentals = 0;
        ErrorObservation.RentalObserverForTests = () => rentals++;
        try
        {
            if (commit is not null) await commit.CommitAsync();
            else await batch!.ExecuteAsync();
            await Assert.That(pending.Result).IsTrue();
            await Assert.That(rentals).IsEqualTo(0);
            // Positive control detects warmed pool rentals as well as newly allocated storage.
            var owner = ErrorObservation.StartFailure();
            owner.Complete();
            await Assert.That(rentals).IsEqualTo(1);
        }
        finally { ErrorObservation.RentalObserverForTests = null; }
    }

    private static MetricConfigurationScope Configure(bool commandMetrics) => new(new()
    {
        Groups = RespireMetricGroups.Resiliency | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None),
    });

    private static async Task InspectFailure(RespirePending<bool> pending, Exception expected)
    {
        var actual = await Assert.That(() => pending.Result).Throws<Exception>();
        await Assert.That(ReferenceEquals(actual, expected)).IsTrue();
    }

    private sealed record Item(bool Internal, int Attempts, string? Type);

    private sealed class Capture : IDisposable
    {
        private readonly MeterListener _listener = new();
        internal ConcurrentQueue<Item> Items { get; } = new();
        internal Capture(bool commandMetrics)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, RespireTelemetry.Meter) && (instrument.Name == "redis.client.errors"
                    || commandMetrics && instrument.Name == "db.client.operation.duration"))
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                var values = tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value);
                Items.Enqueue(new((bool)values["redis.client.errors.internal"]!,
                    (int)values["redis.client.operation.retry_attempts"]!, (string?)values["error.type"]));
                throw new InvalidOperationException("Listener failures must not replace caller errors.");
            });
            _listener.SetMeasurementEventCallback<double>((_, _, _, _) => { });
            _listener.Start();
        }
        public void Dispose() => _listener.Dispose();
    }
}
