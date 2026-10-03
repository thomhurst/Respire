using System.Net;

namespace Respire.Internal;

// Textual identity never resolves DNS. Address evidence and ROLE-accepted peers have
// separate representations so an overlapping DNS set cannot silently become ownership.
internal readonly struct SentinelEndpointIdentity : IEquatable<SentinelEndpointIdentity>
{
    internal string Host { get; }
    internal int Port { get; }
    internal bool IsNumeric { get; }

    internal SentinelEndpointIdentity(RespireEndpoint endpoint)
    {
        IsNumeric = IPAddress.TryParse(endpoint.Host, out var address);
        Host = IsNumeric ? NormalizeAddress(address!) : endpoint.Host;
        Port = endpoint.Port;
    }

    public bool Equals(SentinelEndpointIdentity other)
        => Port == other.Port && StringComparer.OrdinalIgnoreCase.Equals(Host, other.Host);

    public override bool Equals(object? obj) => obj is SentinelEndpointIdentity other && Equals(other);
    // A default identity has no Host; keep default(struct) equality and hashing valid.
    public override int GetHashCode()
        => HashCode.Combine(Host is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Host), Port);

    internal static bool SameEndpoint(RespireEndpoint? left, RespireEndpoint? right)
        => left is { } endpoint ? right is { } other && EndpointComparer.Instance.Equals(endpoint, other) : right is null;

    internal static int EndpointHashCode(RespireEndpoint? endpoint)
        => endpoint is { } value ? EndpointComparer.Instance.GetHashCode(value) : 0;

    // Preserve the runtime's accepted literal syntax, including IPv4 shorthand. Restricting
    // it here would disagree with the existing Sentinel and Socket.ConnectAsync behavior.
    // IPv6 scope IDs remain significant: the same link-local address on another interface
    // is not the same peer. Interface-name syntax is left to the runtime parser; we
    // perform no additional interface lookup.
    internal static string NormalizeHost(string host)
        => IPAddress.TryParse(host, out var address) ? NormalizeAddress(address) : host;

    internal static string NormalizeAddress(IPAddress address)
        => (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();

    internal sealed class EndpointComparer : IEqualityComparer<RespireEndpoint>
    {
        internal static readonly EndpointComparer Instance = new();
        public bool Equals(RespireEndpoint x, RespireEndpoint y)
            => new SentinelEndpointIdentity(x).Equals(new(y));
        public int GetHashCode(RespireEndpoint endpoint) => new SentinelEndpointIdentity(endpoint).GetHashCode();
    }

    internal sealed class AddressComparer : IEqualityComparer<string>
    {
        internal static readonly AddressComparer Instance = new();
        public bool Equals(string? x, string? y)
            => StringComparer.OrdinalIgnoreCase.Equals(x, y) || x is not null && y is not null
                && StringComparer.OrdinalIgnoreCase.Equals(NormalizeHost(x), NormalizeHost(y));
        public int GetHashCode(string address) => StringComparer.OrdinalIgnoreCase.GetHashCode(NormalizeHost(address));
    }
}

// Addresses belong to one observation/lookup lifetime. Consumers retain the existing
// arrays without copying on duplicate hints; unions create a new evidence snapshot.
internal readonly record struct SentinelAddressEvidence
{
    internal RespireEndpoint Endpoint { get; }
    internal string[]? Addresses { get; }
    private readonly SentinelEndpointIdentity _identity;

    internal SentinelAddressEvidence(RespireEndpoint endpoint, string[]? addresses)
    {
        Endpoint = endpoint;
        _identity = new(endpoint);
        Addresses = NormalizeAddresses(addresses);
    }

    private static string[]? NormalizeAddresses(string[]? addresses)
    {
        if (addresses is null) return null;
        string[]? normalized = null;
        for (var index = 0; index < addresses.Length; index++)
        {
            var address = SentinelEndpointIdentity.NormalizeHost(addresses[index]);
            if (StringComparer.Ordinal.Equals(address, addresses[index])) continue;
            normalized ??= (string[])addresses.Clone();
            normalized[index] = address;
        }
        return normalized ?? addresses;
    }

    internal bool HasSameAddresses(string[] addresses)
    {
        if (Addresses is not { } known || known.Length != addresses.Length) return false;
        for (var index = 0; index < known.Length; index++)
            if (!SentinelEndpointIdentity.AddressComparer.Instance.Equals(known[index], addresses[index])) return false;
        return true;
    }

    internal string? SingleAddress
    {
        get
        {
            if (_identity.IsNumeric) return _identity.Host;
            if (Addresses is not { Length: > 0 }) return null;
            var address = Addresses[0];
            for (var index = 1; index < Addresses.Length; index++)
                if (!StringComparer.OrdinalIgnoreCase.Equals(address, Addresses[index])) return null;
            return address;
        }
    }

    internal bool ConfirmsPeer(RespireEndpoint peer)
        => Endpoint.Port == peer.Port && SingleAddress is { } address
            && SentinelEndpointIdentity.AddressComparer.Instance.Equals(address, peer.Host);

    internal bool ConfirmsSameAddress(SentinelAddressEvidence other)
        => Endpoint.Port == other.Endpoint.Port && other.SingleAddress is { } address
            && ConfirmsPeer(new(address, other.Endpoint.Port));

    // Conservative demotion fencing needs only a possible match. This must not be
    // used to confirm ownership or consume a fence: those require ConfirmsPeer.
    internal bool CouldMatch(SentinelAddressEvidence candidate)
    {
        if (_identity.Host is null || candidate._identity.Host is null || Endpoint.Port != candidate.Endpoint.Port) return false;
        if (Contains(candidate._identity.Host)) return true;
        if (candidate.Addresses is not null)
            foreach (var address in candidate.Addresses)
                if (Contains(address)) return true;
        return false;
    }

    private bool Contains(string host)
    {
        // Both observations were normalized at construction. Repeated source/candidate
        // comparisons never parse IPs or allocate inside the Cartesian comparison loop.
        var comparer = StringComparer.OrdinalIgnoreCase;
        if (comparer.Equals(_identity.Host, host)) return true;
        if (Addresses is not null)
            foreach (var address in Addresses)
                if (comparer.Equals(address, host)) return true;
        return false;
    }
}

internal readonly record struct SentinelValidatedPrimary(RespireEndpoint Endpoint, RespireEndpoint? Peer)
{
    public bool Equals(SentinelValidatedPrimary other)
        => SentinelEndpointIdentity.EndpointComparer.Instance.Equals(Endpoint, other.Endpoint)
            && SentinelEndpointIdentity.SameEndpoint(Peer, other.Peer);

    public override int GetHashCode()
        => HashCode.Combine(SentinelEndpointIdentity.EndpointHashCode(Endpoint), SentinelEndpointIdentity.EndpointHashCode(Peer));

    internal bool Matches(RespireEndpoint candidate, string[]? addresses)
    {
        // Fresh DNS takes precedence over the hostname: its owner may have changed
        // since ROLE accepted the physical peer. Ambiguous sets cannot confirm it.
        if (Peer is { } peer && (addresses is { Length: > 0 } || IPAddress.TryParse(candidate.Host, out _)))
            return new SentinelAddressEvidence(candidate, addresses).ConfirmsPeer(peer);
        return SentinelEndpointIdentity.EndpointComparer.Instance.Equals(Endpoint, candidate);
    }
}
