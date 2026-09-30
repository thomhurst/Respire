using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Respire.Networking;

namespace Respire.Internal;

internal sealed partial class DedicatedConnectionPool
{
    private static long _nextRecoveryEpisode;
    private Queue<RespireConnectionStateChange>? _recoveryNotifications;
    private bool _publishingRecovery;

    private async Task<RespireConnection> ConnectWithRecoveryAsync(
        RespireReconnectPolicy policy, CancellationToken cancellationToken)
    {
        // Each rent owns its budget. The initial on-demand connection is immediate and
        // does not consume a replacement attempt, just like initial multiplexer setup.
        var attempt = 0;
        var episode = 0L;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Exception failure;
                try
                {
                    var connection = await RespireConnection.ConnectAsync(
                        host, port, options, logger, cancellationToken).ConfigureAwait(false);
                    if (attempt != 0)
                        QueueRecovery(RespireConnectionState.Connected, null, attempt, episode);
                    return connection;
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested && IsRetryableAcquisitionFailure(error))
                {
                    failure = error;
                }

                if (policy.IsExhausted(attempt))
                {
                    QueueRecovery(RespireConnectionState.Disconnected, failure, attempt, episode, exhausted: true);
                    throw new RespireReconnectLimitException(
                        $"Dedicated connection acquisition to {host}:{port} exhausted {attempt} replacement attempts.", failure);
                }

                if (episode == 0) episode = Interlocked.Increment(ref _nextRecoveryEpisode);
                if (attempt < int.MaxValue) attempt++;
                var delay = policy.GetDelay(attempt);
                QueueRecovery(RespireConnectionState.Reconnecting, failure, attempt, episode, delay);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (attempt != 0 && !cancellationToken.IsCancellationRequested
            && error is not RespireReconnectLimitException)
        {
            QueueRecovery(RespireConnectionState.Disconnected, error, attempt, episode);
            throw;
        }
    }

    private static bool IsRetryableAcquisitionFailure(Exception error) => error switch
    {
        // Handshake errors retain the server response as the connection exception's cause.
        // Credentials, ACLs, invalid databases, and other permanent rejections fail promptly.
        RespireConnectionException { InnerException: RespireServerException server } => server.IsTransient,
        RespireServerException server => server.IsTransient,
        RespireConnectionException { InnerException: { } cause } => IsRetryableAcquisitionFailure(cause),
        // The catch guard excludes caller/pool shutdown. Independent per-attempt connect
        // deadlines can still surface OperationCanceledException and are retryable here.
        SocketException or IOException or RespireConnectionException or RespireTimeoutException
            or OperationCanceledException or TimeoutException => true,
        _ => false,
    };

    private void QueueRecovery(RespireConnectionState state, Exception? error, int attempt,
        long episode, TimeSpan? delay = null, bool exhausted = false)
    {
        var change = new RespireConnectionStateChange(new RespireEndpoint(host, port), state, error)
        {
            ReconnectSource = RespireReconnectSource.Dedicated,
            SourceState = state,
            ReconnectAttempt = attempt,
            ReconnectEpisodeId = episode,
            NextReconnectDelay = delay,
            ReconnectExhausted = exhausted,
        };
        lock (_gate)
        {
            (_recoveryNotifications ??= new()).Enqueue(change);
            if (_publishingRecovery) return;
            _publishingRecovery = true;
            // Observers can synchronously dispose the client. They must never block the
            // acquisition whose reservation disposal is waiting to drain. One worker
            // preserves order; it is not part of the pool's connection ownership graph.
            ThreadPool.UnsafeQueueUserWorkItem(static pool => pool.PublishRecovery(), this, preferLocal: false);
        }
    }

    private void PublishRecovery()
    {
        while (true)
        {
            RespireConnectionStateChange change;
            bool publishState;
            lock (_gate)
            {
                if (!_recoveryNotifications!.TryDequeue(out change))
                {
                    _publishingRecovery = false;
                    return;
                }
                publishState = !_stopping;
            }
            try
            {
                if (change.NextReconnectDelay is { } delay)
                    RespireTelemetry.RecordReconnectAttempt(host, port, change.ReconnectAttempt, delay, change.ReconnectSource);
                if (change.ReconnectExhausted)
                    RespireTelemetry.RecordReconnectExhaustion(host, port, change.ReconnectSource);
                // Measurements describe already scheduled work and may finish after Stop.
                // Skip queued lifecycle callbacks once stopping is observed; callbacks already
                // in flight are deliberately not joined because they can dispose this pool.
                if (publishState) stateChanged?.Invoke(change);
            }
            catch (Exception error)
            {
                logger?.LogWarning(error, "Dedicated connection recovery observer threw");
            }
        }
    }
}
