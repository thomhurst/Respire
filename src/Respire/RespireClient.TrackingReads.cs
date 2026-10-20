using Respire.Commands;
using Respire.Internal;

namespace Respire;

public sealed partial class RespireClient
{
    // Coordination needs Redis to register a read even when a concurrent mutation
    // prevents cache publication. Never serve this registration from the local cache
    // or coalesce it with a producer whose tracking eligibility was decided earlier.
    internal ValueTask TrackKeyUnhookedAsync(RespireKey key, RespireKey? field, CancellationToken token)
        => DispatchResponseSource.Run((Client: this, Key: key, Field: field, Token: token),
            static (state, observation) => state.Client.TrackKeyCoreAsync(
                state.Key, state.Field, state.Token, observation));

    private async ValueTask TrackKeyCoreAsync(RespireKey key, RespireKey? field,
        CancellationToken token, RespireTelemetry.ErrorObservation observation)
    {
        using var response = field is { } hashField
            ? await SendTrackedAsync("HGET", new Cmd2(Verbs.HGet, Key(in key), hashField),
                token, track: true, observation: observation).ConfigureAwait(false)
            : await SendTrackedAsync("GET", new Cmd1(Verbs.Get, Key(in key)),
                token, track: true, observation: observation).ConfigureAwait(false);
    }
}
