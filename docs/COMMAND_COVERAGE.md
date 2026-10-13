# Command coverage

The `RespireCommands` catalog is generated from pinned server metadata. Do not hand-edit it:
regenerate it with `tools/Generate-CommandCatalog.ps1`, and audit every new write or extension
command explicitly, because unaudited commands fail closed (`IsReadOnly == false`,
`RetryCategory == Never`, primary-only).

Respire exposes commands in two layers:

- Typed facets for common operations, option validation and natural .NET results.
- `RespireCommands` descriptors for the full audited surface. They keep exact RESP command words
  and return `RespireResult`.

## Catalog sources

The catalog has 645 unique descriptors. Counts overlap because many commands appear in several
references.

| Reference | Version audited | Descriptors |
| --- | --- | ---: |
| Redis command metadata | 8.10.0 | 598 |
| Valkey command metadata | 9.1.1 | 464 |
| Redis integrated modules | Redis 8.10 documentation | Included above |
| Valkey optional Bloom, JSON, and Search modules | Valkey 9.1 documentation | Included above |
| KeyDB extensions | 6.3.4 command table and command reference, audited 2026-09-30 | 9 |
| Dragonfly extensions | Documentation snapshot and 2.0.0 Cluster guide, audited 2026-09-30 | 18 |

Pinned inputs: [Redis 8.10.0](https://github.com/redis/redis/tree/8.10.0/src/commands),
[Valkey 9.1.1](https://github.com/valkey-io/valkey/tree/9.1.1/src/commands),
[KeyDB 6.3.4 command table](https://github.com/Snapchat/KeyDB/blob/v6.3.4/src/server.cpp),
[KeyDB command reference](https://docs.keydb.dev/docs/commands/),
[Dragonfly documentation at 31881bce](https://github.com/dragonflydb/documentation/tree/31881bce033d4cec47cb2e85865d46745760e499/docs/command-reference)
and [Dragonfly 2.0.0 Cluster guide](https://github.com/dragonflydb/dragonfly/blob/v2.0.0/docs/cluster-mode.md).

- `RespireCommand.Sources` records provenance, not runtime support. Server edition, configuration,
  modules, permissions and version decide whether a command runs.
- The Dragonfly snapshot can describe commands newer than 2.0.0; listing does not claim all 18
  exist there.
- Private replication protocols (DFLYMIGRATE, RREPLAY, KEYDB.MVCCRESTORE) and the internal EXPDEL
  alias are out of scope. Shared names and server-specific argument forms reuse existing
  descriptors. See the [compatible-server guide](../website/docs/guides/server-extensions.md).

## Metadata rules

### `IsReadOnly`

- True only when every provider in `Sources` has authoritative core JSON metadata with `READONLY`
  and without `WRITE`. Conflicting or missing flags give false.
- False means write-capable or unknown, not proof of a write.
- Manual module and compatible-server entries declare no read-only metadata. A repeated manual
  entry does not erase a provider's core flags.
- Caller-supplied descriptors (including a string conversion of `"GET"`) and the default
  descriptor are always false. Names and suffixes never count.
- Read-only scripts use their official variants (`EVAL_RO`, `EVALSHA_RO`, `FCALL_RO`); the ordinary
  variants are false.
- It does not change routing, key layout, blocking or connection scope.

### Replica-read eligibility

The generated `CommandReadMetadata` table drives replica reads for typed verbs and raw/catalog
commands. It uses the same provider flags and records cursor classification and argument
positions.

- `TOUCH` is excluded (it updates primary access metadata).
- `ARSCAN` is a cursor read without a supported affinity layout.
- `MEMORY USAGE` is eligible and reports node-local memory.
- `EVAL_RO` and `EVALSHA_RO` keep read eligibility and script key positions.
- Unknown and unaudited module names are primary-only. Caller descriptors cannot claim eligibility;
  known raw names are classified through the table.
- The table holds no descriptor references, so typed verb initialization cannot recursively
  initialize the catalog. Typed verbs cache their classification; healthy dispatch does no lookup.

### `RetryCategory`

Describes the risk of resending after an uncertain outcome. Retries are off unless
`RespireOptions.RetryPolicy` is set; its `MaxCommandRetryCategory` (default `ReadOnly`) caps which
categories may be resent. See the [command retries guide](../website/docs/guides/command-retries.md).

| Category | Meaning | Examples |
| --- | --- | --- |
| `Always` | Audited stateless operation | `PING`, `ECHO` |
| `Connection` | Audited connection or server-information operation | `CLIENT SETNAME`, `CONFIG GET` |
| `ReadOnly` | Reads without consuming server-side state | `GET`, `JSON.GET` |
| `WriteChecked` | A condition can prevent repeating the write effect | `SETNX`, `HSETNX` |
| `WriteLastWins` | Replaces a value and can overwrite intervening changes | `MSET`, `HSET` |
| `WriteAccumulating` | Can accumulate effects, consume data, or lose the original result | `INCR`, `LPOP`, `GETDEL` |
| `ServerAdmin` | Administrative mutation with server-wide consequences | `CONFIG SET`, `FLUSHALL` |
| `Never` | No automatic retry permission | Scripts, functions, consuming Search cursors, unknown operations |

- Non-`Never` categories are ordered by rising risk. `Never` is zero so defaults fail closed; the
  policy treats it as "no retry".
- A category does not prove whether bytes reached the server, give exactly-once execution or
  recover the original reply. A repeated conditional or replacement write can return a different
  result and interact with other clients' changes.
- Option-sensitive `SET` (including `GET`), `ZADD` (including `INCR`) and `BITFIELD` are
  `WriteAccumulating`.
- Search aggregate/profile commands and `FT.CURSOR READ` are `Never` (they create or consume
  server-side cursors). Ordinary `SCAN` cursors are client positions and stay `ReadOnly`.
- All script and function variants are `Never`, including official read-only ones.
- The generator uses unanimous authoritative read flags, explicit risk overrides and a default of
  `Never`. Pure module reads have a separate audit; an authoritative `WRITE` flag blocks that
  override. Every generated descriptor has an explicit category.
- Retry categories are independent of cache classification and replica routing. A cache
  `ReadOnly` classification does not grant retry permission.
- Default descriptors, implicit string conversions and `RespireCommand.Create` (even for a known
  name or with a cache mutation policy) are `Never`. Opt a caller descriptor in with
  `RespireCommand.WithRetryCategory`; Respire does not validate that declaration.
- Retry metadata does not change routing, cache invalidation, blocking or wire behavior.
- The category byte uses existing struct padding: on 64-bit, `Verb` stays 32 bytes and
  `RespireCommand` 48 bytes. Keep those sizes.

## Execution rules

- Catalog execution sends blocking commands through the dedicated connection pool.
- `ExecuteAsync` rejects commands that change per-connection state. Use the transaction or
  subscription APIs, or connection options, so affinity stays correct.
- Known immediate raw key layouts share the deferred layout table and validate all keys before
  Cluster I/O. Immediate-only layouts: KEYDB.MEXISTS, blocking pops/moves, MSETEX, stream reads,
  MIGRATE, JSON.MGET. Unknown layouts rely on server validation; provenance does not declare a
  layout. See [raw Cluster validation](../website/docs/guides/raw-commands.md#cluster-key-validation).
- Typed [server diagnostics](../website/docs/guides/server-diagnostics.md) (LATENCY
  DOCTOR/HISTORY/HISTOGRAM, MEMORY DOCTOR/PURGE, SLOWLOG LEN) have per-node variants and owned
  results. Their version requirements are documented there, not in catalog provenance.

## Regeneration

Clone the tagged Redis and Valkey repositories, then run:

```powershell
.\tools\Generate-CommandCatalog.ps1 `
  -RedisCommandPath C:\src\redis\src\commands `
  -ValkeyCommandPath C:\src\valkey\src\commands `
  -RedisVersion 8.10.0 `
  -ValkeyVersion 9.1.1
```

The generator reads official core JSON metadata and owns the module and compatible-server
extension lists. Update those lists from their command references when you upgrade the pins.

## Verification

| Check | Covers |
| --- | --- |
| `CommandCatalogTests` (both frameworks) | Descriptor and source counts, uniqueness, errors, argument boundaries, serialized command words |
| Typed facet tests | Every convenience command, option form, response parser and invalid shape |
| `pwsh ./scripts/Test-CommandCatalog.ps1` (CI runs once) | Generator with offline fixtures: conflicting flags, missing metadata, duplicate providers, reproducible output, consuming cursors and read-only scripts stay `Never`, module read overrides reject `WRITE` |
| `RetryCategoryMetadataTests` | Dangerous and option-sensitive commands, catalog/typed-verb agreement, fail-closed caller descriptors, struct sizes, allocation-free category access with a positive control |
