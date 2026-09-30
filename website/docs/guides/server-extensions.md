# Dragonfly and KeyDB extensions

`RespireCommands.Dragonfly` and `RespireCommands.KeyDb` expose pre-encoded descriptors
for compatible-server extensions. Execute them with the normal catalog API and dispose
each `RespireResult`. These are protocol-shaped results; no vendor package is required.

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

`CL.THROTTLE` returns five integers: limited flag, total limit, remaining capacity,
retry delay, and reset delay. A retry delay of `-1` means the request was allowed.
`SADDEX` adds members with an expiry in seconds; its optional `KEEPTTL` preserves
existing members' TTLs. `EXPIREMEMBER` sets a member expiry and optionally accepts
`ms` instead of its default seconds. The server validates options and returns its
own errors, including unsupported commands, wrong types, and denied ACL permissions.

## Audited descriptors

All names below are exact wire commands. The C# identifiers replace punctuation and
spaces with underscores, for example `DFLYCLUSTER_SLOT_MIGRATION_STATUS` sends
`DFLYCLUSTER SLOT-MIGRATION-STATUS` as two command words.

| Server | Commands |
| --- | --- |
| Dragonfly data and rate limits | `STICK`, `CL.THROTTLE`, `SADDEX`, `FIELDEXPIRE`, `FIELDTTL`, `RM`, `CF.COMPACT`, `JSON.DEBUG FIELDS`, `JSON.DEBUG HELP` |
| Dragonfly script diagnostics | `SCRIPT LIST`, `SCRIPT LATENCY` |
| Dragonfly memory diagnostics | `MEMORY ARENA`, `MEMORY DECOMMIT`, `MEMORY DEFRAGMENT` |
| Dragonfly Cluster administration | `DFLYCLUSTER CONFIG`, `DFLYCLUSTER FLUSHSLOTS`, `DFLYCLUSTER GETSLOTINFO`, `DFLYCLUSTER SLOT-MIGRATION-STATUS` |
| KeyDB member expiry | `EXPIREMEMBER`, `EXPIREMEMBERAT`, `PEXPIREMEMBERAT` |
| Other KeyDB extensions | `KEYDB.CRON`, `KEYDB.HRENAME`, `KEYDB.MEXISTS`, `KEYDB.NHGET`, `KEYDB.NHSET`, `REPLPING` |

The audit uses the KeyDB 6.3.4 command table, Dragonfly's documentation snapshot
`31881bce033d4cec47cb2e85865d46745760e499`, and the Dragonfly 2.0.0 Cluster guide.
Documentation may cover extensions newer than your installed server. `Sources`
records audit provenance, not runtime availability or equivalent semantics across servers.
For example, Dragonfly's `HSETEX` argument form differs from Redis's; use the existing
`RespireCommands.Hash.HSETEX` descriptor with the arguments documented by your server.
KeyDB's subkey forms of `TTL`, `PTTL`, and `PERSIST` also use existing descriptors.

`DFLYCLUSTER` administration requires Dragonfly's admin listener (`--admin_port`),
which should be targeted explicitly. `CONFIG`, `FLUSHSLOTS`, and `RM` change server
state or delete data; the catalog does not emulate or broadcast them. Server ACLs apply.
Private inter-server replication protocols are outside this catalog expansion.

These commands retain the raw execution contract: arguments are separate binary-safe
tokens, caller keys are explicit, and prefixed views reject immediate catalog execution.
Key-based descriptors route using their first key argument. Administrative commands and
the `RM` cursor have no routing key and run on one selected node, without Cluster fan-out.
For node-specific administration, use a standalone client aimed at the intended endpoint.
These vendor descriptors are not added to the deferred raw API's supported key layouts;
unsupported layouts continue to fail before enqueueing.
Typed facets continue to provide their documented Redis/Valkey contracts; selecting a
descriptor does not negotiate a different typed API.

References: [Dragonfly command documentation](https://github.com/dragonflydb/documentation/tree/31881bce033d4cec47cb2e85865d46745760e499/docs/command-reference),
[Dragonfly Cluster administration](https://github.com/dragonflydb/dragonfly/blob/v2.0.0/docs/cluster-mode.md),
[KeyDB commands](https://docs.keydb.dev/docs/commands/), and
[KeyDB 6.3.4 command table](https://github.com/Snapchat/KeyDB/blob/v6.3.4/src/server.cpp).
