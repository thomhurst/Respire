using Polly;

namespace Respire.Resilience;

/// <summary>Owns a Respire client whose operations are executed by a caller-supplied resilience pipeline.</summary>
/// <remarks>
/// The pipeline owns callback retries. The underlying client's command retry policy is always null.
/// Callbacks must be safe to replay in their entirety and must not start another retry layer, retain
/// the supplied client, or dispose it. Endpoint circuit breakers and connection recovery remain enabled
/// when configured. The pipeline is borrowed and is not disposed by this instance.
/// Configured hedged reads are preserved and can send more than once per callback; leave
/// RespireOptions.HedgedReads null to disable them. Watched-transaction helpers have a separate
/// WATCH-conflict retry budget, RespireTransactionRetryOptions.MaxAttempts, even when RetryPolicy is null.
/// </remarks>
public sealed class RespireResilienceClient : IAsyncDisposable
{
    private readonly RespireClient _client;
    private readonly ResiliencePipeline _pipeline;
    private int _disposed;

    /// <summary>Creates a lazy client; the first callback that uses it connects.</summary>
    /// <param name="options">Client configuration. RetryPolicy is replaced with null on a copy; other settings are preserved.</param>
    /// <param name="pipeline">The shared pipeline that controls retries, timeouts, and any operation-level circuit breaker.</param>
    public RespireResilienceClient(RespireOptions options, ResiliencePipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pipeline);
        _pipeline = pipeline;
        _client = RespireClient.Create(options with { RetryPolicy = null });
    }

    /// <summary>Executes a replay-safe callback through the pipeline, forwarding its cancellation token.</summary>
    /// <remarks>
    /// The pipeline may invoke the entire callback multiple times. Command retry categories and transport
    /// write evidence do not constrain an outer pipeline. Pass the callback token to every command.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The adapter has been disposed. Thrown synchronously.</exception>
    public ValueTask<TResult> ExecuteAsync<TResult>(
        Func<RespireClient, CancellationToken, ValueTask<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _pipeline.ExecuteAsync(
            static (state, token) => state.Operation(state.Client, token),
            (Client: _client, Operation: operation), cancellationToken);
    }

    /// <summary>Executes a replay-safe callback without a result through the pipeline.</summary>
    /// <remarks>The whole callback can be replayed. Pass its token to every command and do not nest retry helpers.</remarks>
    /// <exception cref="ObjectDisposedException">The adapter has been disposed. Thrown synchronously.</exception>
    public ValueTask ExecuteAsync(
        Func<RespireClient, CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        // Dispatch directly instead of wrapping the callback for the generic overload, avoiding a wrapper allocation.
        return _pipeline.ExecuteAsync(
            static (state, token) => state.Operation(state.Client, token),
            (Client: _client, Operation: operation), cancellationToken);
    }

    /// <summary>Disposes the owned client. The caller retains ownership of the pipeline.</summary>
    public ValueTask DisposeAsync()
        => Interlocked.Exchange(ref _disposed, 1) == 0 ? _client.DisposeAsync() : default;
}
