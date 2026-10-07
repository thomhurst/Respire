using System.Collections.Immutable;
using System.Net;

namespace Respire.Internal;

/// <summary>
/// Textual identity never resolves DNS. Address evidence and ROLE-accepted peers have
/// separate representations so an overlapping DNS set cannot silently become ownership.
/// </summary>
/// <remarks>
/// Defines endpoint equality and hashing for discovery, monitor registration, hint keys, evidence
/// unions and retained validated owners. Hostname case is ignored; numeric addresses compare by
/// canonical spelling, including IPv4-mapped IPv6 equivalence; ports stay distinct. Epoch-owner
/// equivalence (this type) must stay separate from conservative switch-source matching
/// (<see cref="SentinelAddressEvidence.CouldMatch"/>).
/// </remarks>
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

/// <summary>
/// Addresses belong to one observation/lookup lifetime. External arrays are snapshotted;
/// immutable snapshots retain storage on duplicates and copy only when evidence changes.
/// </summary>
/// <remarks>
/// Switch sources retain this evidence across matching calls. Value equality is typed (no boxing when
/// switch sources are compared or hashed) and keeps array-snapshot identity; ownership and alias
/// matching use the explicit operations instead. Default evidence never matches an observation.
/// <see cref="CouldMatch"/> may fence a possible demoted source, but ownership needs
/// <see cref="ConfirmsPeer"/> with one unambiguous address: overlapping multi-address sets can neither
/// confirm an owner nor consume its demotion fence. Unions produce a new snapshot without assigning
/// chronology.
/// </remarks>
internal readonly record struct SentinelAddressEvidence
{
    internal RespireEndpoint Endpoint { get; }
    internal ImmutableArray<string> Addresses { get; }
    private readonly SentinelEndpointIdentity _identity;
    internal bool IsDefault => _identity.Host is null;

    internal SentinelAddressEvidence(RespireEndpoint endpoint, string[]? addresses)
        : this(addresses is null ? default : ImmutableArray.CreateRange(addresses), endpoint) { }

    private SentinelAddressEvidence(ImmutableArray<string> addresses, RespireEndpoint endpoint)
    {
        Endpoint = endpoint;
        _identity = new(endpoint);
        Addresses = NormalizeAddresses(addresses);
    }

    internal static SentinelAddressEvidence FromSnapshot(RespireEndpoint endpoint, ImmutableArray<string> addresses)
        => new(addresses, endpoint);

    private static ImmutableArray<string> NormalizeAddresses(ImmutableArray<string> addresses)
    {
        if (addresses.IsDefaultOrEmpty) return addresses;
        ImmutableArray<string>.Builder? normalized = null;
        for (var index = 0; index < addresses.Length; index++)
        {
            var address = SentinelEndpointIdentity.NormalizeHost(addresses[index]);
            if (StringComparer.Ordinal.Equals(address, addresses[index])) continue;
            normalized ??= addresses.ToBuilder();
            normalized[index] = address;
        }
        return normalized?.ToImmutable() ?? addresses;
    }

    internal bool HasSameAddresses(string[] addresses)
    {
        if (Addresses.IsDefault || Addresses.Length != addresses.Length) return false;
        for (var index = 0; index < Addresses.Length; index++)
            if (!SentinelEndpointIdentity.AddressComparer.Instance.Equals(Addresses[index], addresses[index])) return false;
        return true;
    }

    internal bool HasSameSnapshot(SentinelAddressEvidence other)
    {
        if (IsDefault || other.IsDefault) return IsDefault && other.IsDefault;
        if (!_identity.Equals(other._identity)
            || Addresses.IsDefault != other.Addresses.IsDefault) return false;
        if (Addresses.IsDefault) return true;
        return ContainsAll(Addresses, other.Addresses) && ContainsAll(other.Addresses, Addresses);

        static bool ContainsAll(ImmutableArray<string> known, ImmutableArray<string> candidates)
        {
            foreach (var candidate in candidates)
            {
                var found = false;
                foreach (var address in known)
                    if (StringComparer.OrdinalIgnoreCase.Equals(address, candidate)) { found = true; break; }
                if (!found) return false;
            }
            return true;
        }
    }

    internal string? SingleAddress
    {
        get
        {
            if (_identity.IsNumeric) return _identity.Host;
            if (Addresses.IsDefaultOrEmpty) return null;
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
        if (IsDefault || candidate.IsDefault || Endpoint.Port != candidate.Endpoint.Port) return false;
        if (Contains(candidate._identity.Host)) return true;
        if (!candidate.Addresses.IsDefaultOrEmpty)
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
        if (!Addresses.IsDefaultOrEmpty)
            foreach (var address in Addresses)
                if (comparer.Equals(address, host)) return true;
        return false;
    }
}

/// <summary>
/// Keeps the advertised endpoint separate from the physical peer accepted by ROLE.
/// </summary>
/// <remarks>
/// Discovery holds provisional DNS evidence apart from its accepted peer. At an observed epoch a numeric alias
/// can confirm the peer even when DNS is unavailable; an unchanged hostname cannot replace it without a newer
/// epoch. Retained down reports keep their observation-time owner when later DNS or publication changes the
/// current owner.
/// </remarks>
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
