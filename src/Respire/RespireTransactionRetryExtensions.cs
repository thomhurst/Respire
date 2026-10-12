using Respire.Internal;
using Respire.Networking;

namespace Respire;

/// <summary>Optimistic transaction helpers that repeat WATCH, read, queue, and EXEC after eligible failures.</summary>
public static class RespireTransactionRetryExtensions
{
    /// <summary>Runs a watched transaction with a primary-routed, uncached client view for callback reads.</summary>
    /// <remarks>
    /// Each attempt supplies a view preserving the client's prefix, database, and serialization settings.
    /// The view shares the caller's connections and is not owned by this helper. Use it for reads and
    /// queue writes on the supplied transaction. Do not commit or dispose the transaction yourself.
    /// Custom clients must support WithReadFrom and WithoutClientCache. Captured external clients
    /// retain their own routing and caching settings. Retry and cancellation semantics match RunTransactionAsync.
    /// </remarks>
    public static ValueTask RunTransactionWithReadsAsync(
        this IRespireClient client, RespireKey[] watchKeys,
        Func<IRespireClient, RespireWatchedTransaction, CancellationToken, ValueTask> action,
        RespireTransactionRetryOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return RunTransactionAsync(client, watchKeys, (transaction, token) =>
            action(CreateReadView(client), transaction, token),
            options, cancellationToken);
    }

    /// <summary>Runs a watched transaction with safe callback reads and returns the successful attempt's result.</summary>
    /// <remarks>See the non-generic overload for view ownership and callback contracts.</remarks>
    public static ValueTask<T> RunTransactionWithReadsAsync<T>(
        this IRespireClient client, RespireKey[] watchKeys,
        Func<IRespireClient, RespireWatchedTransaction, CancellationToken, ValueTask<T>> action,
        RespireTransactionRetryOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return RunTransactionAsync(client, watchKeys, (transaction, token) =>
            action(CreateReadView(client), transaction, token),
            options, cancellationToken);
    }

    // Called inside each attempt's callback deliberately: never share a view across retries.
    private static IRespireClient CreateReadView(IRespireClient client)
        => client.WithReadFrom(RespireReadFrom.Primary).WithoutClientCache();

    /// <summary>Runs a new watched transaction on each attempt until EXEC succeeds or the attempt limit is reached.</summary>
    /// <remarks>
    /// Read inputs inside the callback using a primary-routed, uncached client view, then queue writes on the transaction.
    /// Prefer RunTransactionWithReadsAsync when the helper should supply that read view for every attempt.
    /// The callback can run more than once; avoid external side effects and do not commit or dispose it yourself.
    /// False EXEC results use MaxAttempts. Transient failures use the client's RespireOptions.RetryPolicy and a
    /// separate budget. Every queued command must permit replay for the commit's transport write outcome.
    /// A lost EXEC reply remains ambiguous; replay requires explicit permission for all possible repeated effects.
    /// Timeouts, cancellation, unselected callback errors and Cluster routing rejections propagate without replay.
    /// WATCH and queued keys retain the existing same-slot Cluster requirement.
    /// A canceled accepted commit may still execute. Failed attempts are disposed before backoff and before a new WATCH.
    /// </remarks>
    public static ValueTask RunTransactionAsync(
        this IRespireClient client, RespireKey[] watchKeys,
        Func<RespireWatchedTransaction, CancellationToken, ValueTask> action,
        RespireTransactionRetryOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        var operation = RunTransactionAsync(client, watchKeys, async (transaction, token) =>
        {
            await action(transaction, token).ConfigureAwait(false);
            return true;
        }, options, cancellationToken);
        return AwaitCompletionAsync(operation);

        static async ValueTask AwaitCompletionAsync(ValueTask<bool> operation)
            => _ = await operation.ConfigureAwait(false);
    }

