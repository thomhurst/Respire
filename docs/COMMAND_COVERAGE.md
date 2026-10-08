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

`RespireCommand.IsReadOnly` is true only when every provider listed in `Sources` has
authoritative core JSON metadata with the `READONLY` command flag and without `WRITE`.
Conflicting flags or a provider without audited flags produce false. False means either
write-capable or unknown; it is not evidence that a command writes. The pinned inputs are
[Redis 8.10.0](https://github.com/redis/redis/tree/8.10.0/src/commands) and
[Valkey 9.1.1](https://github.com/valkey-io/valkey/tree/9.1.1/src/commands).
Manual module and compatible-server entries do not declare read-only metadata. A repeated
manual entry for a provider does not erase that provider's authoritative core flags.

Caller-supplied descriptors, including a string conversion of `"GET"`, and the default
descriptor always have `IsReadOnly == false`. Names and suffixes do not establish this guarantee.
Read-only scripts and functions use their explicit official variants (`EVAL_RO`, `EVALSHA_RO`,
`FCALL_RO`); the ordinary variants remain false. This property describes command metadata only:
it does not change routing, establish a key layout, or override blocking and connection scope.

Replica-read eligibility for typed verbs and raw/catalog commands comes from the generated
`CommandReadMetadata` table. It uses the same audited provider flags, excludes `TOUCH`
because that command updates primary access metadata, and records cursor classification and
argument positions. `ARSCAN` remains a cursor read without a supported affinity layout.
`MEMORY USAGE` remains eligible and reports node-local memory; `EVAL_RO` and `EVALSHA_RO`
retain their read eligibility and script key positions. Unknown and unaudited module names
remain primary-only. Caller descriptors cannot assert read eligibility; known raw names
are classified through the audited table under the existing raw-command contract.

The table contains no descriptor references, so initializing typed verbs cannot recursively
initialize the catalog. Each verb caches its classification without adding fields; healthy
typed dispatch performs no table lookup. Raw lookup reuses the table directly.

`RespireCommand.RetryCategory` describes the risk of repeating an invocation after an uncertain
outcome. It is metadata only: Respire does not use this property to enable automatic retries.
It does not prove whether socket bytes reached the server, provide exactly-once execution, or
recover the original reply. Even a repeated conditional or replacement write can return a
different result and can interact with another client's intervening changes.

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

The non-`Never` categories are ordered by increasing risk. `Never` is zero so default values
fail closed; a future permission check must exclude it explicitly rather than treating a
numeric threshold as permission. Categories describe command-level risk, not an execution
policy. Option-sensitive `SET` (including `GET`), `ZADD` (including `INCR`) and `BITFIELD`
remain `WriteAccumulating`. Search aggregation/profile commands remain `Never` because their
invocations can create or consume cursors. Ordinary `SCAN` cursors are client-supplied positions
and remain read-only; `FT.CURSOR READ` consumes server-side cursor state and remains `Never`.
All script/function variants remain `Never`, including those with official read-only flags.

This audit is independent of cache effects and replica routing. A cache `ReadOnly` classification
does not establish retry permission. The generator uses unanimous authoritative read flags,
separate explicit command-risk overrides, and a conservative default of `Never`. Pure module
reads have a separate explicit audit; an authoritative `WRITE` flag prevents that read override.
Unlisted writes and extensions remain `Never` until audited. Every generated descriptor has an
explicit category. Typed verbs cache the same independent table during initialization, with no
lookup during healthy dispatch. The added byte uses existing struct padding: on the supported
64-bit runtime, `Verb` remains 32 bytes and `RespireCommand` remains 48 bytes. The independent
frozen table retains one entry per catalog command after initialization.

Default descriptors, implicit string conversions, and `RespireCommand.Create`, even for a known
name or with an explicit cache mutation policy, always have retry category `Never`. Caller-supplied
names cannot acquire retry permission by matching the catalog. This metadata does not add a raw
retry opt-in API or change existing routing, cache invalidation, blocking, or wire behavior.

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

Run `pwsh ./scripts/Test-CommandCatalog.ps1` to exercise the generator with offline fixtures,
including conflicting flags, missing metadata, duplicate providers, and reproducible output.
CI runs this generator check once. Catalog behavior tests run on both supported frameworks.
`RetryCategoryMetadataTests` verifies dangerous and option-sensitive commands, catalog/typed-verb
agreement, fail-closed caller descriptors, unchanged struct sizes, and allocation-free warmed
category access with a positive allocation control. Generator fixtures also prove that consuming
cursors and read-only scripts remain `Never`, and that module read overrides reject `WRITE` flags.
