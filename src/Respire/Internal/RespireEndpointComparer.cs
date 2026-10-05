namespace Respire.Internal;

/// <summary>Compares TCP hosts case-insensitively and Unix socket paths ordinally.</summary>
internal sealed class RespireEndpointComparer : IEqualityComparer<RespireEndpoint>
{
    internal static readonly RespireEndpointComparer Instance = new();

    public bool Equals(RespireEndpoint left, RespireEndpoint right)
        => left.Port == right.Port && Comparer(left).Equals(left.Host, right.Host);

    public int GetHashCode(RespireEndpoint endpoint)
        => HashCode.Combine(Comparer(endpoint).GetHashCode(endpoint.Host), endpoint.Port);

    private static StringComparer Comparer(RespireEndpoint endpoint)
        => endpoint.IsUnixSocket ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
}
