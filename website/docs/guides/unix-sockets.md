---
title: Unix domain sockets
description: Connect to a local Redis server through its unixsocket listener instead of TCP.
---

# Unix domain sockets

Respire can connect to a Redis `unixsocket` listener on the same host instead of going through TCP. Use it when Redis runs locally, or in a container that shares its socket directory with your application.

```csharp
await using var redis = await RespireClient.ConnectAsync("unix:///run/redis/redis.sock?db=2");

// Equivalent endpoint with structured options:
var options = new RespireOptions
{
    Endpoints = [RespireEndpoint.UnixSocket("/run/redis/redis.sock")],
    Database = 2,
};
```

## Connection strings

### URI form

`unix:///path` and `redis+unix:///path` are both accepted.

- The URI path is the socket path, not a database number. Use the `db` query parameter to select a database. Other [URI query options](../fundamentals/connections.md#uri-query-options) work as for `redis://`.
- Paths are percent-decoded. Escape `#`, `?`, and spaces when they are part of a file name.
- The URI must have no host, port, user information, or fragment. Supply credentials through `RespireOptions.Username` and `RespireOptions.Password`, a credential provider, or the StackExchange.Redis form below.

### StackExchange.Redis form

Prefix the path with `!`, then add ordinary comma-separated options:

```text
!/run/redis/redis.sock,user=app,password=secret,defaultDatabase=2
```

### Endpoint factory

`RespireEndpoint.UnixSocket(path)` accepts absolute paths. It also accepts relative paths and resolves them against the current directory when called. It rejects the root path and paths that contain a NUL byte, so Linux abstract socket names are not supported.

## Endpoint representation

- `RespireEndpoint.Host` holds the socket path and `Port` is `0`.
- `IsUnixSocket` identifies a socket endpoint, including endpoints reported in connection events.
- `ToString()` returns the `!path` form.
- When Respire matches endpoints, socket paths compare case-sensitively. DNS host names compare case-insensitively.
- Credential providers receive only a cancellation token, not the endpoint.

## Behavior

- Command, blocking-pool, pub/sub, cache-tracking, and reconnect connections all use the socket.
- RESP2 and RESP3 negotiation and authentication work as they do over TCP.
- TCP no-delay and keepalive options are not applied.
- Redis Unix sockets do not use TLS. Setting `UseTls = true` with a socket endpoint throws `RespireConfigurationException`. Protect the socket with filesystem permissions, and configure Redis ACLs as needed.

## Supported topologies

Standalone primary endpoints and explicitly configured replica endpoints can use sockets. Cluster and Sentinel discovery advertise TCP addresses, not file paths. Combining socket endpoints with either discovery mode throws `RespireConfigurationException` before any I/O.

Automatic maintenance notifications are turned off on socket connections. Setting `MaintenanceNotifications` to `Enabled` with a socket endpoint throws `RespireConfigurationException`, because handoffs target advertised TCP endpoints.

## Platform support

The operating system must support `UnixDomainSocketEndPoint`. Docker Desktop's cross-OS bind mounts do not carry Unix sockets from a Linux container to a Windows host, so a socket shared that way works only on Linux hosts.
