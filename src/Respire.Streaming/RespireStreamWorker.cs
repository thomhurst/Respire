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
    private readonly RespireKey[]? _groupReadKeys = options.JobQueue is null ? null
        : [.. JobGroupScheduling.Keys(stream), JobQueueControls.Key(stream)];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.JobEventsStream is { } events)
        {
            var eventKey = client.ResolveKey(events).ToBytes();
            foreach (var key in new[] { stream, options.DeadLetterStream! })
            {
                var resolved = client.ResolveKey(key).ToBytes();
                if (eventKey.AsSpan().SequenceEqual(resolved) || ClusterHash.GetSlot(eventKey) != ClusterHash.GetSlot(resolved))
                    throw new ArgumentException("Job event, source, and dead-letter streams must be distinct keys in one Cluster slot.");
            }
        }
        if (options.JobQueue is not null)
            JobQueueScheduling.ValidateKeys(client, JobGroupScheduling.Keys(stream));
        if (options.JobEventsStream is not null)
        {
            JobQueueScheduling.ValidateKeys(client, [stream, JobQueueControls.Key(stream), options.JobEventsStream, options.DeadLetterStream!]);
            if (options.JobQueue?.MetricsRetentionMinutes > 0)
                JobQueueScheduling.ValidateKeys(client, [stream, options.JobEventsStream, options.DeadLetterStream!, stream + ":metrics"]);
            if (options.JobQueue?.RateLimit is { } rate)
                await client.Scripts.ExecuteIntegerAsync(JobQueueControls.Configure, [JobQueueControls.Key(stream)],
                    [rate.PermitLimit, (long)Math.Ceiling(rate.Window.TotalMilliseconds), "initialize"], stoppingToken).ConfigureAwait(false);
        }
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
        var readOptions = new RespireStreamReadOptions { Count = options.BatchSize, WaitFor = options.ReadWait };
        RespireStreamId? cursor = options.ConsumerName is null ? (RespireStreamId?)null : RespireStreamId.Beginning;
        var recoveryCursor = RespireStreamId.Beginning;
        var recoveryPagesRemaining = 0;
        var recoveryClock = Stopwatch.StartNew();
        var schedulingClock = new Stopwatch();
        try
        {
            while (!readers.IsCancellationRequested)
            {
                if (options.JobQueue is { } queue
                    && (!schedulingClock.IsRunning || schedulingClock.Elapsed >= queue.SchedulingPollInterval))
                {
                    await client.Scripts.ExecuteIntegerAsync(JobQueueScheduling.Promote, [.. JobQueueScheduling.Keys(stream)],
                        [queue.SchedulingBatchSize], readers.Token).ConfigureAwait(false);
                    schedulingClock.Restart();
                }
                Delivery[] deliveries;
                var recoveredBatch = false;
                if (options.JobEventsStream is not null)
                {
                    var replay = cursor.HasValue;
                    var recover = !replay && recoveryClock.Elapsed >= options.RecoveryPollInterval;
                    var groupedRead = !replay && !recover && options.JobQueue is not null;
                    var script = replay ? (options.JobQueue is null ? JobQueueControls.Replay : JobQueueControls.ReplayGrouped)
                        : recover ? (options.JobQueue is null ? JobQueueControls.Claim : JobQueueControls.ClaimGrouped)
                        : groupedRead ? JobQueueControls.ReadGrouped : JobQueueControls.Read;
                    RespireValue[] arguments = replay ? [group, consumer, 1, cursor!.Value.Value]
                        : recover ? [group, consumer, (long)Math.Ceiling(options.MinimumIdleTime.TotalMilliseconds), recoveryCursor.Value, 1]
                        : groupedRead ? [group, consumer, 1, options.JobQueue!.SchedulingBatchSize] : [group, consumer];
                    var page = await ReadPageAsync(script, arguments, readers.Token, controls: true, grouped: options.JobQueue is not null).ConfigureAwait(false);
                    _telemetry.SetPaused(page.Paused);
                    var poll = options.JobQueue?.SchedulingPollInterval ?? TimeSpan.FromSeconds(1);
                    if (page.Paused || page.RetryAfter > 0)
                    {
                        if (page.RetryAfter > 0)
                        {
                            _telemetry.RateLimited();
                            poll = TimeSpan.FromMilliseconds(Math.Min(poll.TotalMilliseconds, page.RetryAfter));
                        }
                        await Task.Delay(poll, readers.Token).ConfigureAwait(false);
                        continue;
                    }
                    if (replay) cursor = page.Cursor.CompareTo(RespireStreamId.Beginning) == 0 ? (RespireStreamId?)null : page.Cursor;
                    if (recover)
                    {
                        // Preserve a bounded batch's scan budget while checking admission per entry.
                        // Denied admissions above neither advance the cursor nor spend this budget.
                        if (recoveryPagesRemaining == 0) recoveryPagesRemaining = options.BatchSize;
                        recoveryCursor = page.Cursor;
                        recoveredBatch = --recoveryPagesRemaining == 0 || recoveryCursor == RespireStreamId.Beginning;
                        if (recoveredBatch) recoveryPagesRemaining = 0;
                    }
                    deliveries = page.Deliveries;
                    if (deliveries.Length == 0 && !replay && !recover)
                    {
                        var wait = poll < options.ReadWait ? poll : options.ReadWait;
                        var recoveryRemaining = options.RecoveryPollInterval - recoveryClock.Elapsed;
                        if (wait > recoveryRemaining) wait = recoveryRemaining;
                        if (options.JobQueue is not null)
                        {
                            var schedulingRemaining = poll - schedulingClock.Elapsed;
                            if (wait > schedulingRemaining) wait = schedulingRemaining;
                        }
                        if (wait > TimeSpan.Zero) await Task.Delay(wait, readers.Token).ConfigureAwait(false);
                    }
                }
                else if (cursor.HasValue)
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
                    if (options.JobQueue is { } scheduling)
                    {
                        var schedulingRemaining = scheduling.SchedulingPollInterval - schedulingClock.Elapsed;
                        if (wait > schedulingRemaining) wait = schedulingRemaining;
                    }
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

    private async Task<(RespireStreamId Cursor, Delivery[] Deliveries, bool Paused, long RetryAfter)> ReadPageAsync(
        RespireScript script, RespireValue[] arguments, CancellationToken token, bool controls = false, bool grouped = false)
    {
        using var reply = await client.Scripts.ExecuteLeaseAsync(script,
            grouped ? _groupReadKeys
                : controls ? [stream, JobQueueControls.Key(stream)] : [stream], arguments, token).ConfigureAwait(false);
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
        return (new(reply[0].AsString()), deliveries, controls && reply[2].AsInteger() == 1,
            controls ? reply[3].AsInteger() : 0);
    }

    private async Task ProcessAsync(Delivery delivery, string consumer)
    {
        using var observation = _telemetry.Begin(delivery.Entry, options);
        try
        {
            if (HasGroupLease(delivery)) await ProcessLeasedAsync(delivery, consumer, observation).ConfigureAwait(false);
            else await ProcessAsync(delivery, consumer, observation, _handlers.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_handlers.IsCancellationRequested && !observation.CleanupFailed)
        {
            observation.Outcome = "canceled";
            throw;
        }
    }

    private async Task ProcessLeasedAsync(Delivery delivery, string consumer, StreamWorkerTelemetry.Attempt observation)
    {
        using var processing = CancellationTokenSource.CreateLinkedTokenSource(_handlers.Token);
        using var renewals = new CancellationTokenSource();
        Task? renewal = null;
        try
        {
            var duration = await RenewGroupLeaseAsync(delivery, consumer, processing.Token).ConfigureAwait(false);
            if (duration == 0) { observation.Outcome = "stale"; return; }
            renewal = RenewGroupLeaseLoopAsync(delivery, consumer, duration, processing, renewals.Token);
            await ProcessAsync(delivery, consumer, observation, processing.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (processing.IsCancellationRequested && !observation.CleanupFailed)
        {
            observation.Outcome = "canceled";
        }
        finally
        {
            await renewals.CancelAsync().ConfigureAwait(false);
            try { if (renewal is not null) await renewal.ConfigureAwait(false); }
            finally
            {
                await client.Scripts.ExecuteIntegerAsync(JobGroupControls.Release, [.. JobGroupScheduling.Keys(stream)],
                    [consumer, delivery.Entry.Id.Value, delivery.Attempt], CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private bool HasGroupLease(Delivery delivery) => options.JobQueue is not null && delivery.Entry.GetString("_respire.job.group") is not null;

    private ValueTask<long> RenewGroupLeaseAsync(Delivery delivery, string consumer, CancellationToken token)
        => client.Scripts.ExecuteIntegerAsync(JobGroupControls.Renew, [.. JobGroupScheduling.Keys(stream)],
            [consumer, delivery.Entry.Id.Value, delivery.Attempt, group], token);

    private async Task RenewGroupLeaseLoopAsync(Delivery delivery, string consumer, long duration,
        CancellationTokenSource processing, CancellationToken token)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, duration / 3)), token).ConfigureAwait(false);
                duration = await RenewGroupLeaseAsync(delivery, consumer, token).ConfigureAwait(false);
                if (duration == 0) { await processing.CancelAsync().ConfigureAwait(false); return; }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch { await processing.CancelAsync().ConfigureAwait(false); throw; }
    }

    private RespireKey[] LeaseFenceKeys(Delivery delivery, RespireKey[] keys)
        => HasGroupLease(delivery) ? [.. keys, stream + ":group-leases", stream + ":group-owners"] : keys;

    private async Task ProcessAsync(Delivery delivery, string consumer, StreamWorkerTelemetry.Attempt observation, CancellationToken processingToken)
    {
        if (options.DeadLetterStream is not null && delivery.Entry.Fields.Count > StreamWorkerScripts.MaximumDeadLetterFields)
            throw new InvalidOperationException("Dead-letter-enabled workers support at most 1024 field/value pairs per entry.");
        var totalAttempts = delivery.Attempt;
        if (options.JobEventsStream is { } batchEvents && delivery.Entry.GetString("_respire.job.batch") is { } batchKey)
        {
            JobQueueScheduling.ValidateKeys(client, [stream, batchKey]);
            var cancelled = await client.Scripts.ExecuteIntegerAsync(JobQueueBatches.CheckCancellation, [stream, batchKey],
                [group, consumer, delivery.Entry.Id.Value, delivery.Attempt],
                processingToken).ConfigureAwait(false);
            if (cancelled == -1) { observation.Outcome = "stale"; return; }
            if (cancelled == 1)
            {
                var acknowledged = await client.Scripts.ExecuteIntegerAsync(CompletionScript(delivery, options.DeleteAcknowledgedEntries),
                    LeaseFenceKeys(delivery, CompletionKeys(delivery, batchEvents, false)),
                    [group, consumer, delivery.Entry.Id.Value, delivery.Attempt, "cancelled", "", "",
                        0, Array.Empty<byte>(), options.JobQueue?.MetricsRetentionMinutes ?? 0],
                    processingToken).ConfigureAwait(false);
                observation.Outcome = acknowledged == 2 ? "canceled" : "stale";
                return;
            }
        }
        if (options.JobQueue is not null)
        {
            var previous = delivery.Entry.GetString("_respire.job.attempts");
            if (previous is not null) totalAttempts = checked(totalAttempts + long.Parse(previous, System.Globalization.CultureInfo.InvariantCulture));
            var expires = delivery.Entry.GetString("_respire.job.expires");
            if (expires is not null)
            {
                var live = await client.Scripts.ExecuteIntegerAsync(HasGroupLease(delivery) ? JobGroupControls.CheckExpiry : JobQueueScheduling.CheckExpiry, LeaseFenceKeys(delivery, [stream]),
                    [group, consumer, delivery.Entry.Id.Value, delivery.Attempt, expires,
                        options.DeleteAcknowledgedEntries ? 1 : 0], processingToken).ConfigureAwait(false);
                if (live != 1) { observation.Outcome = live == -1 ? "expired" : "stale"; return; }
            }
        }
        if (options.DeliveryLimit is { } limit && totalAttempts > limit)
        {
            observation.Outcome = await DeadLetterAsync(delivery, consumer, "delivery-limit", "", processingToken).ConfigureAwait(false);
            return;
        }
        var scope = scopeFactory.CreateAsyncScope();
        try
        {
            var handler = scope.ServiceProvider.GetRequiredService<THandler>();
            RespireStreamWorkerResult result;
            var reason = "nack";
            var failureType = "";
            TimeSpan? retryDelay = null;
            byte[]? jobResult = null;
            try
            {
                var message = deserialize(delivery.Entry);
                if (options.JobEventsStream is { } events && message is IJobDelivery job)
                    job.Attach(new(client, stream, events, group, consumer, delivery.Entry.Id, delivery.Attempt,
                        delivery.Entry.GetString("_respire.job.flow"),
                        int.Parse(delivery.Entry.GetString("_respire.job.node") ?? "0", System.Globalization.CultureInfo.InvariantCulture),
                        job.Id, options.JobQueue, HasGroupLease(delivery)));
                result = await handler.HandleAsync(message, processingToken).ConfigureAwait(false);
                if (result is not (RespireStreamWorkerResult.Ack or RespireStreamWorkerResult.Nack or RespireStreamWorkerResult.DeadLetter))
                    throw new InvalidOperationException("The stream handler returned an unknown completion result.");
                if (result == RespireStreamWorkerResult.DeadLetter && options.DeadLetterStream is null)
                    throw new InvalidOperationException("DeadLetter completion requires a configured dead-letter stream.");
                retryDelay = (message as IJobRetry)?.RetryDelay;
                jobResult = (message as IJobDelivery)?.Result;
            }
            catch (OperationCanceledException) when (processingToken.IsCancellationRequested)
            { observation.Outcome = "canceled"; return; }
            catch (Exception error)
            {
                observation.ProcessingFailed = true;
                // Exception messages may contain payloads. Do not pass them to the logger by default.
                var exceptionType = error.GetType();
                failureType = exceptionType.FullName ?? exceptionType.Name;
                HandlerFailed(logger, failureType);
                reason = "processing-failed";
                result = RespireStreamWorkerResult.Nack;
            }

            if (processingToken.IsCancellationRequested) { observation.Outcome = "canceled"; return; }
            if (result == RespireStreamWorkerResult.Ack)
            {
                // ConsumeAsync treats acknowledgement cancellation as expected only after
                // readers stop. StopAsync and Dispose must therefore cancel readers first.
                var acknowledged = options.JobEventsStream is { } events
                    ? await client.Scripts.ExecuteIntegerAsync(CompletionScript(delivery, options.DeleteAcknowledgedEntries),
                        LeaseFenceKeys(delivery, CompletionKeys(delivery, events, false)),
                        [group, consumer, delivery.Entry.Id.Value, delivery.Attempt, "completed", "", "",
                            jobResult is null ? 0 : 1, jobResult ?? [], options.JobQueue?.MetricsRetentionMinutes ?? 0], processingToken).ConfigureAwait(false)
                    : await client.Scripts.ExecuteIntegerAsync(options.DeleteAcknowledgedEntries
                        ? StreamWorkerScripts.AckAndDelete : StreamWorkerScripts.Ack, [stream],
                    [group, consumer, delivery.Entry.Id.Value, delivery.Attempt], processingToken).ConfigureAwait(false);
                observation.Outcome = acknowledged switch { 1 => "ack", 2 => "canceled", _ => "stale" };
            }
            else if (result == RespireStreamWorkerResult.DeadLetter)
                observation.Outcome = await DeadLetterAsync(delivery, consumer, "explicit", "", processingToken).ConfigureAwait(false);
            else if (options.DeliveryLimit is { } deliveryLimit && totalAttempts >= deliveryLimit)
                observation.Outcome = await DeadLetterAsync(delivery, consumer, reason, failureType, processingToken).ConfigureAwait(false);
            else if (options.JobQueue is { } queue
                && (retryDelay ?? queue.GetRetryDelay(totalAttempts)) is { } delay)
            {
                var retried = await client.Scripts.ExecuteIntegerAsync(HasGroupLease(delivery) ? JobGroupControls.Retry : JobQueueScheduling.Retry, LeaseFenceKeys(delivery, [.. JobGroupScheduling.Keys(stream)]),
                    [group, consumer, delivery.Entry.Id.Value, delivery.Attempt, (long)Math.Ceiling(delay.TotalMilliseconds),
                        options.DeleteAcknowledgedEntries ? 1 : 0],
                    processingToken).ConfigureAwait(false);
                observation.Outcome = retried == 1 ? "nack" : "stale";
            }
            else
            {
                await client.Scripts.ExecuteIntegerAsync(HasGroupLease(delivery) ? JobGroupControls.Nack : StreamWorkerScripts.Nack, LeaseFenceKeys(delivery, [stream]),
                    [group, consumer, delivery.Entry.Id.Value, delivery.Attempt,
                        (long)Math.Ceiling(options.MinimumIdleTime.TotalMilliseconds)], processingToken).ConfigureAwait(false);
                observation.Outcome = "nack";
            }
        }
        finally
        {
            try { await scope.DisposeAsync().ConfigureAwait(false); }
            catch { observation.CleanupFailed = true; throw; }
        }
    }

    private async Task<string> DeadLetterAsync(Delivery delivery, string consumer, string reason, string failureType, CancellationToken processingToken)
    {
        if (processingToken.IsCancellationRequested) return "canceled";
        var completed = options.JobEventsStream is { } events
            ? await client.Scripts.ExecuteIntegerAsync(CompletionScript(delivery, false), LeaseFenceKeys(delivery, CompletionKeys(delivery, events, true)),
                [group, consumer, delivery.Entry.Id.Value, delivery.Attempt, "dead-letter", reason, failureType,
                    0, Array.Empty<byte>(), options.JobQueue?.MetricsRetentionMinutes ?? 0],
                processingToken).ConfigureAwait(false)
            : await client.Scripts.ExecuteIntegerAsync(StreamWorkerScripts.DeadLetter, [stream, options.DeadLetterStream!],
            [group, consumer, delivery.Entry.Id.Value, delivery.Attempt, reason, failureType],
            processingToken).ConfigureAwait(false);
        if (completed == 1) _telemetry.DeadLetter(reason);
        return completed switch { 1 => "dead-letter", 2 => "canceled", -1 => "deleted", _ => "stale" };
    }

    private RespireKey[] CompletionKeys(Delivery delivery, string events, bool deadLetter)
    {
        RespireKey[] keys;
        var state = delivery.Entry.GetString("_respire.job.batch") ?? delivery.Entry.GetString("_respire.job.flow");
        if (state is null)
            keys = deadLetter ? [stream, events, options.DeadLetterStream!] : [stream, events];
        else
        {
            JobQueueScheduling.ValidateKeys(client, options.JobQueue?.MetricsRetentionMinutes > 0
                ? [stream, events, options.DeadLetterStream!, state, stream + ":metrics"]
                : [stream, events, options.DeadLetterStream!, state]);
            keys = [stream, events, options.DeadLetterStream!, state];
        }
        return options.JobQueue?.MetricsRetentionMinutes > 0 ? [.. keys, stream + ":metrics"] : keys;
    }

    private RespireScript CompletionScript(Delivery delivery, bool delete)
    {
        if (HasGroupLease(delivery))
            return options.JobQueue?.MetricsRetentionMinutes > 0
                ? delete ? JobGroupControls.CompleteAndDeleteWithMetrics : JobGroupControls.CompleteWithMetrics
                : delete ? JobGroupControls.CompleteAndDelete : JobGroupControls.Complete;
        return options.JobQueue?.MetricsRetentionMinutes > 0
            ? delete ? JobQueueScripts.CompleteAndDeleteWithMetrics : JobQueueScripts.CompleteWithMetrics
            : delete ? JobQueueScripts.CompleteAndDelete : JobQueueScripts.Complete;
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
        _telemetry.StopPublishing();
        try
        {
            try { base.Dispose(); }
            finally { CancelHandlers(); }
        }
        finally
        {
            try
            {
                var running = Volatile.Read(ref _consumerDrain) ?? ExecuteTask;
                if (running is not { IsCompleted: false })
                    _handlers.Dispose();
                else
                    _ = running.ContinueWith(static (_, state) => ((CancellationTokenSource)state!).Dispose(), _handlers,
                        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            // External teardown callbacks may block, so cancel and arrange handler cleanup first.
            finally { _telemetry.Dispose(); }
        }
    }

    private void CancelHandlers()
    {
        try { _handlers.Cancel(); }
        catch (ObjectDisposedException) { } // Stop/Dispose can be called after the execution task has completed.
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Stream handler or serializer failed ({ExceptionType}).")]
    private static partial void HandlerFailed(ILogger logger, string exceptionType);
}