    /// <summary>Runs a watched transaction and returns only the successful attempt's callback result.</summary>
    /// <remarks>See the non-generic overload for retry and callback contracts. Queued pending values complete only after EXEC.
    /// Prefer RunTransactionWithReadsAsync when the helper should supply a primary-routed, uncached read view.</remarks>
    public static ValueTask<T> RunTransactionAsync<T>(
        this IRespireClient client, RespireKey[] watchKeys,
        Func<RespireWatchedTransaction, CancellationToken, ValueTask<T>> action,
        RespireTransactionRetryOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(watchKeys);
        ArgumentNullException.ThrowIfNull(action);
        options ??= new RespireTransactionRetryOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxAttempts);
        cancellationToken.ThrowIfCancellationRequested();
        var keys = new RespireKey[watchKeys.Length];
        for (var index = 0; index < keys.Length; index++) keys[index] = watchKeys[index].Snapshot();
        return RunCoreAsync(client, keys, action, options, cancellationToken);
    }

    private static async ValueTask<T> RunCoreAsync<T>(
        IRespireClient client, RespireKey[] watchKeys,
        Func<RespireWatchedTransaction, CancellationToken, ValueTask<T>> action,
        RespireTransactionRetryOptions options, CancellationToken cancellationToken)
    {
        var core = (client as RespireClient)?.Core;
        var policy = core?.CommandRetries;
        var commandTimeout = core?.Options.CommandTimeout;
        var deadline = LogicalCommandRetry.CreateDeadline(commandTimeout);
        using var timeout = deadline.IsSet
            ? CommandTimeoutCancellation.Create(cancellationToken, TimeSpan.FromMilliseconds(Math.Max(1, deadline.RemainingMilliseconds)))
            : null;
        var token = timeout?.Token ?? cancellationToken;
        var conflicts = 0;
        var transientAttempts = 1;
        var retryAfterConflict = false;
        var transientRetryStarted = false;
        var retryCategory = RespireCommandRetryCategory.ReadOnly;
        var succeeded = false;
        try
        {
            for (;;)
            {
                ThrowIfStopped();
                bool committed;
                T result;
                var context = new WatchedTransactionRetryContext(deadline);
                try
                {
                    var transaction = await client.CreateTransactionAsync(watchKeys, token).ConfigureAwait(false);
                    await using (transaction.ConfigureAwait(false))
                    {
                        // Only built-in clients expose a logical timeout. Custom clients retain their own deadlines.
                        if (core is not null) transaction.HelperRetryContext = context;
                        ThrowIfStopped();
                        if (retryAfterConflict)
                        {
                            RespireTelemetry.RecordTransactionRetry();
                            retryAfterConflict = false;
                        }
                        result = await action(transaction, token).ConfigureAwait(false);
                        ThrowIfStopped();
                        committed = await transaction.CommitWithWatchValidationAsync(token).ConfigureAwait(false);
                    }
                }
                catch (Exception error)
                {
                    ThrowIfStopped();
                    var remaining = deadline.IsSet ? TimeSpan.FromMilliseconds(deadline.RemainingMilliseconds) : TimeSpan.MaxValue;
                    if (policy is null || !context.TryGetDelay(policy, error, transientAttempts, remaining, token, out var delay))
                    {
                        ThrowIfStopped();
                        throw;
                    }
                    // WATCH acquisition and transaction disposal have both finished before this backoff.
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, token).ConfigureAwait(false);
                    ThrowIfStopped();
                    transientAttempts++;
                    transientRetryStarted = true;
                    retryCategory = context.TelemetryCategory;
                    RespireTelemetry.RecordCommandRetry(retryCategory, "attempted");
                    continue;
                }

                if (committed)
                {
                    succeeded = true;
                    return result;
                }
                RespireTelemetry.RecordTransactionConflict();
                token.ThrowIfCancellationRequested();
                if (++conflicts == options.MaxAttempts) throw new RespireTransactionConflictException(conflicts);
                var conflictDelay = options.GetDelay(conflicts);
                if (conflictDelay > TimeSpan.Zero) await Task.Delay(conflictDelay, token).ConfigureAwait(false);
                retryAfterConflict = true;
            }
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested && error.CancellationToken == token)
        {
            throw new OperationCanceledException(error.Message, error, cancellationToken);
        }
        catch (OperationCanceledException error) when (timeout is not null && timeout.IsCancellationRequested && error.CancellationToken == token)
        {
            throw new RespireTimeoutException("WATCH/MULTI/EXEC", commandTimeout!.Value, error);
        }
        finally
        {
            if (transientRetryStarted) RespireTelemetry.RecordCommandRetry(retryCategory, succeeded ? "succeeded" : "exhausted");
        }

        void ThrowIfStopped()
        {
            token.ThrowIfCancellationRequested();
            if (deadline.IsSet && deadline.RemainingMilliseconds == 0)
                throw new RespireTimeoutException("WATCH/MULTI/EXEC", commandTimeout!.Value);
        }
    }
}

// One helper attempt owns its category set and immutable transport evidence through disposal.
// Direct CommitAsync never attaches this context and never replays a watched connection.
internal sealed class WatchedTransactionRetryContext(CommandDeadline deadline)
{
    private readonly HashSet<RespireCommandRetryCategory> _categories = [];
    private RespireCommandRetryCategory _telemetryCategory = RespireCommandRetryCategory.Always;
    internal CommandDeadline Deadline { get; } = deadline;
    internal RespireConnection.CommandWriteObservation WriteObservation { get; } = new();
    internal RespireCommandRetryCategory TelemetryCategory => _categories.Count == 0 ? RespireCommandRetryCategory.ReadOnly : _telemetryCategory;

    internal void RecordCategory(RespireCommandRetryCategory category)
    {
        if (!Enum.IsDefined(category)) category = RespireCommandRetryCategory.Never;
        _categories.Add(category);
        // Numeric ordering is used only to label telemetry, never to decide replay permission.
        if ((byte)category > (byte)_telemetryCategory) _telemetryCategory = category;
    }

    internal bool TryGetDelay(CommandRetryPolicy policy, Exception error, int attempts, TimeSpan remaining,
        CancellationToken token, out TimeSpan delay)
    {
        var outcome = WriteObservation.CaptureCompletedOutcome();
        delay = default;
        var delayCategory = RespireCommandRetryCategory.ReadOnly;
        foreach (var category in _categories)
        {
            if (!policy.CanRetry(error, outcome, category, attempts, remaining, token)) return false;
            delayCategory = category;
        }
        return policy.TryGetDelay(error, outcome, delayCategory, attempts, remaining, token, out delay);
    }
}
