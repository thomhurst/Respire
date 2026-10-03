namespace Respire.Internal;

// Keep event identity separate from its display name. Endpoint comparison preserves
// hostname case folding and numeric aliases without concatenating a new event string.
internal readonly record struct SentinelHintKey(string Name, RespireEndpoint? Primary = null,
    RespireEndpoint? Peer = null)
{
    public bool Equals(SentinelHintKey other)
        => StringComparer.Ordinal.Equals(Name, other.Name)
            && SentinelEndpointIdentity.SameEndpoint(Primary, other.Primary)
            && SentinelEndpointIdentity.SameEndpoint(Peer, other.Peer);

    public override int GetHashCode()
        => HashCode.Combine(Name, SentinelEndpointIdentity.EndpointHashCode(Primary), SentinelEndpointIdentity.EndpointHashCode(Peer));
}
