# Valkey 9.2 commands

Valkey 9.2 adds opt-in missing-key replies and server administration commands. Respire tests
these against `valkey/valkey:9.2.0-rc1-alpine` under RESP2 and RESP3. This is a release candidate;
the compatibility matrix will need a new pin when the stable release is available.

## Distinguish missing keys

`SISMEMBER XX` returns `-1` for a missing key. `ContainsIfExistsAsync` maps this to `null`,
`0` to `false`, and `1` to `true`:

```csharp
bool? membership = await client.Sets.ContainsIfExistsAsync("members", "alice");
```

`RangeIfExistsAsync` and `RangeIfExistsWithScoresAsync` send `ZRANGE XX`. They return `null`
when the sorted-set key is absent, an empty array when an existing key has no members in the
requested rank range, and an owned array otherwise. Both support `descending`, typed member
deserialization, and batches or transactions through methods without the `Async` suffix.

```csharp
var members = await client.SortedSets.RangeIfExistsAsync("ranking", start: 0, stop: 9);
var scored = await client.SortedSets.RangeIfExistsWithScoresAsync("ranking");
var binary = await client.SortedSets.RangeIfExistsAsync<byte[]>("ranking");
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
bool moved = await client.Keys.MoveAsync("key", database: 2, replace: true);
```

This sends `MOVE key 2 REPLACE`. The overload also works in batches and transactions.
`replace: false` omits `REPLACE`. MOVE requires a standalone server and rejects negative database
numbers and Cluster clients. Key prefixes apply to the moved key.

## Configuration metadata and ACL roles

```csharp
var configuration = await client.Server.ConfigInfoAsync(["maxmemory", "append*"]);
var digest = await client.Server.AclDigestAsync();
var roles = await client.Server.AclRolesAsync();
var role = await client.Server.AclGetRoleAsync("reader");
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
The regenerated command catalog also includes Valkey 9.2 PathHash descriptors under
`RespireCommands.PathHash`. Descriptor availability does not imply support on older servers.

See the [Valkey 9.2 release notes](https://github.com/valkey-io/valkey/releases/tag/9.2.0-rc1)
and the [server compatibility matrix](server-compatibility.md).
