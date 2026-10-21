# Valkey 9.2 commands

Valkey 9.2 adds opt-in missing-key replies and server administration commands. Respire tests
these against `valkey/valkey:9.2.0-rc1-alpine` under RESP2 and RESP3. This is a release candidate;
the compatibility matrix will need a new pin when the stable release is available.

## Distinguish missing keys

`SISMEMBER XX` returns `-1` for a missing key. `ContainsIfExistsAsync` maps this to `null`,
`0` to `false`, and `1` to `true`:

```csharp
bool? membership = await redis.Sets.ContainsIfExistsAsync("members", "alice");
```

`RangeIfExistsAsync` and `RangeIfExistsWithScoresAsync` send `ZRANGE XX`. They return `null`
when the sorted-set key is absent, an empty array when an existing key has no members in the
requested rank range, and an owned array otherwise. Both support `descending`, typed member
deserialization, and batches or transactions through methods without the `Async` suffix.

```csharp
var members = await redis.SortedSets.RangeIfExistsAsync("ranking", start: 0, stop: 9);
var scored = await redis.SortedSets.RangeIfExistsWithScoresAsync("ranking");
var binary = await redis.SortedSets.RangeIfExistsAsync<byte[]>("ranking");
```

Existing `ContainsAsync`, `RangeAsync`, and `RangeWithScoresAsync` keep their existing reply
types and omit `XX`. Older servers keep working with these APIs. The new methods preserve
unsupported-option errors from older servers; there is no fallback that loses missing-key information.
For `BYSCORE`, `BYLEX`, or legacy range commands with `XX`, use the matching descriptor through
`ExecuteAsync` and check `RespireResult.IsNull` before reading the array.

The empty `XPENDING` summary is `[0, null, null, null]` on Valkey 9.2. Its release notes correct
the reply schema rather than introducing an incompatible wire reply. `PendingSummaryAsync`
already accepts this shape, RESP2 null arrays, and an empty consumers array. It returns zero
pending entries, null minimum and maximum IDs, and an empty consumers array.

## Replace a key in another database

```csharp
bool moved = await redis.Keys.MoveAsync("key", database: 2, replace: true);
```

This sends `MOVE key 2 REPLACE`. The overload also works in batches and transactions.
`replace: false` omits `REPLACE`. MOVE requires a standalone server and rejects negative database
numbers and Cluster clients. Key prefixes apply to the moved key.

## Configuration metadata and ACL roles

```csharp
var configuration = await redis.Server.ConfigInfoAsync(["maxmemory", "append*"]);
var digest = await redis.Server.AclDigestAsync();
var roles = await redis.Server.AclRolesAsync();
var role = await redis.Server.AclGetRoleAsync("reader");
```

`ConfigInfoAsync` returns owned `RespireConfigurationInfo` records with names, server-defined
types and flags, optional aliases, enum values, and numeric bounds. Bounds remain decimal
strings so unsigned values larger than `long.MaxValue` retain their exact value. Unknown fields
remain available in `AdditionalFields`.

`AclGetRoleAsync` returns null for an unknown role. Its `Root` selector and extra `Selectors`
retain commands, binary key/channel rules, and unknown fields, including Valkey's `databases`
rule. `Users` contains owned binary usernames. `AclDigestAsync` and `AclRolesAsync` also return
owned bytes.

With `AllowAdmin = true`, create or update roles with `AclSetRoleAsync`, and delete them with
`AclDeleteRolesAsync`. Rules are separate binary-safe tokens, use Valkey's ACL syntax, and
are applied in order. Role identifiers, patterns, and rules receive no key prefix. These APIs
operate on one execution node; they do not broadcast changes, persist ACL files, or replicate
configuration to other Cluster nodes.

`SET IFNE` already uses `RespireValueCondition`; no new condition type is needed.
## Path Hashes

`PathHashes` provides typed operations for Valkey's Path Hash data type. These APIs are tested
against **9.2.0-rc1**, including RESP2 and RESP3. Valkey 9.2 GA compatibility is unverified until
the stable release exists. Older servers return their unsupported-command error; there is no
fallback to an ordinary hash. The raw descriptors remain available under `RespireCommands.PathHash`.

