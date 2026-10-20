using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Respire;

/// <summary>Creates owned TCP tunnels for <see cref="RespireOptions.ConnectionFactory"/>.</summary>
/// <remarks>
/// Proxy transport is unencrypted TCP. Redis TLS is applied by Respire after negotiation and does
/// not protect proxy credentials. Hostnames are resolved by the proxy. Each invocation opens a new
/// socket and uses the supplied cancellation token for connection and negotiation.
/// </remarks>
public static class RespireProxy
{
    /// <summary>Creates a SOCKS5 CONNECT factory with optional username/password authentication.</summary>
    /// <param name="proxy">The TCP proxy endpoint.</param>
    /// <param name="username">Optional username, encoded as 1–255 UTF-8 bytes.</param>
    /// <param name="password">Password, encoded as 1–255 UTF-8 bytes; required with a username.</param>
    /// <remarks>Only no-authentication and RFC 1929 authentication are supported. Supplying credentials requires authentication; it never falls back to anonymous access.</remarks>
    public static Func<RespireEndpoint, CancellationToken, ValueTask<Stream>> Socks5(
        RespireEndpoint proxy, string? username = null, string? password = null)
    {
        var proxyHost = ValidateEndpoint(proxy);
        ValidateCredentialPair(username, password);
        byte[]? authentication = null;
        if (username is not null)
        {
            var encoding = new UTF8Encoding(false, true);
            var user = encoding.GetBytes(username);
            var secret = encoding.GetBytes(password!);
            if (user.Length is < 1 or > 255 || secret.Length is < 1 or > 255)
                throw new ArgumentException("SOCKS5 credentials must each contain 1–255 UTF-8 bytes.");
            authentication = [1, (byte)user.Length, .. user, (byte)secret.Length, .. secret];
        }

        return async (endpoint, token) =>
        {
            var host = ValidateEndpoint(endpoint);
            byte[] address;
            if (TryParseIpLiteral(host, out var ip))
                address = [(byte)(ip.AddressFamily == AddressFamily.InterNetwork ? 1 : 4), .. ip.GetAddressBytes()];
            else
            {
                var name = Encoding.ASCII.GetBytes(host);
                address = [3, (byte)name.Length, .. name];
            }
            byte[] request = [5, 1, 0, .. address, (byte)(endpoint.Port >> 8), (byte)endpoint.Port];
            return await OpenTunnelAsync(proxyHost, proxy.Port, async stream =>
            {
                byte method = authentication is null ? (byte)0 : (byte)2;
                await stream.WriteAsync(new byte[] { 5, 1, method }, token).ConfigureAwait(false);
                var reply = new byte[4];
                await ReadReplyAsync(stream, reply.AsMemory(0, 2), "SOCKS5 method", token).ConfigureAwait(false);
                if (reply[0] != 5 || reply[1] != method)
                    throw new IOException($"SOCKS5 proxy rejected authentication method (version {reply[0]}, method 0x{reply[1]:X2}).");
                if (authentication is not null)
                {
                    await stream.WriteAsync(authentication, token).ConfigureAwait(false);
                    await ReadReplyAsync(stream, reply.AsMemory(0, 2), "SOCKS5 authentication", token).ConfigureAwait(false);
                    if (reply[0] != 1 || reply[1] != 0)
                        throw new IOException("SOCKS5 username/password authentication failed.");
                }
                await stream.WriteAsync(request, token).ConfigureAwait(false);
                await ReadReplyAsync(stream, reply, "SOCKS5 CONNECT", token).ConfigureAwait(false);
                if (reply[0] != 5 || reply[2] != 0)
                    throw new IOException("Malformed SOCKS5 CONNECT response header.");
                if (reply[1] != 0)
                    throw new IOException($"SOCKS5 CONNECT rejected: {SocksError(reply[1])} (0x{reply[1]:X2}).");
                var length = reply[3] switch
                {
                    1 => 4,
                    4 => 16,
                    3 => -1,
                    _ => throw new IOException("Invalid SOCKS5 bound address type."),
                };
                if (length == -1)
                {
                    await ReadReplyAsync(stream, reply.AsMemory(0, 1), "SOCKS5 bound address", token).ConfigureAwait(false);
                    length = reply[0];
                    if (length == 0) throw new IOException("Empty SOCKS5 bound hostname.");
                }
                await ReadReplyAsync(stream, new byte[length + 2], "SOCKS5 bound address", token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
        };
    }

    /// <summary>Creates an HTTP/1.1 CONNECT factory with optional preemptive Basic authentication.</summary>
    /// <param name="proxy">The TCP proxy endpoint (HTTPS proxies are not supported).</param>
    /// <param name="username">Optional printable ASCII username without a colon.</param>
    /// <param name="password">Printable ASCII password; required with a username.</param>
    /// <remarks>Digest, NTLM, Negotiate, redirects, and HTTP/2 are not supported. Response headers, including informational responses, are limited to 16 KiB in total.</remarks>
    public static Func<RespireEndpoint, CancellationToken, ValueTask<Stream>> HttpConnect(
        RespireEndpoint proxy, string? username = null, string? password = null)
    {
        var proxyHost = ValidateEndpoint(proxy);
        ValidateCredentialPair(username, password);
        var authorization = "";
        if (username is not null)
        {
            if (username.Contains(':') || !IsPrintableAscii(username) || !IsPrintableAscii(password!))
                throw new ArgumentException("HTTP Basic credentials must be printable ASCII and the username must not contain a colon.");
            authorization = $"Proxy-Authorization: Basic {Convert.ToBase64String(Encoding.ASCII.GetBytes(username + ":" + password))}\r\n";
        }

        return async (endpoint, token) =>
        {
            var host = ValidateEndpoint(endpoint);
            var authority = (host.Contains(':') ? $"[{host}]" : host) + ":" + endpoint.Port.ToString(CultureInfo.InvariantCulture);
            var request = Encoding.ASCII.GetBytes($"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n{authorization}\r\n");
            return await OpenTunnelAsync(proxyHost, proxy.Port, async stream =>
            {
                await stream.WriteAsync(request, token).ConfigureAwait(false);
                // Read exactly through the blank line: a buffered reader could consume tunnel bytes.
                var buffer = new byte[16 * 1024];
                var used = 0;
                while (true)
                {
                    var start = used;
                    do
                    {
                        if (used == buffer.Length) throw new IOException("HTTP CONNECT response headers exceed 16 KiB.");
                        await ReadReplyAsync(stream, buffer.AsMemory(used++, 1), "HTTP CONNECT headers", token).ConfigureAwait(false);
                    } while (used - start < 4 || !buffer.AsSpan(used - 4, 4).SequenceEqual("\r\n\r\n"u8));
                    var status = ParseHttpResponse(buffer.AsSpan(start, used - start));
                    if (status is >= 200 and < 300) return;
                    if (status is >= 100 and < 200 && status != 101) continue;
                    throw new IOException($"HTTP CONNECT rejected with status {status}.");
                }
            }, token).ConfigureAwait(false);
        };
    }

    private static int ParseHttpResponse(ReadOnlySpan<byte> response)
    {
        var end = response.IndexOf("\r\n"u8);
        var line = response[..end];
        if (line.Length < 13 || !(line.StartsWith("HTTP/1.1 "u8) || line.StartsWith("HTTP/1.0 "u8))
            || line[9] is < (byte)'1' or > (byte)'5' || line[10] is < (byte)'0' or > (byte)'9'
            || line[11] is < (byte)'0' or > (byte)'9' || line[12] != ' ')
            throw new IOException("Malformed HTTP CONNECT status line.");
        ValidateFieldValue(line[13..]);
        var status = (line[9] - '0') * 100 + (line[10] - '0') * 10 + line[11] - '0';
        response = response[(end + 2)..];
        while (response.Length > 2)
        {
            end = response.IndexOf("\r\n"u8);
            line = response[..end];
            var colon = line.IndexOf((byte)':');
            if (colon <= 0) throw new IOException("Malformed HTTP CONNECT header.");
            foreach (var value in line[..colon])
                if (!(value is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z'
                    or >= (byte)'0' and <= (byte)'9') && !"!#$%&'*+-.^_`|~"u8.Contains(value))
                    throw new IOException("Malformed HTTP CONNECT header name.");
            ValidateFieldValue(line[(colon + 1)..]);
            response = response[(end + 2)..];
        }
        return status;
    }

    private static void ValidateFieldValue(ReadOnlySpan<byte> value)
    {
        foreach (var b in value)
            if ((b < 32 && b != 9) || b == 127) throw new IOException("Malformed HTTP CONNECT field value.");
    }

    private static async ValueTask<Stream> OpenTunnelAsync(string host, int port,
        Func<NetworkStream, Task> negotiate, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        NetworkStream? stream = null;
        try
        {
            socket.NoDelay = true;
            await socket.ConnectAsync(host, port, token).ConfigureAwait(false);
            stream = new NetworkStream(socket, ownsSocket: true);
            await negotiate(stream).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return stream;
        }
        catch
        {
            if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
            socket.Dispose();
            throw;
        }
    }

    private static async Task ReadReplyAsync(Stream stream, Memory<byte> bytes, string stage, CancellationToken token)
    {
        try { await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false); }
        catch (EndOfStreamException error) { throw new IOException($"Proxy closed the connection during {stage}.", error); }
    }

    private static string ValidateEndpoint(RespireEndpoint endpoint)
    {
        if (endpoint.Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(endpoint), "A TCP port from 1 to 65535 is required.");
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint.Host);
        var host = endpoint.Host;
        if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
        if (TryParseIpLiteral(host, out var address))
        {
            if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
                throw new ArgumentException("Scoped IPv6 addresses cannot be routed through a proxy.", nameof(endpoint));
            return address.ToString();
        }
        host = new IdnMapping().GetAscii(host);
        // Uri also classifies legacy numeric names as IPv4; preserve them for proxy DNS.
        if (host.Length > 255 || Uri.CheckHostName(host) is not (UriHostNameType.Dns or UriHostNameType.IPv4))
            throw new ArgumentException("A DNS hostname or IP address is required.", nameof(endpoint));
        return host;
    }

    private static bool TryParseIpLiteral(string host, [NotNullWhen(true)] out IPAddress? address)
    {
        // IPAddress accepts shorthand, hex and octal IPv4. Only dotted-decimal IPv4
        // that round-trips is a literal; IPv6 may use any valid colon-based spelling.
        return IPAddress.TryParse(host, out address)
            && (host.Contains(':') || address.ToString() == host);
    }

    private static void ValidateCredentialPair(string? username, string? password)
    {
        if ((username is null) != (password is null)) throw new ArgumentException("Supply both proxy username and password, or neither.");
    }

    private static bool IsPrintableAscii(string value)
    {
        foreach (var c in value) if (c is < ' ' or > '~') return false;
        return true;
    }

    private static string SocksError(byte status) => status switch
    {
        1 => "general failure", 2 => "connection denied by ruleset", 3 => "network unreachable",
        4 => "host unreachable", 5 => "connection refused", 6 => "TTL expired",
        7 => "command not supported", 8 => "address type not supported", _ => "unknown reply code",
    };
}
