---
title: Public API naming
description: Native type prefixes, command result names, and the pre-2.0 migration.
---

# Public API naming

Native public top-level types use the `Respire` prefix across the core and integration
packages. Interfaces put the conventional `I` first: `IRespireClient`,
`IRespireStringCommands`, and `IRespireBatchStringCommands`. This applies to options,
enums, serializers, codecs, credential providers, and extension classes as well as clients.
It lets applications import Respire and StackExchange.Redis together during migration.

Generated command implementation classes remove the conventional interface `I`:
`IRespireJsonCommands` produces `RespireJsonCommandsImplementation`. Rebuild generated
code and update constructor calls when migrating from `<InterfaceName>Implementation`.

Command outcome wrappers end in `Result`, before any generic type parameters:
`RespireGetResult<T>`, `RespireTtlResult`, and `RespireIncrementResult<T>`.
The suffix distinguishes a returned outcome from command inputs such as `RespireExpiry`.

## Deliberate exceptions

- Aspire integration builders and extension classes use `AspireRespire` to identify their
  host integration, for example `AspireRespireClientBuilder` and `AspireRespireExtensions`.
- Host-discovered analyzer, code-fix, and suppressor types in `Respire.Analyzers`, plus
  its `DiagnosticIds` constants, retain their tooling names. Source generators already
  use the `Respire` prefix. These are compiler tooling, not native runtime API types.
- Nested helper types such as `Enumerator` inherit their enclosing type's context.
  They do not need another prefix. Internal protocol types such as `RespValue` are
  implementation details; the public enums are `RespireProtocol` and `RespireDataType`.
- Domain values and entities retain descriptive names such as `RespireValue`,
  `RespireGeoPosition`, `RespireStreamEntry`, and `RespireTimeSeriesSample`, even when
  commands return them. Metadata uses `Info`, `Stats`, `Summary`, `Status`, or `Snapshot`;
  cursor pages use `Page`. These describe the data, rather than wrapping an operation's
  outcome. Lease/lock acquisition handles use `Attempt` because they also own disposal.
- StackExchange.Redis types and adapter signatures keep their upstream names.
  Tests, samples, and build tools are outside the shipped native API rule.

The shipping-assembly naming tests enforce the prefix rule and its explicit exception
list. Public API baselines record the complete signature changes.

## Pre-2.0 migration

This is an intentional source and binary API change. Rebuild consumers and update type
references; no compatibility aliases are provided. Command names, option property names,
configuration keys, enum values, serialization formats, and Redis commands are unchanged.
For example, `SubscriptionOverflow` is still the options property/configuration key,
and its type is now `RespireSubscriptionOverflow`.

| Previous type | Current type |
| --- | --- |
| `StreamReadOptions`, `SetWhen`, `SubscriptionOverflow` | `RespireStreamReadOptions`, `RespireSetWhen`, `RespireSubscriptionOverflow` |
| `RespProtocol`, `RespDataType` | `RespireProtocol`, `RespireDataType` |
| `IStringCommands`, `IBatchStringCommands` (and the other facets) | `IRespireStringCommands`, `IRespireBatchStringCommands` |
| `RespireGet<T>` | `RespireGetResult<T>` |
| `RespireTtl` | `RespireTtlResult` |
| `RespireExpiryTime` | `RespireExpiryTimeResult` |
| `RespireJsonValue<T>` | `RespireJsonGetResult<T>` |
| `RespireArrayAggregate` | `RespireArrayAggregateResult` |
| `SortedSetRank` | `RespireSortedSetRankResult` |
| `RespireAofAcknowledgement` | `RespireAofAcknowledgementResult` |
| `SystemTextJsonSerializer` | `RespireSystemTextJsonSerializer` |
| `BrotliValueCodec`, `DeflateValueCodec`, `Lz4ValueCodec`, `ZstdValueCodec` | Add the `Respire` prefix |
| `AzureManagedRedisCredentialProvider`, `ElastiCacheIamCredentialProvider`, `MemoryDbIamCredentialProvider` | Add the `Respire` prefix |

Other previously unprefixed native options and enums follow the same rule: for example,
`GeoSearchOptions` becomes `RespireGeoSearchOptions`, `IntegerIncrementOptions` becomes
`RespireIntegerIncrementOptions`, and `HashFieldExpiryResult` becomes
`RespireHashFieldExpiryResult`. See the [incremental migration guide](../guides/stackexchange-interop.md)
for the earlier collision-removal renames and examples importing both libraries.
