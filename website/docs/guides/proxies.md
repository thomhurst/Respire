---
title: SOCKS5 and HTTP proxies
---

Use `RespireProxy.Socks5` or `RespireProxy.HttpConnect` as the
[`ConnectionFactory`](../fundamentals/connections.md#custom-connection-streams) to reach Redis through a proxy:

```csharp
await using var client = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = [new("redis.internal", 6379)],
    ConnectionFactory = RespireProxy.Socks5(new("proxy.example", 1080)),
    ConnectTimeout = TimeSpan.FromSeconds(10),
});

await client.SetAsync("key", "value");
```

For HTTP CONNECT, replace the factory:

```csharp
ConnectionFactory = RespireProxy.HttpConnect(new("proxy.example", 8080)),
```

Each invocation creates a new owned TCP stream. Respire disposes it when the connection
ends; the helper disposes the socket if connection or negotiation fails. Reconnects,
pub/sub, dedicated connections, and discovered endpoints each negotiate their own tunnel.
Proxy negotiation happens only when opening a connection, outside command hot paths.

## DNS and destinations

Keep Redis endpoints in `Endpoints`; the proxy belongs only in the factory. Both helpers
send DNS hostnames to the proxy for resolution, including international names converted to
ASCII IDN form. IPv4 and IPv6 literals are supported; scoped IPv6 addresses and Unix sockets
are rejected. Only the proxy's hostname is resolved locally.

Address mapping runs before the factory. Ensure the proxy can reach all mapped destinations,
including nodes discovered through Cluster or Sentinel. HTTP proxies must permit CONNECT
to the Redis port; many allow only port 443 by default.

## Authentication and transport

Both helpers accept optional `username` and `password` arguments. Supply both or neither.
Load secrets from your secret store rather than embedding them in source code.

```csharp
ConnectionFactory = RespireProxy.Socks5(
    new("proxy.example", 1080), proxyUsername, proxyPassword),

// Or:
ConnectionFactory = RespireProxy.HttpConnect(
    new("proxy.example", 8080), proxyUsername, proxyPassword),
```

| Helper | Supported authentication |
| --- | --- |
| SOCKS5 | Anonymous, or RFC 1929 username/password (each 1–255 UTF-8 bytes). Credentials require authentication; there is no anonymous fallback. GSSAPI is unsupported. |
| HTTP CONNECT | Anonymous, or preemptive Basic. Credentials must be printable ASCII; the username cannot contain a colon. Digest, NTLM, Negotiate, and challenge retries are unsupported. |

Proxy transport is **unencrypted TCP**. SOCKS5 passwords travel in clear text and HTTP Basic
credentials are merely base64 encoded. Use these credentials only over a trusted or otherwise
encrypted network path. Redis TLS starts after the tunnel opens, so it does **not** protect
proxy credentials or hide the destination from the proxy. Without Redis TLS, the proxy can also
read Redis credentials and command traffic.

HTTPS proxies, SOCKS4, SOCKS UDP/BIND, HTTP/2 proxies, and redirect following are unsupported.
Use a custom connection factory if you need another proxy transport or authentication scheme.

## Redis TLS

Enable TLS normally. The helper returns the tunnel before the Redis TLS handshake:

```csharp
await using var client = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = [new("redis.internal", 6380)],
    ConnectionFactory = RespireProxy.HttpConnect(new("proxy.example", 8080)),
    UseTls = true,
});
```

Certificate validation and SNI use the Redis hostname, with the usual `TlsOptions.TargetHost`
and `UseMappedHostForTls` overrides. Configure trusted Redis certificates; do not disable
certificate validation to accommodate the proxy.

## Timeouts and failures

`ConnectTimeout` covers connecting to the proxy, negotiating the tunnel, and Redis TLS.
Caller cancellation also interrupts these operations. When invoking a factory directly,
provide a cancellation token with a deadline: the helper has no independent timeout.

Protocol failures throw `IOException`, with the SOCKS reply code or HTTP status where available.
Socket connection failures retain their socket exception; cancellation retains cancellation
semantics. Errors never include proxy credentials or arbitrary response text. HTTP headers,
including all informational responses, have a combined 16 KiB limit; SOCKS address lengths
are bounded by the protocol. HTTP/1.0 and HTTP/1.1 replies are accepted, with any 2xx status
opening the tunnel. Authentication rejection (such as HTTP 407) closes the connection.

The protocols are described in [RFC 1928](https://www.rfc-editor.org/rfc/rfc1928),
[RFC 1929](https://www.rfc-editor.org/rfc/rfc1929), and
[HTTP CONNECT in RFC 9110](https://www.rfc-editor.org/rfc/rfc9110.html#section-9.3.6).
