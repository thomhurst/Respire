using System.Collections.Immutable;
using System.Net;

namespace Respire.Internal;

/// <summary>
/// Captured under the router gate. Identity is opaque; all matching reads immutable
/// endpoint/peer facts, never a live generation or transport.
/// </summary>
/// <remarks>
/// Captures generation identity, endpoint, validated peer, retirement state and accepting command peers.
/// Source and cycle matching may use any captured peer; skipping discovery requires one confirmed peer
/// shared by every command slot (<see cref="ConfirmedPeer"/>).
/// Socket peer hosts already use IPAddress.ToString with mapped IPv4 converted to IPv4.
/// HasPeer callers normalize numeric candidates before comparison. Custom stream transports
/// may supply a hostname fallback; only the exact host matches, never numeric target proof.
/// </remarks>
internal readonly record struct SentinelGenerationEvidence(SentinelGenerationIdentity Identity, RespireEndpoint Endpoint,
    RespireEndpoint? ValidatedPeer, bool IsRetired, ImmutableArray<RespireEndpoint> Peers,
    RespireEndpoint? ConfirmedPeer)
{
    internal bool ConfirmsTarget(RespireEndpoint target)
        => !IsRetired && ConfirmedPeer is { } peer && IPAddress.TryParse(target.Host, out var address)
            && SentinelEndpointIdentity.NormalizeAddress(address) == peer.Host && target.Port == peer.Port;

    internal bool HasPeer(RespireEndpoint peer)
    {
        foreach (var candidate in Peers)
            if (candidate.Host == peer.Host && candidate.Port == peer.Port) return true;
        return false;
    }

    internal bool ShouldRetire(in SentinelHint hint)
    {
        if (IsRetired) return false;
        foreach (var target in hint.Targets)
        {
            if (Matches(target, default, allowHostnameIdentity: false)) return false;
            foreach (var source in hint.Sources)
                if (SentinelEndpointIdentity.EndpointComparer.Instance.Equals(target, source.Endpoint)
                    && Matches(target, source.Addresses)) return false;
        }
        foreach (var source in hint.Sources)
            if (Matches(source.Endpoint, source.Addresses)) return true;
        return false;
    }

    private bool Matches(RespireEndpoint endpoint, ImmutableArray<string> addresses,
        bool allowHostnameIdentity = true)
    {
        var numeric = IPAddress.TryParse(endpoint.Host, out var literal);
        if ((allowHostnameIdentity || numeric)
            && SentinelEndpointIdentity.EndpointComparer.Instance.Equals(Endpoint, endpoint)) return true;
        if (numeric && HasPeer(new(SentinelEndpointIdentity.NormalizeAddress(literal!), endpoint.Port))) return true;
        if (!addresses.IsDefaultOrEmpty)
            foreach (var address in addresses)
                if (HasPeer(new(address, endpoint.Port))) return true;
        return false;
    }
}
