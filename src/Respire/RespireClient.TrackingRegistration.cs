using Respire.Commands;
using Respire.Internal;

namespace Respire;

public sealed partial class RespireClient
{
    /// <inheritdoc/>
    public ValueTask RegisterKeyTrackingAsync(RespireKey key, RespireKey? field = null,
        CancellationToken cancellationToken = default)
        => CommandHookScope.RunObservedAsync(_core.Options.CommandHook, field is null ? "GET" : "HGET",
            cancellationToken, (Client: this, Key: key, Field: field, Token: cancellationToken),
            static (state, observation) => state.Client.RegisterKeyTrackingCoreAsync(
                state.Key, state.Field, state.Token, observation));

    private async ValueTask RegisterKeyTrackingCoreAsync(RespireKey key, RespireKey? field,
        CancellationToken token, RespireTelemetry.ErrorObservation observation)
    {
        token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_core.Disposed, this);
        var cache = ReadCache ?? throw new RespireConfigurationException(
            "Key tracking registration requires client-side caching enabled on this view.");
        var physicalKey = ResolveKey(key);
        if (!cache.CanTrack(in physicalKey))
            throw new ArgumentException("The key is outside the configured client cache key prefixes.", nameof(key));

        // Tracking registration must not inherit a cache producer's mutation fence, cached
        // response, or coalescing decision. SendTrackedAsync retains normal routing and ownership.
        using var response = field is { } hashField
            ? await SendTrackedAsync("HGET", new Cmd2(Verbs.HGet, physicalKey.AsValue(), hashField),
                token, track: true, observation: observation).ConfigureAwait(false)
            : await SendTrackedAsync("GET", new Cmd1(Verbs.Get, physicalKey.AsValue()),
                token, track: true, observation: observation).ConfigureAwait(false);
    }
}
