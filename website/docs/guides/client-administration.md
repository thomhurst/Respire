# Client administration

`Server.GetClientConnectionAsync()` sends `CLIENT ID` and returns a handle for one
existing physical connection. The handle exposes its server-local `Id` and `Endpoint`.
All its commands stay on that socket, including in Cluster mode. It never follows a
redirect or silently switches to a replacement connection.

```csharp
var connection = await redis.Server.GetClientConnectionAsync();
RespireServerClientInfo info = await connection.InfoAsync();
string? name = await connection.GetNameAsync();
RespireClientTrackingInfo tracking = await connection.TrackingInfoAsync();
byte[] echoed = await connection.EchoAsync(new byte[] { 255, 0, 128 });
```

The parent client owns the socket. The handle needs no disposal and does not reserve
exclusive use: application commands may share the same multiplexed connection. With
multiple connections, a handle addresses only the identified one. Calling the factory
again selects a connection normally; it does not promise a different connection or an
enumeration of every connection. Cluster selection uses an available execution node.
For a specific endpoint, create a standalone client for that endpoint.

`IsConnected` is a point-in-time observation. After socket loss or parent disposal,
operations fail on the original socket. Acquire a new handle when ready to inspect a
replacement; settings are not replayed after reconnect and do not become client-wide
connection defaults. Commands remain immediate and are not exposed in deferred queues.

## Connection settings

Set `RespireOptions.AllowAdmin = true` before using mutation methods. Permission and
option validation happen before sending the mutation. Server ACL checks still apply.

```csharp
await using var admin = RespireClient.Create(new RespireOptions
{
    Endpoints = [new RespireEndpoint("localhost", 6379)],
    AllowAdmin = true,
});
var connection = await admin.Server.GetClientConnectionAsync();
await connection.SetInfoAsync(RespireClientInfoAttribute.LibraryName, "my-library");
await connection.SetInfoAsync(RespireClientInfoAttribute.LibraryVersion, "1.0");
await connection.SetNoEvictAsync(true);
await connection.SetNoTouchAsync(true);
```

`SetInfoAsync` changes the library metadata shown by CLIENT INFO/LIST. The server validates
attribute values; invalid spaces or non-printable characters produce normal server errors.
`SetNoEvictAsync` changes exemption from **client** eviction, not key eviction.
`SetNoTouchAsync` changes whether commands on this socket affect key LRU/LFU bookkeeping.
Both boolean settings accept `false` to restore ordinary behavior. These settings also
apply to other application operations sharing this socket, and leave other sockets alone.

Tracking inspection is read-only. It does not change Respire's internal CLIENT TRACKING
state or expose unsafe CLIENT REPLY, RESET, or tracking mutations.

## Endpoint controls

The same handle exposes `PauseClientsAsync`, `UnpauseClientsAsync`, and
`UnblockClientAsync`. These commands act on its **server endpoint**, not only its own ID.
They all require `AllowAdmin`.

```csharp
var connection = await redis.Server.GetClientConnectionAsync();
await connection.PauseClientsAsync(TimeSpan.FromMilliseconds(250), RespireClientPauseMode.Write);
await connection.UnpauseClientsAsync();
bool released = await connection.UnblockClientAsync(123, RespireClientUnblockMode.Timeout);
```

Pause duration must be nonnegative whole milliseconds. `Write` is the default; `All`
also suspends reads and affects other clients of that server. A paused command already
queued on this multiplexed socket can delay a later UNPAUSE. Use a separate control
client and acquire its handle before pausing when early unpause must remain available.
A pause also expires server-side after its duration. Cancellation does not undo a pause
or any other mutation already accepted by the server.

UNBLOCK takes a positive client ID belonging to the handle's endpoint. `Timeout` makes
the target's blocking operation complete as if its timeout expired; `Error` makes it
receive an UNBLOCKED error. The result is `false` when that ID was not blocked.
An ID from another Cluster node is not interchangeable. No control operation broadcasts,
retries a mutation on another node, or resolves an ambiguous outcome automatically.

## Results and Cluster listing

CLIENT INFO reuses `RespireServerClientInfo`. Strings and its `Attributes` dictionary
are owned, including unknown attributes. An unset connection name returns `null`.
Tracking results own their flag strings and binary prefixes; unknown flags remain present.
`Fields` preserves every tracking field as a protocol-shaped `RespireResult` backed by
GC-owned storage, including future fields. Disposal of those raw field results is optional;
if explicitly disposed, their nested views become invalid. Typed flags and prefixes remain
independent. Echo returns owned bytes and borrows its input until the operation completes.
Client names, prefixes in tracking replies, metadata, and echo payloads are never key-prefixed.

`Server.ClientsOnAllNodesAsync()` returns separate client arrays or failures associated
with each endpoint, including replicas and slotless Cluster members. It uses the
[server fan-out contract](pub-sub-introspection.md), including fresh discovery, bounded
concurrency, temporary connections, and cancellation behavior. A temporary inspection
connection may appear in CLIENT LIST. Results are neither aggregated nor atomic across
nodes. Discovery errors throw; after discovery, inspect every result's `IsSuccess` or
`Error`, including cancellation failures, before using its `Value`.

## Compatibility

| Command/API | Minimum Redis version |
| --- | --- |
| ECHO | 1.0 |
| CLIENT GETNAME | 2.6.9 |
| CLIENT ID and UNBLOCK | 5.0 |
| CLIENT INFO, TRACKINGINFO, UNPAUSE, PAUSE with explicit mode | 6.2 |
| CLIENT NO-EVICT | 7.0 |
| CLIENT SETINFO and NO-TOUCH | 7.2 |

Creating a handle requires CLIENT ID even when its later command exists on older Redis.
Older or compatible servers that do not support an operation return their normal error;
there is no silent fallback. Required `IServerCommands` additions are
`GetClientConnectionAsync` and `ClientsOnAllNodesAsync`; mocks, decorators, and custom
implementations must implement or forward both. Parent administrative coverage remains
tracked separately from this client-command family.

References: [CLIENT INFO](https://redis.io/docs/latest/commands/client-info/),
[SETINFO](https://redis.io/docs/latest/commands/client-setinfo/),
[TRACKINGINFO](https://redis.io/docs/latest/commands/client-trackinginfo/),
[NO-EVICT](https://redis.io/docs/latest/commands/client-no-evict/),
[NO-TOUCH](https://redis.io/docs/latest/commands/client-no-touch/),
[PAUSE](https://redis.io/docs/latest/commands/client-pause/), and
[UNBLOCK](https://redis.io/docs/latest/commands/client-unblock/).
