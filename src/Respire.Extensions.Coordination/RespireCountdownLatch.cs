using System.Globalization;

namespace Respire.Extensions.Coordination;

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
    public async ValueTask<long> CountDownAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var result = await _client.Scripts.ExecuteAsync(RespireCoordination.CountDownLatch,
            [_key], [_generation, _channel], cancellationToken).ConfigureAwait(false);
        return long.TryParse(result.AsString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var remaining)
            ? remaining
            : throw new RespireProtocolException("Redis returned an invalid countdown-latch count.");
    }

    /// <summary>Waits until this generation reaches zero. Returns false if reset replaced it.</summary>
    public async ValueTask<bool> WaitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var subscription = await _client.SubscribeAsync(new RespireChannel(_channel), cancellationToken).ConfigureAwait(false);
        await using var messages = subscription.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            var state = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
            if (state.Generation != _generation) return false;
            if (state.Remaining == 0) return true;
            if (!await messages.MoveNextAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reason = await subscription.Completion.ConfigureAwait(false);
                if (reason == RespireSubscriptionEndReason.ReconnectExhausted)
                    throw new RespireReconnectLimitException("Countdown-latch wait ended because Pub/Sub reconnect attempts were exhausted.");
                throw new RespireConnectionException($"Countdown-latch subscription ended: {reason}.");
            }
        }
    }

    private async ValueTask<(string Generation, long Remaining)> ReadStateAsync(CancellationToken cancellationToken)
    {
        using var result = await _client.Scripts.ExecuteAsync(RespireCoordination.ReadCountdownLatch,
            [_key], cancellationToken: cancellationToken).ConfigureAwait(false);
        return (result[0].AsString(),
            long.TryParse(result[1].AsString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : -1);
    }
}
