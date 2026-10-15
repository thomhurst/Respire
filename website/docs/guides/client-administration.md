# Client administration

Inspect and control server client connections: get a handle to one physical connection, change its settings, pause or unblock clients, and list or kill clients by filter.

`Server.GetClientConnectionAsync()` sends `CLIENT ID` and returns a handle with the connection's server-local `Id` and `Endpoint`. Every command on the handle stays on that socket, even in Cluster mode; it never follows a redirect or switches to a replacement connection.

```csharp
var connection = await redis.Server.GetClientConnectionAsync();
RespireServerClientInfo info = await connection.InfoAsync();
string? name = await connection.GetNameAsync();
RespireClientTrackingInfo tracking = await connection.TrackingInfoAsync();
byte[] echoed = await connection.EchoAsync(new byte[] { 255, 0, 128 });
```

- The parent client owns the socket. The handle needs no disposal and is not exclusive: application commands may share the connection.
- Calling the factory again selects a connection normally. It does not promise a different one or enumerate them all. In Cluster it uses an available node; for a specific endpoint, create a standalone client for it.
- `IsConnected` is a point-in-time check. After socket loss or parent disposal, operations fail. Get a new handle for the replacement; settings are not replayed and never become client-wide defaults.

## Connection settings

Mutation methods need `RespireOptions.AllowAdmin = true`, or they throw `NotSupportedException` before sending. Server ACL checks still apply.

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

| Method | Effect |
| --- | --- |
| `SetInfoAsync` | Sets library metadata shown by CLIENT INFO/LIST. Invalid values (spaces, non-printable characters) return server errors. |
| `SetNoEvictAsync` | Exempts the connection from **client** eviction (not key eviction). |
| `SetNoTouchAsync` | Stops commands on this socket from updating key LRU/LFU data. |

Pass `false` to restore normal behavior. Settings affect all operations sharing the socket, and no other sockets.

`TrackingInfoAsync` is read-only. It does not change Respire's own CLIENT TRACKING state, and CLIENT REPLY, RESET, and tracking mutations are not exposed.

## Endpoint controls

`PauseClientsAsync`, `UnpauseClientsAsync`, and `UnblockClientAsync` act on the handle's **server endpoint**, not just its own connection. All require `AllowAdmin`.

```csharp
var connection = await redis.Server.GetClientConnectionAsync();
await connection.PauseClientsAsync(TimeSpan.FromMilliseconds(250), RespireClientPauseMode.Write);
await connection.UnpauseClientsAsync();
bool released = await connection.UnblockClientAsync(123, RespireClientUnblockMode.Timeout);
```

Pause:

- Duration must be nonnegative whole milliseconds. The pause expires on the server after it.
- `Write` is the default. `All` also suspends reads, for every client of that server.
- A paused command already queued on the same socket can delay a later UNPAUSE. If you must be able to unpause early, get a handle from a separate control client before pausing.

Unblock:

- Takes a positive client ID on the handle's endpoint. IDs from other Cluster nodes are not interchangeable.
- `Timeout` completes the target's blocking call as if it timed out; `Error` makes it receive an UNBLOCKED error.
- Returns `false` when the ID was not blocked.

Cancellation does not undo a mutation the server already accepted. No control operation broadcasts, retries on another node, or resolves an ambiguous outcome.

## Results and Cluster listing

- CLIENT INFO returns `RespireServerClientInfo`. Strings and `Attributes`, including unknown attributes, are owned. An unset name returns `null`.
- Tracking results own their flags and binary prefixes; unknown flags are kept. `Fields` holds every tracking field, including future ones, as a `RespireResult`. Disposing them is optional; once disposed, their nested views are invalid. Typed flags and prefixes are unaffected.
- Echo returns owned bytes and borrows its input until the operation completes.
- Client names, tracking prefixes, metadata, and echo payloads are never key-prefixed.

