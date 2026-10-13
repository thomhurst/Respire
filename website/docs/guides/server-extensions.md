# Dragonfly and KeyDB extensions

`RespireCommands.Dragonfly` and `RespireCommands.KeyDb` hold descriptors for those servers' extension commands. Run them with the normal catalog API and dispose each `RespireResult`. No vendor package is needed.

```csharp
await using var dragonfly = RespireClient.Create("redis://dragonfly:6379");
await using var keydb = RespireClient.Create("redis://keydb:6379");

using RespireResult rate = await dragonfly.ExecuteAsync(
    RespireCommands.Dragonfly.CL_THROTTLE, "user:42", 20, 120, 60, 1);
bool limited = rate[0].AsInteger() != 0;
long retryAfterSeconds = rate[3].AsInteger();

using RespireResult added = await dragonfly.ExecuteAsync(
    RespireCommands.Dragonfly.SADDEX, "online", 60, "alice", "bob");

using RespireResult expiry = await keydb.ExecuteAsync(
    RespireCommands.KeyDb.EXPIREMEMBER, "online", "alice", 30);
```

- `CL.THROTTLE` returns five integers: limited flag, total limit, remaining capacity, retry delay, and reset delay. A retry delay of `-1` means allowed.
- `SADDEX` adds members with an expiry in seconds. Its optional `KEEPTTL` keeps existing members' TTLs.
- `EXPIREMEMBER` sets a member expiry, in seconds by default or milliseconds with `ms`.
- The server validates options and returns its own errors, including for unsupported commands, wrong types, and denied ACL permissions.

## Audited descriptors

Names below are exact wire commands. C# identifiers replace punctuation and spaces with underscores: `DFLYCLUSTER_SLOT_MIGRATION_STATUS` sends `DFLYCLUSTER SLOT-MIGRATION-STATUS` as two command words.

| Server | Commands |
| --- | --- |
| Dragonfly data and rate limits | `STICK`, `CL.THROTTLE`, `SADDEX`, `FIELDEXPIRE`, `FIELDTTL`, `RM`, `CF.COMPACT`, `JSON.DEBUG FIELDS`, `JSON.DEBUG HELP` |
| Dragonfly script diagnostics | `SCRIPT LIST`, `SCRIPT LATENCY` |
| Dragonfly memory diagnostics | `MEMORY ARENA`, `MEMORY DECOMMIT`, `MEMORY DEFRAGMENT` |
| Dragonfly Cluster administration | `DFLYCLUSTER CONFIG`, `DFLYCLUSTER FLUSHSLOTS`, `DFLYCLUSTER GETSLOTINFO`, `DFLYCLUSTER SLOT-MIGRATION-STATUS` |
| KeyDB member expiry | `EXPIREMEMBER`, `EXPIREMEMBERAT`, `PEXPIREMEMBERAT` |
| Other KeyDB extensions | `KEYDB.CRON`, `KEYDB.HRENAME`, `KEYDB.MEXISTS`, `KEYDB.NHGET`, `KEYDB.NHSET`, `REPLPING` |

The [catalog audit](https://github.com/thomhurst/Respire/blob/main/docs/COMMAND_COVERAGE.md) records the server versions and source commits behind this list. Your installed server may be older. `Sources` records where a command was documented, not whether your server supports it or behaves the same.

- Dragonfly's `HSETEX` arguments differ from Redis's. Use `RespireCommands.Hash.HSETEX` with the arguments your server documents.
- KeyDB's subkey forms of `TTL`, `PTTL`, and `PERSIST` also use the existing descriptors.
- Private inter-server replication protocols are not included.

:::warning
`CONFIG`, `FLUSHSLOTS`, and `RM` change server state or delete data. The catalog does not emulate or broadcast them, and server ACLs apply. `DFLYCLUSTER` commands need Dragonfly's admin listener (`--admin_port`); target it explicitly.
:::

## Keys and routing

These commands follow the raw execution rules: each argument is a separate binary-safe token, and you supply keys explicitly.

- Prefixed views reject them, except `JSON.DEBUG FIELDS`, whose key is prefixed.
- Key-based commands route by their first key. Administrative commands and the `RM` cursor run on one selected node, without Cluster fan-out. For node-specific administration, use a standalone client aimed at that endpoint.
- `KEYDB.MEXISTS key [key ...]` is multi-key. With `UseCluster`, all keys are slot-checked before I/O and the request is never split, so use same-slot keys such as `{user}:a` and `{user}:b`. Other keys fail locally with `CROSSSLOT`. It returns one boolean per key in input order (0/1 integers under RESP2). See [raw Cluster validation](raw-commands.md#cluster-key-validation).
- These descriptors have no [deferred raw](deferred-raw-commands.md) layouts, so queuing them fails before enqueueing.
- Typed facets keep their Redis/Valkey contracts; using a vendor descriptor does not change them.

Real-server tests cover `CL.THROTTLE`, `SADDEX`, `FIELDTTL`, and `FIELDEXPIRE` on Dragonfly 2.0.0, and `EXPIREMEMBER`, `PEXPIREMEMBERAT`, `KEYDB.HRENAME`, and `KEYDB.MEXISTS` on KeyDB 6.3.4, under RESP2 and RESP3. They use default expiry behavior, not `KEEPTTL` or `ms`, and do not certify every descriptor.

References: [Dragonfly command documentation](https://github.com/dragonflydb/documentation/tree/31881bce033d4cec47cb2e85865d46745760e499/docs/command-reference),
[Dragonfly Cluster administration](https://github.com/dragonflydb/dragonfly/blob/v2.0.0/docs/cluster-mode.md),
[KeyDB commands](https://docs.keydb.dev/docs/commands/), and
[KeyDB 6.3.4 command table](https://github.com/Snapchat/KeyDB/blob/v6.3.4/src/server.cpp).
