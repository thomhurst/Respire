using Respire.Infrastructure;
using Respire.Networking;

namespace Respire.Internal;

internal sealed partial class SentinelRouter
{
    private RespireConnectionMultiplexer.ReconnectBacklog? _backlog;

    internal bool IsBacklogDraining => Volatile.Read(ref _backlog)?.IsRunning == true;

    internal RespireConnectionMultiplexer.ReconnectBacklog GetReconnectBacklog(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        lock (_gate)
        {
            ThrowIfBacklogUnavailable();
            if (_backlog is null)
                _backlog = new(capacity, _lifetime.Token, ThrowIfBacklogUnavailable,
                    () => Current?.Multiplexer.ActiveConnectionEndpoint, token => WaitForBacklogConnectionAsync(token));
            else if (_backlog.Capacity != capacity)
                throw new InvalidOperationException("A Sentinel router has one fixed reconnect backlog capacity.");
            return _backlog;
        }
    }

    // Queue callbacks must not take _gate while holding the queue lock: disposal can cancel
    // the router lifetime under _gate. The disposed flag only transitions from false to true.
    private void ThrowIfBacklogUnavailable()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) || core.Disposed, this);

    internal async ValueTask<RespireConnection> WaitForBacklogConnectionAsync(CancellationToken cancellationToken,
        RespireTelemetry.ErrorObservation observation = default)
    {
        var budget = new SentinelRetryBudget(core.Options.ReconnectPolicy);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfBacklogUnavailable();
            Generation? generation = null;
            try
            {
                // A disconnected generation is retired, not reconnected in isolation. Resolve
                // and ROLE-validate the current primary afresh for every queued head.
                generation = await GetGenerationAsync(cancellationToken, observation: observation).ConfigureAwait(false);
                var connection = generation.Multiplexer.GetConnection();
                if (generation.IsRetired || !ReferenceEquals(Current, generation))
                    throw new RespireConnectionRetiredException(connection.Host, connection.Port);
                return connection;
            }
            catch (ObjectDisposedException error) when (generation is { IsRetired: true }
                && !Volatile.Read(ref _disposed) && !core.Disposed)
            {
                // Publication may finish disposing the old generation between selection
                // and GetConnection. Only the old route ended; the queue is still alive.
                ReadEndpointRouter.RecordCandidateFailure(observation, error, generation.Endpoint);
            }
            catch (RespireException error) when (error is RespireConnectionRetiredException
                || error is RespireConnectionException and not RespireReconnectLimitException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfBacklogUnavailable();
                // Each resolver call already bounds its candidate fallbacks. This separate
                // budget bounds repeated discovery rounds for this unsent queue head.
                if (!budget.TryStartRetry())
                    throw new RespireReconnectLimitException("Sentinel backlog recovery exhausted its configured attempts.", error)
                    { Endpoint = error.Endpoint };
                ReadEndpointRouter.RecordCandidateFailure(observation, error, observation.Endpoint ?? error.Endpoint);
                var delay = budget.GetDelay();
                if (delay < TimeSpan.FromMilliseconds(25)) delay = TimeSpan.FromMilliseconds(25);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