`Server.ClientsOnAllNodesAsync()` returns a client array or a failure for each endpoint, including replicas and slotless Cluster members. It follows the [server fan-out contract](pub-sub-introspection.md#results-from-every-node): fresh discovery, bounded concurrency, temporary connections, and the same cancellation rules. A temporary connection may appear in CLIENT LIST. Results are not merged or atomic across nodes. Discovery errors throw; afterwards, check each result's `IsSuccess` or `Error` (including cancellation) before reading `Value`.

## Filtering and closing clients

`RespireClientFilterOptions.Include` and `.Exclude` hold positive and negative selectors:

- All supplied selectors must match (AND).
- `Include.Ids` matches any listed ID; `Exclude.Ids` excludes every listed ID.
- ID collections are snapshotted when you call or queue the command. Filter values are never key-prefixed.

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

### Flat properties

The flat properties on `RespireClientFilterOptions` (`Ids`, `User`, `ExcludedIds`, and so on) still work. To migrate:

1. Move positive selectors into `RespireClientIncludeFilters`.
2. Move negative selectors into `RespireClientExcludeFilters`, dropping the `Excluded` prefix.
3. Keep `SkipMe` and `AllowUnfilteredKill` on the outer options.

Either group may be omitted. Once a group is set, any non-null flat scalar or nonempty flat ID list throws synchronously, even if identical. Null flat scalars and empty flat ID lists are ignored; a null ID list is invalid. An empty group adds no selector and does not allow an unfiltered KILL.

### Scope

IDs belong to one server.

- The connection handle filters on its own endpoint and never switches sockets.
- `Server.ClientsAsync(filter, ...)` uses one normally selected node.
- `ClientsOnAllNodesAsync(filter, cancellationToken)` filters on every node and returns per-endpoint results. There is no KILL fan-out.
- The filtered `ClientsAsync` and `ClientsOnAllNodesAsync` overloads require the token argument (pass `default` if unused), so existing calls like `ClientsAsync(default)` still bind.

### Killing clients

`KillClientsAsync` requires `AllowAdmin` and returns the number closed, which can be zero. `KillClientAsync(id, ...)` keeps its boolean return.

- An empty or `SkipMe`-only filter is rejected unless `AllowUnfilteredKill = true`. With that opt-in and no `SkipMe`, Respire sends `SKIPME yes`.
- `SkipMe = null` uses the server default: true for KILL, false for LIST.
- `SkipMe` protects only the executing socket, not other pooled or multiplexed connections of the same client.
- Killing the handle's own socket invalidates the handle. Cancellation cannot reopen closed connections.

:::warning
**Exclusion-only filters can close almost every connection on a node.** They count as selectors, so `Exclude = new() { Type = RespireClientType.PubSub }` allows KILL without `AllowUnfilteredKill` and closes every non-pub/sub connection except the executing socket (with `SkipMe` true). Prefer a positive selector such as an owned client ID.
:::

Empty exclusion values:

- Empty excluded addresses, IPs, names, library names, and library versions are rejected before sending, because they exclude nothing.
- Empty `Exclude.Flags` and `Exclude.Capabilities` are valid and exclude every connection, so they select none.
- Null omits the filter.

### Batches and transactions

Batches and transactions expose `Server.Clients(filter)` (owned typed rows) and `Server.KillClients(filter)` (a count). They work inside MULTI/EXEC; filters run at EXEC.

- A queue targets its execution node, not the endpoint of an earlier client ID.
- In Cluster, keyless batch commands form their own routing group; a transaction uses its selected node.
- For a specific server, use a standalone client for that endpoint. Queueing never reserves a handle's socket.

### Server support

| Filter | Availability |
| --- | --- |
| LIST `Type` / `Ids` | Redis 5 / 6.2 and Valkey |
| KILL `Type`, single `Ids`, `User`, `Address`, `SkipMe` | Redis and Valkey |
| KILL `LocalAddress` | Redis 6.2+ and Valkey |
| KILL `MaximumAgeSeconds` | Redis 7.4+ and Valkey 8+ |
| KILL multiple `Ids`; LIST `User`, `Address`, `LocalAddress`, `SkipMe`, `MaximumAgeSeconds` | Valkey 8.1+ |
| `Name`, `IdleSeconds`, `Flags`, `LibraryName`, `LibraryVersion`, `Database`, `Capabilities`, `Ip`, all `Excluded...` filters | Valkey 9+ |

- `MaximumAgeSeconds` selects connections at least that old; `IdleSeconds`, connections idle at least that long. Both need positive whole seconds.
- Database numbers must be nonnegative; IDs positive.
- All filter paths, including handles and queues, validate and snapshot options synchronously. Invalid options throw before anything is sent or queued.
- Unsupported filters return the server error. Respire never drops them or emulates them with LIST then KILL.
- Redis through 8.10 accepts LIST `Type` or `Ids` alone but rejects both together with a syntax error. Valkey 8.1+ accepts combined LIST filters. KILL accepts combined common filters on both.
- Valkey 9 uses seconds for MAXAGE on LIST and KILL; its [filter implementation](https://github.com/valkey-io/valkey/blob/9.0.0/src/networking.c#L4765) compares whole seconds despite the CLIENT LIST docs saying milliseconds.
- `RespireClientType.Primary` sends `MASTER`. For KILL, `Replica` sends `SLAVE`, which works before Redis 5 and on current Redis/Valkey.

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

- Creating a handle needs CLIENT ID (Redis 5.0), even if the command you plan to send exists on older Redis.
- Unsupported operations return the server error; there is no fallback.
- `GetClientConnectionAsync`, both `ClientsOnAllNodesAsync` overloads, the filtered `ClientsAsync`, and `KillClientsAsync` are `IRespireServerCommands` members. Mocks, decorators, and custom implementations must implement or forward them.

References: [CLIENT INFO](https://redis.io/docs/latest/commands/client-info/),
[SETINFO](https://redis.io/docs/latest/commands/client-setinfo/),
[TRACKINGINFO](https://redis.io/docs/latest/commands/client-trackinginfo/),
[NO-EVICT](https://redis.io/docs/latest/commands/client-no-evict/),
[NO-TOUCH](https://redis.io/docs/latest/commands/client-no-touch/),
[PAUSE](https://redis.io/docs/latest/commands/client-pause/), and
[UNBLOCK](https://redis.io/docs/latest/commands/client-unblock/).
