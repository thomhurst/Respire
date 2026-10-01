namespace Respire.Internal;

/// <summary>Compares endpoints by port and case-insensitive host name.</summary>
internal sealed class RespireEndpointComparer : IEqualityComparer<RespireEndpoint>
{
    internal static readonly RespireEndpointComparer Instance = new();

    public bool Equals(RespireEndpoint left, RespireEndpoint right)
        => left.Port == right.Port && StringComparer.OrdinalIgnoreCase.Equals(left.Host, right.Host);

    public int GetHashCode(RespireEndpoint endpoint)
        => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(endpoint.Host), endpoint.Port);
}
