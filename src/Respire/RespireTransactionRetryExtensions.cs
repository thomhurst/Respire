using Respire.Internal;

namespace Respire;

/// <summary>Optimistic transaction helpers that repeat WATCH, read, queue, and EXEC after a WATCH conflict.</summary>
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
            action(client.WithReadFrom(RespireReadFrom.Primary).WithoutClientCache(), transaction, token),
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
            action(client.WithReadFrom(RespireReadFrom.Primary).WithoutClientCache(), transaction, token),
            options, cancellationToken);
    }

    /// <summary>Runs a new watched transaction on each attempt until EXEC succeeds or the attempt limit is reached.</summary>
    /// <remarks>
    /// Read inputs inside the callback using a primary-routed, uncached client view, then queue writes on the transaction.
    /// The callback can run more than once; avoid external side effects and do not commit or dispose it yourself.
    /// Only a false EXEC result is retried. Callback, connection, timeout, cancellation, server, and Cluster routing
    /// exceptions propagate without replay. WATCH and queued keys retain the existing same-slot Cluster requirement.
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
    /// <remarks>See the non-generic overload for retry and callback contracts. Queued pending values complete only after EXEC.</remarks>
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
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool committed;
            T result;
            var transaction = await client.CreateTransactionAsync(watchKeys, cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (attempt > 1) RespireTelemetry.RecordTransactionRetry();
                result = await action(transaction, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                committed = await transaction.CommitWithWatchValidationAsync(cancellationToken).ConfigureAwait(false);
            }

            if (committed) return result;
            RespireTelemetry.RecordTransactionConflict();
            cancellationToken.ThrowIfCancellationRequested();
            if (attempt == options.MaxAttempts) throw new RespireTransactionConflictException(attempt);
            var delay = options.GetDelay(attempt);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }
}
