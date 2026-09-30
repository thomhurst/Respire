# Command coverage

Respire exposes commands in two layers:

- Typed facets for common operations, option validation, and natural .NET results.
- `RespireCommands` descriptors for the complete audited command surface. These preserve exact
  RESP command words and return `RespireResult` for server-specific reply shapes.

The generated catalog contains 645 unique descriptors:

| Reference | Version audited | Descriptors |
| --- | --- | ---: |
| Redis command metadata | 8.10.0 | 598 |
| Valkey command metadata | 9.1.1 | 464 |
| Redis integrated modules | Redis 8.10 documentation | Included above |
| Valkey optional Bloom, JSON, and Search modules | Valkey 9.1 documentation | Included above |
| KeyDB extensions | 6.3.4 command table and command reference, audited 2026-09-30 | 9 |
| Dragonfly extensions | Documentation snapshot and 2.0.0 Cluster guide, audited 2026-09-30 | 18 |

Counts overlap because many commands appear in more than one reference. `RespireCommand.Sources`
records provenance; it is not a runtime feature-negotiation guarantee. Server edition,
configuration, loaded modules, permissions, and version still determine whether execution is
accepted.

`RespireCommand.IsReadOnly` is true only when every pinned Redis or Valkey core metadata entry
that defines the command includes the authoritative `READONLY` command flag. Missing flags or
conflicting provider declarations resolve to false. Commands supplied only by the audited
KeyDB, Dragonfly, or module extension lists remain false because those lists do not include
authoritative read-only flags. Caller-supplied commands also remain false. This metadata does
not change command routing or imply that a deployment supports a command.

The compatible-server audit uses [KeyDB's 6.3.4 command table](https://github.com/Snapchat/KeyDB/blob/v6.3.4/src/server.cpp),
the [KeyDB command reference](https://docs.keydb.dev/docs/commands/),
[Dragonfly documentation at 31881bce](https://github.com/dragonflydb/documentation/tree/31881bce033d4cec47cb2e85865d46745760e499/docs/command-reference),
and [Dragonfly 2.0.0's Cluster guide](https://github.com/dragonflydb/dragonfly/blob/v2.0.0/docs/cluster-mode.md).
The Dragonfly documentation snapshot can describe commands newer than a particular server release;
its provenance is not a claim that all 18 commands exist in Dragonfly 2.0.0.
See the [compatible-server guide](../website/docs/guides/server-extensions.md) for the extension list,
execution examples, and scope. Private replication protocols such as DFLYMIGRATE, RREPLAY,
and KEYDB.MVCCRESTORE are outside this client-command audit, as is the internal EXPDEL alias.
Shared command names and server-specific argument forms reuse existing descriptors.

Catalog execution routes blocking commands through the dedicated connection pool. Commands that
change per-connection state remain discoverable but are rejected by `ExecuteAsync`; use Respire's
transaction/subscription APIs or connection options so affinity stays correct.

Known immediate raw key layouts share the deferred layout table and validate all declared
keys before Cluster I/O. Immediate-only layouts include KEYDB.MEXISTS, blocking pops/moves,
MSETEX, stream reads, MIGRATE, and JSON.MGET. Unknown layouts retain server validation;
descriptor provenance alone does not declare a key layout. See
[raw Cluster validation](../website/docs/guides/raw-commands.md#cluster-key-validation).

Typed [server diagnostics](../website/docs/guides/server-diagnostics.md) cover LATENCY
DOCTOR/HISTORY/HISTOGRAM, MEMORY DOCTOR/PURGE, and SLOWLOG LEN, with explicit per-node
variants and owned results. Their Redis and Valkey version requirements are documented
separately from catalog provenance.

## Regeneration

Clone the tagged Redis and Valkey repositories, then run:

```powershell
.\tools\Generate-CommandCatalog.ps1 `
  -RedisCommandPath C:\src\redis\src\commands `
  -ValkeyCommandPath C:\src\valkey\src\commands `
  -RedisVersion 8.10.0 `
  -ValkeyVersion 9.1.1
```

The generator reads the official core JSON metadata and owns the smaller documented module and
compatible-server extension lists. Update those lists from their command references when
upgrading the pinned versions.

## Verification

`CommandCatalogTests` checks the exact descriptor count, source counts, uniqueness, error
behavior, argument boundaries, and the serialized command words of every descriptor. Typed facet
tests additionally cover every convenience command, option form, response parser, and invalid
shape introduced with the catalog.

Run `pwsh tests/Test-CommandCatalogGenerator.ps1` to check read-only flag aggregation against
small Redis and Valkey metadata fixtures, including missing and conflicting flags.
