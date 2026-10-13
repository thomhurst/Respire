---
title: Unix domain sockets
description: Connect to a local Redis server through its unixsocket listener instead of TCP.
---

# Unix domain sockets

Connect to a Redis `unixsocket` listener instead of TCP when Redis runs on the same host, or in a container that shares its socket directory with your application.

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

- The URI path is the socket path. Select a database with the `db` query parameter. Other [URI query options](../fundamentals/connections.md#uri-query-options) work as for `redis://`.
- Paths are percent-decoded. Escape `#`, `?` and spaces in file names.
- The URI must have no host, port, user information or fragment. Supply credentials through `RespireOptions.Username` and `RespireOptions.Password`, a credential provider, or the StackExchange.Redis form below.

### StackExchange.Redis form

Prefix the path with `!`, then add comma-separated options:

```text
!/run/redis/redis.sock,user=app,password=secret,defaultDatabase=2
```

### Endpoint factory

`RespireEndpoint.UnixSocket(path)` accepts absolute paths, and relative paths resolved against the current directory at call time. It rejects the root path and paths containing a NUL byte, so Linux abstract socket names are not supported.

## Endpoint representation

- `RespireEndpoint.Host` holds the socket path and `Port` is `0`.
- `IsUnixSocket` identifies a socket endpoint, including in connection events.
- `ToString()` returns the `!path` form.
- Socket paths compare case-sensitively; DNS host names compare case-insensitively.
- Credential providers receive only a cancellation token, not the endpoint.

## Behavior

- Command, blocking-pool, pub/sub, cache-tracking and reconnect connections all use the socket.
- RESP2 and RESP3 negotiation and authentication work as over TCP.
- TCP no-delay and keepalive options are not applied.
- Redis Unix sockets do not use TLS: `UseTls = true` with a socket endpoint throws `RespireConfigurationException`. Protect the socket with filesystem permissions and Redis ACLs.

## Supported topologies

- Standalone primaries and explicitly configured replica endpoints can use sockets.
- Cluster and Sentinel discovery advertise TCP addresses, so combining either with socket endpoints throws `RespireConfigurationException` before any I/O.
- Maintenance notifications are off on socket connections. `MaintenanceNotifications = Enabled` with a socket endpoint throws `RespireConfigurationException`, because handoffs target TCP endpoints.

## Platform support

The operating system must support `UnixDomainSocketEndPoint`. Docker Desktop's cross-OS bind mounts do not carry Unix sockets from a Linux container to a Windows host, so sharing a socket that way works only on Linux hosts.
