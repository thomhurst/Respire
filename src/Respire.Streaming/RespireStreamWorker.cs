using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Respire.Internal;

namespace Respire.Streaming;

internal sealed partial class RespireStreamWorker<THandler, TMessage>(
    IRespireClient client, IServiceScopeFactory scopeFactory,
    ILogger<RespireStreamWorker<THandler, TMessage>> logger,
    string stream, string group, Func<RespireStreamEntry, TMessage> deserialize,
    RespireStreamWorkerOptions options) : BackgroundService
    where THandler : class, IRespireStreamHandler<TMessage>
{
    private readonly CancellationTokenSource _handlers = new();
    private readonly string _identity = options.ConsumerName ?? Guid.NewGuid().ToString("N");
    private readonly TaskCompletionSource _firstFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _consumerDrain;
    private readonly StreamWorkerTelemetry _telemetry = new(options.TelemetryName);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.DeadLetterStream is { } deadLetter)
        {
            var sourceKey = client.ResolveKey(stream).ToBytes();
            var deadLetterKey = client.ResolveKey(deadLetter).ToBytes();
            if (sourceKey.AsSpan().SequenceEqual(deadLetterKey))
                throw new ArgumentException("Source and dead-letter streams must be distinct resolved keys.");
            if (ClusterHash.GetSlot(sourceKey) != ClusterHash.GetSlot(deadLetterKey))
                throw new ArgumentException("Source and dead-letter streams must share a resolved Cluster slot.");
        }
        var readers = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        try
        {
            if (options.CreateGroup)
                await client.Streams.CreateGroupAsync(stream, group, options.GroupStart,
                    cancellationToken: readers.Token).ConfigureAwait(false);
        }
        catch { readers.Dispose(); throw; }

        var consumers = new Task[options.ConsumerCount + 1];
        for (var i = 0; i < options.ConsumerCount; i++)
            consumers[i] = ConsumeAsync($"{_identity}-{i}", readers);
        consumers[^1] = PollGroupAsync(readers.Token);
        var drain = DrainConsumersAsync(consumers, readers);
        Volatile.Write(ref _consumerDrain, drain);
        // A failed reader must reach the host even if a sibling ignores cancellation.
        // Its handler scope remains owned by that sibling until the separate drain ends.
        await Task.WhenAny(_firstFailure.Task, drain).ConfigureAwait(false);
        // The fault's asynchronous continuation can lose to an already completed drain.
        // Its published state still takes precedence over clean completion.
        if (_firstFailure.Task.IsCompleted) await _firstFailure.Task.ConfigureAwait(false);
    }

    private async Task DrainConsumersAsync(Task[] consumers, CancellationTokenSource readers)
    {
        try { await Task.WhenAll(consumers).ConfigureAwait(false); }
        catch { /* ConsumeAsync has already published the first fault to the host. */ }
        finally { _telemetry.StopPolling(); readers.Dispose(); }
    }

    private async Task PollGroupAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_telemetry.NeedsGroupPoll)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(options.MetricsPollTimeout);
                    try
                    {
                        var groups = await client.Streams.GroupInfoAsync(stream, deadline.Token).ConfigureAwait(false);
                        if (!deadline.IsCancellationRequested)
                        {
                            RespireStreamGroupInfo? match = null;
                            foreach (var info in groups)
                                if (info.Name == group) { match = info; break; }
                            _telemetry.SetGroup(match);
                        }
                        else _telemetry.SetGroup(null);
                    }
                    catch { _telemetry.SetGroup(null); /* Telemetry queries must not fault consumers. */ }
                }
                else _telemetry.SetGroup(null);
                // Delay after completion prevents overlapping queries and failure hot loops.
                await Task.Delay(options.MetricsPollInterval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { _telemetry.StopPolling(); }
    }

    private async Task ConsumeAsync(string consumer, CancellationTokenSource readers)
    {
        var readOptions = new StreamReadOptions { Count = options.BatchSize, WaitFor = options.ReadWait };
        RespireStreamId? cursor = options.ConsumerName is null ? (RespireStreamId?)null : RespireStreamId.Beginning;
        var recoveryCursor = RespireStreamId.Beginning;
        var recoveryClock = Stopwatch.StartNew();
        try
        {
            while (!readers.IsCancellationRequested)
            {
                Delivery[] deliveries;
                var recoveredBatch = false;
                if (cursor.HasValue)
                {
                    var page = await ReadPageAsync(StreamWorkerScripts.Replay,
                        [group, consumer, options.BatchSize, cursor.Value.Value], readers.Token).ConfigureAwait(false);
                    // Even a deleted pending entry advances replay without dispatching a handler.
                    cursor = page.Cursor.CompareTo(RespireStreamId.Beginning) == 0 ? (RespireStreamId?)null : page.Cursor;
                    deliveries = page.Deliveries;
                }
                else if (recoveryClock.Elapsed >= options.RecoveryPollInterval)
                {
                    var page = await ReadPageAsync(StreamWorkerScripts.CapabilityClaim,
                        [group, consumer, (long)Math.Ceiling(options.MinimumIdleTime.TotalMilliseconds),
                            recoveryCursor.Value, options.BatchSize], readers.Token).ConfigureAwait(false);
                    recoveryCursor = page.Cursor; // Keep the cursor even when no entries were claimable.
                    recoveredBatch = true;
                    deliveries = page.Deliveries;
                }
                else
                {
                    var remaining = options.RecoveryPollInterval - recoveryClock.Elapsed;
                    var wait = remaining < options.ReadWait ? remaining : options.ReadWait;
                    if (wait <= TimeSpan.Zero) continue;
                    var entries = await client.Streams.ReadGroupOnceAsync(stream, group, consumer,
                        readOptions with { WaitFor = wait }, cancellationToken: readers.Token).ConfigureAwait(false);
                    // New group deliveries start at attempt 1 at the server read boundary.
                    // Never query XPENDING later: a same-name reclaim may already have occurred.
                    deliveries = entries.Select(static entry => new Delivery(
                        entry.WithoutAcknowledgement(), 1)).ToArray();
                }
                foreach (var delivery in deliveries)
                {
                    // A batch delivered during shutdown stays pending rather than starting more handlers.
                    if (readers.IsCancellationRequested) break;
                    await ProcessAsync(delivery, consumer).ConfigureAwait(false);
                }
                // Slow recovered handlers must not make another scan due before new reads get a turn.
                if (recoveredBatch) recoveryClock.Restart();
            }
        }
        catch (OperationCanceledException) when (readers.IsCancellationRequested) { }
        catch (Exception error)
        {
            _firstFailure.TrySetException(error);
            try { await readers.CancelAsync().ConfigureAwait(false); }
            finally { CancelHandlers(); }
            throw;
        }
    }

    private readonly record struct Delivery(RespireStreamEntry Entry, long Attempt);

    private async Task<(RespireStreamId Cursor, Delivery[] Deliveries)> ReadPageAsync(
        RespireScript script, RespireValue[] arguments, CancellationToken token)
    {
        using var reply = await client.Scripts.ExecuteAsync(script, [stream], arguments, token).ConfigureAwait(false);
        var rows = reply[1];
        var deliveries = new Delivery[rows.Count];
        for (var i = 0; i < deliveries.Length; i++)
        {
            var row = rows[i];
            var values = row[1];
            var fields = new KeyValuePair<string, byte[]>[values.Count / 2];
            for (var field = 0; field < fields.Length; field++)
                fields[field] = new(values[field * 2].AsString(), values[field * 2 + 1].AsBytes());
            // No unconditional entry.AckAsync is exposed to the handler for scripted deliveries.
            deliveries[i] = new(new RespireStreamEntry(new(row[0].AsString()), fields),
                long.Parse(row[2].AsString(), System.Globalization.CultureInfo.InvariantCulture));
        }
        return (new(reply[0].AsString()), deliveries);
    }

    private async Task ProcessAsync(Delivery delivery, string consumer)
    {
        using var observation = _telemetry.Begin(delivery.Entry, options);
        if (options.DeadLetterStream is not null && delivery.Entry.Fields.Count > StreamWorkerScripts.MaximumDeadLetterFields)
            throw new InvalidOperationException("Dead-letter-enabled workers support at most 1024 field/value pairs per entry.");
        if (options.DeliveryLimit is { } limit && delivery.Attempt > limit)
        {
            observation.Outcome = await DeadLetterAsync(delivery, consumer, "delivery-limit", "").ConfigureAwait(false);
            return;
        }
        await using var scope = scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<THandler>();
        RespireStreamWorkerResult result;
        var reason = "nack";
        var failureType = "";
        try
        {
            result = await handler.HandleAsync(deserialize(delivery.Entry), _handlers.Token).ConfigureAwait(false);
            if (result is not (RespireStreamWorkerResult.Ack or RespireStreamWorkerResult.Nack or RespireStreamWorkerResult.DeadLetter))
                throw new InvalidOperationException("The stream handler returned an unknown completion result.");
            if (result == RespireStreamWorkerResult.DeadLetter && options.DeadLetterStream is null)
                throw new InvalidOperationException("DeadLetter completion requires a configured dead-letter stream.");
        }
        catch (OperationCanceledException) when (_handlers.IsCancellationRequested)
        { observation.Outcome = "canceled"; return; }
        catch (Exception error)
        {
            // Exception messages may contain payloads. Do not pass them to the logger by default.
            var exceptionType = error.GetType();
            failureType = exceptionType.FullName ?? exceptionType.Name;
            HandlerFailed(logger, failureType);
            reason = "processing-failed";
            result = RespireStreamWorkerResult.Nack;
        }

        if (_handlers.IsCancellationRequested) { observation.Outcome = "canceled"; return; }
        if (result == RespireStreamWorkerResult.Ack)
        {
            // ConsumeAsync treats acknowledgement cancellation as expected only after
            // readers stop. StopAsync and Dispose must therefore cancel readers first.
            var acknowledged = await client.Scripts.ExecuteIntegerAsync(options.DeleteAcknowledgedEntries
                    ? StreamWorkerScripts.AckAndDelete : StreamWorkerScripts.Ack, [stream],
                [group, consumer, delivery.Entry.Id.Value, delivery.Attempt], _handlers.Token).ConfigureAwait(false);
            observation.Outcome = acknowledged == 1 ? "ack" : "stale";
        }
        else if (result == RespireStreamWorkerResult.DeadLetter)
            observation.Outcome = await DeadLetterAsync(delivery, consumer, "explicit", "").ConfigureAwait(false);
        else if (options.DeliveryLimit is { } deliveryLimit && delivery.Attempt >= deliveryLimit)
            observation.Outcome = await DeadLetterAsync(delivery, consumer, reason, failureType).ConfigureAwait(false);
        else
        {
            await client.Scripts.ExecuteIntegerAsync(StreamWorkerScripts.Nack, [stream],
                [group, consumer, delivery.Entry.Id.Value, delivery.Attempt,
                    (long)Math.Ceiling(options.MinimumIdleTime.TotalMilliseconds)], _handlers.Token).ConfigureAwait(false);
            observation.Outcome = "nack";
        }
    }

    private async Task<string> DeadLetterAsync(Delivery delivery, string consumer, string reason, string failureType)
    {
        if (_handlers.IsCancellationRequested) return "canceled";
        var completed = await client.Scripts.ExecuteIntegerAsync(StreamWorkerScripts.DeadLetter, [stream, options.DeadLetterStream!],
            [group, consumer, delivery.Entry.Id.Value, delivery.Attempt, reason, failureType],
            _handlers.Token).ConfigureAwait(false);
        if (completed == 1) _telemetry.DeadLetter(reason);
        return completed switch { 1 => "dead-letter", -1 => "deleted", _ => "stale" };
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _telemetry.StopPolling();
        // BackgroundService cancels readers immediately. Handlers keep a separate token while draining.
        var stopping = base.StopAsync(cancellationToken);
        using var abort = cancellationToken.UnsafeRegister(static state =>
            ((RespireStreamWorker<THandler, TMessage>)state!).CancelHandlers(), this);
        try { await stopping.ConfigureAwait(false); }
        finally
        {
            // WaitAsync's cancellation callback can finish the wait and remove our registration first.
            if (cancellationToken.IsCancellationRequested) CancelHandlers();
        }
    }

    public override void Dispose()
    {
        _telemetry.Dispose();
        base.Dispose();
        CancelHandlers();
        var running = Volatile.Read(ref _consumerDrain) ?? ExecuteTask;
        if (running is not { IsCompleted: false })
            _handlers.Dispose();
        else
            _ = running.ContinueWith(static (_, state) => ((CancellationTokenSource)state!).Dispose(), _handlers,
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void CancelHandlers()
    {
        try { _handlers.Cancel(); }
        catch (ObjectDisposedException) { } // Stop/Dispose can be called after the execution task has completed.
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stream handler or serializer failed ({ExceptionType}).")]
    private static partial void HandlerFailed(ILogger logger, string exceptionType);
}