Each logical path holds field/value pairs. Paths, field names, and payloads are binary-safe.
Only the containing Redis key receives the client's key prefix. Matching compares bytes, so
`/api` also prefixes `/apiculture`; the server does not infer path segments or separators.

```csharp
await redis.PathHashes.SetAsync("routes", "/api", "handler", "api-handler");
await redis.PathHashes.SetFieldsAsync("routes", "/api/users",
    [("handler", "users-handler"), ("policy", "authenticated")]);

string? handler = await redis.PathHashes.GetStringAsync("routes", "/api", "handler");
byte[]? bytes = await redis.PathHashes.GetBytesAsync("routes", "/api", "handler");
var fields = await redis.PathHashes.GetAllAsync("routes", "/api/users");
var longest = await redis.PathHashes.LongestPrefixAsync("routes", "/api/users/42");
var ancestors = await redis.PathHashes.PrefixesAsync("routes", "/api/users/42", count: 2);
```

`LongestPrefixAsync` returns null when no path matches. `PrefixesAsync` returns matching
ancestors from shortest to longest. Its optional `maxPathLength` limits UTF-8 **bytes** before
`count` selects the deepest matches. Entries own their `Path` bytes and `Fields` payloads.
`GetAllAsync` returns an empty array for a missing path. `GetFieldsAsync` returns owned byte
arrays in requested field order, with null for missing fields. `GetManyAsync` accepts
`RespirePathHashRead` groups and returns one flat array in path-group and field order.
`SetManyAsync` accepts `RespirePathHashWrite` groups for an atomic multi-path write.

`SetAsync` and `SetFieldsAsync` accept `RespireSetWhen.NotExists` (`FNX`) and
`RespireSetWhen.Exists` (`FXX`). The condition applies to every supplied field; if any field
fails it, no fields change and the result is false. Generic `SetAsync<T>`, `GetAsync<T>`, and
`TryGetAsync<T>` use the client's serializers. `TryGetAsync<T>` preserves presence for stored
default values such as zero. `CountAsync` counts paths, `ExistsAsync` checks an exact path,
`RemoveAsync` removes supplied fields (or the whole path when fields are omitted), and
`RemovePrefixAsync` deletes descendants of a byte prefix. Empty prefixes match every path.

```csharp
await foreach (var entry in redis.PathHashes.ScanAsync("routes", prefix: "/api", count: 100,
    cancellationToken: cancellationToken))
{
    Console.WriteLine(System.Text.Encoding.UTF8.GetString(entry.Path));
}

var page = await redis.PathHashes.ScanPageAsync("routes", prefix: "/api", count: 100);
if (!page.IsComplete)
    page = await redis.PathHashes.ScanPageAsync("routes", page.Cursor, prefix: "/api", count: 100);
```

Traversal is lexicographic. PHSCAN cursors are owned opaque bytes (`ReadOnlyMemory<byte>`); pass them back unchanged with
the same key and prefix, and stop only when `IsComplete` is true (the single ASCII byte `0`). Omit the cursor or pass empty memory to start a scan. An empty page alone does not end traversal.
Streamed enumerations pin their cursor to its issuing server, including when reading replicas.
Each page is a separate read, so concurrent writes can alter the traversal. Cancellation is
forwarded to page requests and checked between yielded entries. Pages own their response data.

All single-command operations, including `ScanPage`, are available through `batch.PathHashes`
and `transaction.PathHashes` without the `Async` suffix. Queuing snapshots binary inputs.
Streaming `ScanAsync` issues multiple requests and is immediate-only.

```csharp
using var batch = redis.CreateBatch();
var write = batch.PathHashes.Set("routes", "/health", "handler", "health-handler");
var read = batch.PathHashes.GetString("routes", "/health", "handler");
await batch.ExecuteAsync();
Console.WriteLine(read.Result);
```

See the [Valkey 9.2 release notes](https://github.com/valkey-io/valkey/releases/tag/9.2.0-rc1)
and the [server compatibility matrix](server-compatibility.md).
