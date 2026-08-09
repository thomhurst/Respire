# Command coverage

Respire exposes commands in two layers:

- Typed facets for common operations, option validation, and natural .NET results.
- `RespireCommands` descriptors for the complete audited command surface. These preserve exact
  RESP command words and return `RespireResult` for server-specific reply shapes.

The generated catalog contains 621 unique descriptors:

| Reference | Version audited | Descriptors |
| --- | --- | ---: |
| Redis command metadata | 8.10.0 | 597 |
| Valkey command metadata | 9.1.1 | 463 |
| Redis integrated modules | Redis 8.10 documentation | Included above |
| Valkey optional Bloom, JSON, and Search modules | Valkey 9.1 documentation | Included above |
| KeyDB extensions | Current command reference, 2026-08-09 | 4 |
| Dragonfly extensions | Current command reference, 2026-08-09 | 1 |

Counts overlap because many commands appear in more than one reference. `RespireCommand.Sources`
records provenance; it is not a runtime feature-negotiation guarantee. Server edition,
configuration, loaded modules, permissions, and version still determine whether execution is
accepted.

Catalog execution routes blocking commands through the dedicated connection pool. Commands that
change per-connection state remain discoverable but are rejected by `ExecuteAsync`; use Respire's
transaction/subscription APIs or connection options so affinity stays correct.

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
