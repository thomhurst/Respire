namespace Respire.Internal;

// Keep event identity separate from its display name. Endpoint comparison preserves
// hostname case folding and numeric aliases without concatenating a new event string.
internal readonly record struct SentinelHintKey(string Name, RespireEndpoint? Primary = null,
    RespireEndpoint? Peer = null)
{
    public bool Equals(SentinelHintKey other)
        => StringComparer.Ordinal.Equals(Name, other.Name)
            && SameEndpoint(Primary, other.Primary) && SameEndpoint(Peer, other.Peer);

    public override int GetHashCode()
        => HashCode.Combine(Name, EndpointHashCode(Primary), EndpointHashCode(Peer));

    private static bool SameEndpoint(RespireEndpoint? left, RespireEndpoint? right)
        => left is { } endpoint
            ? right is { } other && SentinelDiscoveryState.EndpointComparer.Instance.Equals(endpoint, other)
            : right is null;

    private static int EndpointHashCode(RespireEndpoint? endpoint)
        => endpoint is { } value ? SentinelDiscoveryState.EndpointComparer.Instance.GetHashCode(value) : 0;
}
