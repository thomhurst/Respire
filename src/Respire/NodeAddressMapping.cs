using System.Collections.ObjectModel;

namespace Respire;

internal static class NodeAddressMapping
{
    private sealed class EndpointComparer : IEqualityComparer<RespireEndpoint>
    {
        internal static readonly EndpointComparer Instance = new();
        public bool Equals(RespireEndpoint x, RespireEndpoint y)
            => x.Port == y.Port && StringComparer.OrdinalIgnoreCase.Equals(x.Host, y.Host);
        public int GetHashCode(RespireEndpoint endpoint)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(endpoint.Host), endpoint.Port);
    }

    internal static IReadOnlyDictionary<RespireEndpoint, RespireEndpoint>? Snapshot(
        IReadOnlyDictionary<RespireEndpoint, RespireEndpoint>? map)
    {
        if (map is null) return null;
        var copy = new Dictionary<RespireEndpoint, RespireEndpoint>(map.Count, EndpointComparer.Instance);
        foreach (var (source, target) in map)
        {
            Validate(source);
            Validate(target);
            if (!copy.TryAdd(source, target))
                throw new RespireConfigurationException("NodeAddressMap contains a duplicate source endpoint.");
        }
        return new ReadOnlyDictionary<RespireEndpoint, RespireEndpoint>(copy);
    }

    internal static void Validate(RespireEndpoint endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint.Host) || endpoint.Host.Any(char.IsWhiteSpace)
            || endpoint.Host.Contains('\0') || endpoint.Port is < 1 or > 65535)
            throw new RespireConfigurationException("Node address mapping requires non-empty TCP hosts and ports from 1 to 65535.");
    }

    internal static IReadOnlyDictionary<RespireEndpoint, RespireEndpoint> Parse(string value)
    {
        var map = new Dictionary<RespireEndpoint, RespireEndpoint>(EndpointComparer.Instance);
        foreach (var entry in value.Split(';'))
        {
            var separator = entry.IndexOf('=');
            if (separator <= 0 || separator == entry.Length - 1 || entry.IndexOf('=', separator + 1) >= 0)
                throw new RespireConfigurationException("NodeAddressMap requires source=target entries separated by semicolons.");
            var source = RespireEndpoint.Parse(entry[..separator]);
            var target = RespireEndpoint.Parse(entry[(separator + 1)..]);
            Validate(source);
            Validate(target);
            if (!map.TryAdd(source, target))
                throw new RespireConfigurationException("NodeAddressMap contains a duplicate source endpoint.");
        }
        return map;
    }
}
