# Unix domain sockets

Use a local Redis `unixsocket` listener without going through TCP:

```csharp
await using var redis = await RespireClient.ConnectAsync("unix:///run/redis/redis.sock?db=2");

// Equivalent endpoint with structured options:
var options = new RespireOptions
{
    Endpoints = [RespireEndpoint.UnixSocket("/run/redis/redis.sock")],
    Database = 2,
};
```

`redis+unix:///run/redis/redis.sock?db=2` is also accepted. URI paths are
percent-decoded; escape `#`, `?`, and spaces when they are part of a filename.
The URI must have no host or fragment. The path is the socket name, not a database
number; use the `db` query parameter for database selection.

StackExchange.Redis-style strings use `!/run/redis/redis.sock`, optionally followed
by ordinary comma-delimited options such as `user=app,password=secret,defaultDatabase=2`.
The endpoint factory also accepts relative filesystem paths and resolves them
against the current directory when called. Linux abstract socket names are not supported.

`RespireEndpoint.Host` holds the socket path and `Port` is zero for these endpoints.
`IsUnixSocket` identifies that representation, including endpoints reported in
connection events. Credential providers receive only a cancellation token, not an
endpoint context. `ToString()` emits the `!path`
form. Socket paths are compared case-sensitively; DNS host names retain their
case-insensitive comparison.

Command, blocking-pool, pub/sub, cache tracking, and reconnect connections all use
the socket. RESP2/RESP3 negotiation and authentication work as on TCP. TCP NoDelay
and keepalive options are not applied. Redis Unix sockets do not use TLS; protect
the socket using filesystem permissions and configure Redis ACLs as needed.

Standalone primary and explicitly configured replica endpoints may use sockets.
Cluster and Sentinel discovery advertise TCP addresses, not filesystem paths, so
configuring socket endpoints with those discovery modes is rejected before I/O.
Automatic maintenance notification negotiation is disabled on socket connections;
explicitly enabling TCP maintenance handoffs is rejected.

The operating system must support `UnixDomainSocketEndPoint`. Linux integration
tests start Redis in a container and share its socket directory with the test host.
Docker Desktop's cross-OS bind mounts do not carry Unix sockets to Windows; these
container tests run only on Linux.

## TCP comparison

The **Unix socket benchmarks** workflow runs two BenchmarkDotNet cases on Linux
against the same local Redis server and 128-byte value: `TcpLoopbackGet` (baseline)
and `UnixSocketGet`. It validates with a Dry job, then publishes the measured table
and logs as workflow artifacts and in the job summary. No performance result is
assumed from the transport choice alone.

To run that targeted comparison on a prepared Linux benchmark host, set
`RESPIRE_UNIX_SOCKET` and optionally `RESPIRE_TCP_CONNECTION` (default `127.0.0.1:6379`),
then select `*UnixSocketBenchmarks*` in the existing benchmark project. Both
endpoints must reach the same Redis database.
