using System.Globalization;

namespace Respire.Internal;

internal static class RedirectEndpoint
{
    // Both MOVED/ASK and standalone REDIRECT allow an unbracketed IPv6 address.
    internal static bool TryParse(ReadOnlySpan<char> address, string sourceHost, out RespireEndpoint endpoint)
    {
        endpoint = default;
        address = address.Trim();
        var colon = address.LastIndexOf(':');
        if (colon < 0 || !int.TryParse(address[(colon + 1)..], NumberStyles.None,
                CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            return false;
        var host = address[..colon];
        if (host.Length >= 2 && host[0] == '[' && host[^1] == ']') host = host[1..^1];
        if (host.SequenceEqual("?") || host.ContainsAny(' ', '\r', '\n')
            || host.ContainsAny('[', ']') || host.Contains('\t')) return false;
        endpoint = new(host.IsEmpty ? sourceHost : host.ToString(), port);
        return true;
    }
}
