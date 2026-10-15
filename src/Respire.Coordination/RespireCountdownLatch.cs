using System.Globalization;

namespace Respire.Coordination;

/// <summary>A single-use countdown generation backed by Redis.</summary>
/// <remarks>Reset creates a new generation. Waiters on the old generation return <see langword="false"/>. Redis owns the count; the client is not owned or disposed by this object.</remarks>
public sealed class RespireCountdownLatch
{
    private readonly IRespireClient _client;
    private readonly RespireKey _key;
    private readonly string _generation;
    private readonly string _channel;

    internal RespireCountdownLatch(IRespireClient client, RespireKey key, string generation, string channel)
        => (_client, _key, _generation, _channel) = (client, key, generation, channel);

    /// <summary>The key before the client's configured prefix.</summary>
    public RespireKey Key => _key;

    /// <summary>Unique token identifying this reset generation.</summary>
    public string Generation => _generation;

    /// <summary>Atomically decrements this generation. Returns remaining count, or -1 if reset replaced it.</summary>
    public ValueTask<long> CountDownAsync(CancellationToken cancellationToken = default)
        => CoordinationCommandHooks.RunAsync(_client, "LATCH COUNTDOWN", cancellationToken,
            (Latch: this, Token: cancellationToken), static s => s.Latch.CountDownUnhookedAsync(s.Token));

    private async ValueTask<long> CountDownUnhookedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var result = await CoordinationCommandHooks.ExecuteAsync(_client, RespireCoordination.CountDownLatch,
            [_key], [_generation, _channel], cancellationToken).ConfigureAwait(false);
        return long.TryParse(result.AsString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var remaining)
            ? remaining
            : throw new RespireProtocolException("Redis returned an invalid countdown-latch count.");
    }

    /// <summary>Waits until this generation reaches zero. Returns false if reset replaced it or the latch key was removed.</summary>
    /// <remarks>Pub/Sub notifications wake the waiter early. The waiter also re-reads Redis periodically, so a lost notification delays completion but cannot block it.</remarks>
    public ValueTask<bool> WaitAsync(CancellationToken cancellationToken = default)
        => CoordinationCommandHooks.RunAsync(_client, "LATCH WAIT", cancellationToken,
            (Latch: this, Token: cancellationToken), static s => s.Latch.WaitUnhookedAsync(s.Token));

    private async ValueTask<bool> WaitUnhookedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var initialState = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
        if (initialState.Generation != _generation) return false;
        if (initialState.Remaining == 0) return true;

        // The wait owns this subscription. Its admission and owed teardown are internal
        // legs, while opaque custom clients retain their existing dispatch behavior.
        var subscription = await (_client is RespireClient concrete
            ? concrete.SubscribeUnhookedAsync(new RespireChannel(_channel), cancellationToken)
            : _client.SubscribeAsync(new RespireChannel(_channel), cancellationToken: cancellationToken)).ConfigureAwait(false);
        var messages = subscription.GetAsyncEnumerator(cancellationToken);
        Task<bool>? pending = null;
        try
        {
            while (true)
            {
                var state = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
                if (state.Generation != _generation) return false;
                if (state.Remaining == 0) return true;

                pending ??= messages.MoveNextAsync().AsTask();
                using (var resync = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    var delay = Task.Delay(ResyncInterval, resync.Token);
                    var completed = await Task.WhenAny(pending, delay).ConfigureAwait(false);
                    await resync.CancelAsync().ConfigureAwait(false);
                    if (completed != pending)
                    {
                        // A notification can be lost across a Pub/Sub reconnect; Redis remains authoritative.
                        cancellationToken.ThrowIfCancellationRequested();
                        continue;
                    }
                }

                var moved = await pending.ConfigureAwait(false);
                pending = null;
                if (!moved)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var reason = await subscription.Completion.ConfigureAwait(false);
                    if (reason == RespireSubscriptionEndReason.ReconnectExhausted)
                        throw new RespireReconnectLimitException("Countdown-latch wait ended because Pub/Sub reconnect attempts were exhausted.");
                    throw new RespireConnectionException($"Countdown-latch subscription ended: {reason}.");
                }
            }
        }
        finally
        {
            try
            {
                // Completing the subscription first ends any outstanding MoveNextAsync before the enumerator is disposed.
                if (_client is RespireClient)
                    await subscription.DisposeUnhookedAsync().ConfigureAwait(false);
                else
                    await subscription.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                if (pending is not null)
                {
                    try { await pending.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                }
                await messages.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>How often a waiter re-reads Redis when no notification arrives.</summary>
    internal TimeSpan ResyncInterval { get; set; } = TimeSpan.FromSeconds(5);

    private async ValueTask<(string Generation, long Remaining)> ReadStateAsync(CancellationToken cancellationToken)
    {
        using var result = await RespireCoordination.ReadLatchStateAsync(_client, _key, cancellationToken).ConfigureAwait(false);
        if (result[0].AsString() == "0") return (string.Empty, -1);
        var generation = result[1].AsString();
        var remainingText = result[2].AsString();
        var channel = result[3].AsString();
        if (generation.Length == 0 || channel.Length == 0)
            throw new RespireProtocolException("Redis returned invalid countdown-latch state.");
        if (generation == _generation && channel != _channel)
            throw new RespireProtocolException("Redis returned a countdown-latch channel that does not match this generation.");
        var validRemaining = long.TryParse(remainingText, NumberStyles.None, CultureInfo.InvariantCulture, out var remaining);
        if (!validRemaining || remaining < 0)
            throw new RespireProtocolException("Redis returned invalid countdown-latch state.");
        return (generation, remaining);
    }
}
