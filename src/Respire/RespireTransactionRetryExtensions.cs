using Respire.Internal;

namespace Respire;

/// <summary>Optimistic transaction helpers that repeat WATCH, read, queue, and EXEC after a WATCH conflict.</summary>
public static class RespireTransactionRetryExtensions
{
    /// <summary>Runs a new watched transaction on each attempt until EXEC succeeds or the attempt limit is reached.</summary>
    /// <remarks>
    /// Read inputs inside the callback using a primary-routed, uncached client view, then queue writes on the transaction.
    /// The callback can run more than once; avoid external side effects and do not commit or dispose it yourself.
    /// Only a false EXEC result is retried. Callback, connection, timeout, cancellation, server, and Cluster routing
    /// exceptions propagate without replay. WATCH and queued keys retain the existing same-slot Cluster requirement.
    /// A canceled accepted commit may still execute. Failed attempts are disposed before backoff and before a new WATCH.
    /// </remarks>
    public static async ValueTask RunTransactionAsync(
        this IRespireClient client, RespireKey[] watchKeys,
        Func<RespireWatchedTransaction, CancellationToken, ValueTask> action,
        RespireTransactionRetryOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        await RunTransactionAsync(client, watchKeys, async (transaction, token) =>
        {
            await action(transaction, token).ConfigureAwait(false);
            return true;
        }, options, cancellationToken).ConfigureAwait(false);
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
                result = await action(transaction, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                committed = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (committed) return result;
            RespireTelemetry.RecordTransactionConflict();
            cancellationToken.ThrowIfCancellationRequested();
            if (attempt == options.MaxAttempts) throw new RespireTransactionConflictException(attempt);
            var delay = options.Backoff?.Invoke(attempt) ?? TimeSpan.Zero;
            if (delay < TimeSpan.Zero || delay.TotalMilliseconds > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(options.Backoff), "Backoff must be between zero and 2,147,483,647 milliseconds.");
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            RespireTelemetry.RecordTransactionRetry();
        }
    }
}
