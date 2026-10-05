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
connection defaults. Connection settings remain immediate. CLIENT LIST/KILL also have
deferred forms with execution-node scope, described below.

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

## Filtering and closing clients

`RespireClientFilterOptions.Include` and `.Exclude` group positive and negative selectors.
Supplied selectors combine with logical AND. Multiple `Include.Ids`
match any ID in the list; `Exclude.Ids` excludes every listed ID. ID collections are
snapshotted when calling or queueing the command. No filter value is key-prefixed.

```csharp
using Respire;

await using var redis = RespireClient.Create(new RespireOptions
{
    Endpoints = [new RespireEndpoint("localhost", 6379)],
    AllowAdmin = true,
});
var cancellationToken = CancellationToken.None;
// Replace these example IDs with IDs of disposable clients your application owns.
var filter = new RespireClientFilterOptions
{
    Include = new() { Ids = [123, 456] },
};
RespireServerClientInfo[] matches = await redis.Server.ClientsAsync(filter, cancellationToken);
var endpoint = await redis.Server.GetClientConnectionAsync();
long closed = await endpoint.KillClientsAsync(new RespireClientFilterOptions
{
    Include = new() { Ids = [123], User = "worker" },
    SkipMe = true,
}, cancellationToken);
```

Existing flat properties such as `Ids`, `User`, and `ExcludedIds` remain supported.
To migrate, move positive selectors into `RespireClientIncludeFilters` and negative
selectors into `RespireClientExcludeFilters`, removing the `Excluded` prefix from their names.
Keep `SkipMe` and `AllowUnfilteredKill` on the outer options. Either group may be omitted.
When either group is supplied, any non-null flat scalar selector or nonempty flat ID list
throws synchronously before sending or queueing, even if both values are identical.
Null flat scalars and empty flat ID lists remain omitted; a null ID list is invalid.
An empty group supplies no selector and does not permit an unfiltered KILL.

IDs belong to one server. The connection handle runs filters on its known endpoint and
never switches sockets. The immediate `Server` forms use one normally selected execution
node. `ClientsOnAllNodesAsync(filter, cancellationToken)` evaluates filters independently
on every discovered node and returns endpoint-tagged results; there is no KILL fan-out.
The filtered `Server.ClientsAsync` and `ClientsOnAllNodesAsync` overloads require the token
argument (use `default` if unused), preserving existing calls such as `ClientsAsync(default)`.

`KillClientsAsync` returns the number closed, including zero, and requires `AllowAdmin`.
The existing `KillClientAsync(id, ...)` overload retains its boolean return and behavior.
An empty or `SkipMe`-only KILL filter is rejected unless `AllowUnfilteredKill = true`
explicitly opts into closing all matching connections. With that opt-in and no `SkipMe`
value, the command uses `SKIPME yes`. `SkipMe` does not protect other sockets
owned by the same client. A null value preserves the server default: true for KILL and
false for LIST. Killing the handle's socket invalidates that handle. Cancellation cannot
undo connections already closed by the server.

**Exclusion-only filters can close almost every connection on a node.** They count as
selectors: `Exclude = new() { Type = RespireClientType.PubSub }`, for example, permits KILL without
`AllowUnfilteredKill` and selects every non-pub/sub connection except the executing socket
when `SkipMe` is true. Prefer a positive selector such as an owned client ID when possible.
`SkipMe` protects only the executing socket, including when the caller owns other pooled
or multiplexed connections.

Empty excluded addresses, IPs, names, library names, and library versions are rejected
before sending or queueing because those values can exclude no connections. Empty
`Exclude.Flags` and `Exclude.Capabilities` instead exclude every connection and select
none; they remain valid. Null means that the corresponding filter is omitted.

Both batches and transactions expose `Server.Clients(filter)` and `Server.KillClients(filter)`.
They return owned typed rows and a count respectively. These commands are supported inside
MULTI/EXEC; their filters run when EXEC executes. A queue targets its execution node, not a
previously observed client ID's endpoint. In Cluster, keyless batch commands form their own
routing group; a transaction uses its selected node. For a specific server, use a standalone
client configured for that endpoint. Queueing never reserves a connection handle's socket.

| Filter | Availability |
| --- | --- |
| LIST `Type` / `Ids` | Redis 5 / 6.2 and Valkey |
| KILL `Type`, single `Ids`, `User`, `Address`, `SkipMe` | Redis and Valkey |
| KILL `LocalAddress` | Redis 6.2+ and Valkey |
| KILL `MaximumAgeSeconds` | Redis 7.4+ and Valkey 8+ |
| KILL multiple `Ids`; LIST `User`, `Address`, `LocalAddress`, `SkipMe`, `MaximumAgeSeconds` | Valkey 8.1+ |
| `Name`, `IdleSeconds`, `Flags`, `LibraryName`, `LibraryVersion`, `Database`, `Capabilities`, `Ip`, all `Excluded...` filters | Valkey 9+ |

`MaximumAgeSeconds` selects connections at least that old; `IdleSeconds` selects connections
idle at least that long. Both require positive whole seconds. Database numbers must be
nonnegative and IDs positive. Unsupported filters remain server errors; Respire does not
silently remove them or emulate selection with a separate LIST followed by KILL.
Valkey 9 uses seconds for MAXAGE on both LIST and KILL. Its
[shared filter implementation](https://github.com/valkey-io/valkey/blob/9.0.0/src/networking.c#L4765)
compares whole-second connection ages, despite the CLIENT LIST reference's milliseconds wording.
`RespireClientType.Primary` uses the compatible `MASTER` wire token. For KILL,
`Replica` uses `SLAVE`, supported before Redis 5 as well as by current Redis/Valkey.
All typed filter paths, including pinned handles and queues, validate and snapshot options
synchronously before returning. Invalid options throw immediately, before any command is sent or queued.

Redis through 8.10 accepts LIST `Type` or `Ids` separately, but rejects their combination
with a syntax error. Valkey 8.1+ supports combined LIST filters. KILL supports combined
common filters on both servers. Respire preserves these server differences.

Sources: [Redis CLIENT LIST](https://redis.io/docs/latest/commands/client-list/),
[Redis CLIENT KILL](https://redis.io/docs/latest/commands/client-kill/), and
[Valkey CLIENT KILL](https://valkey.io/commands/client-kill/).

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
