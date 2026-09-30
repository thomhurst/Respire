# Release notes

## Unreleased

### Sorted sets

- Multi-key `PopManyAsync` adds ZMPOP and optional blocking BZMPOP; multi-key
  `PopAsync` adds BZPOPMIN/BZPOPMAX. Results include the selected, owned binary key
  with the view prefix removed, plus string or typed member/score entries.
  Batches and transactions support nonblocking `PopMany`. Existing single-key
  overloads keep their bindings. Implementers of `ISortedSetCommands` and
  `IBatchSortedSetCommands` must implement the new members.
  See the [sorted-set pop guide](SORTED_SET_POPS.md) for versions and wait semantics.

### Key sorting and database helpers

- `Keys.SortAsync` and `SortAsync<T>` support SORT/SORT_RO options, with `SortStoreAsync`
  for STORE's count result. `RandomAsync` returns an owned binary key from an unprefixed
  standalone database; `MoveAsync` moves a key between standalone databases. All have
  batch/transaction counterparts. Custom key-facet implementations must add these
  members. See [key sorting](KEY_SORTING.md) for prefix, Cluster, and version restrictions.


### Scripting

- `Functions` adds Redis 7+ function calls and library administration, with matching
  batch/transaction methods. `RespireFunctionLibrary` supports bounded immediate reloads.
  Existing client/queue implementers retain source compatibility through default facet
  properties; decorators should forward `Functions`. See the [scripting guide](SCRIPTING.md#redis-functions).

- `RespireScript.Create(source, readOnly: true)` selects Redis 7+ read-only Lua commands.
  Script cache management adds `ExistsAsync` and `FlushAsync`, with matching deferred
  operations and deferred `Load`. Existing external `IScriptCommands` and
  `IBatchScriptCommands` implementations remain source-compatible through default
  members that throw `NotSupportedException`; decorators must forward these new members
  to expose them. See the [scripting guide](SCRIPTING.md) for Cluster scope and versions.

### Breaking API changes

- `IKeyCommands` and `IBatchKeyCommands` add absolute expiration and OBJECT metadata
  members. External implementations, decorators, and mocks must implement or forward
  `ExpiryTimeAsync`/`ExpiryTime` (both overloads), `EncodingAsync`/`Encoding`,
  `IdleTimeAsync`/`IdleTime`, `FrequencyAsync`/`Frequency`, and
  `ReferenceCountAsync`/`ReferenceCount`. These APIs are mirrored by batches and
  transactions. See the [key metadata guide](../website/docs/commands/strings-and-keys.md#absolute-expiration-and-object-metadata).

- Count-based set and sorted-set pops are now named `PopManyAsync(key, count, ...)`.
  Rename the corresponding batch and transaction calls from `Pop` to `PopMany`,
  including typed sorted-set calls. Scalar `PopAsync(key, ...)` and `Pop(key, ...)`
  keep their names and return one member; the `Many` methods return arrays.
  List APIs keep their existing `LeftPopManyAsync`/`RightPopManyAsync` names.
  No compatibility aliases are retained during this pre-release API cleanup.
  See [#365](https://github.com/thomhurst/Respire/issues/365) and the
  [collection API conventions](API_DESIGN.md#single-member-and-multi-member-pops).

- Multi-endpoint comma-delimited connection strings now require `cluster=true` for
  Cluster seeds or `serviceName=...` for Sentinel discovery. Standalone
  `RespireOptions.Endpoints` must not contain more than one endpoint; extra endpoints
  previously ignored now cause configuration validation to fail. For connection-time
  fallback between independent deployments, pass separate options to
  `RespireClient.ConnectAnyAsync`. See [#272](https://github.com/thomhurst/Respire/issues/272)
  and the [connection guide](../website/docs/fundamentals/connections.md).

- Pub/Sub message channels and subscription targets now use the owned, binary-safe
  `RespireChannel` value: `RespireMessage.Channel`, nullable `RespireMessage.Pattern`,
  and `RespireSubscription.Targets` no longer expose strings. Use `.Bytes` for exact
  identity and `.ToString()` for UTF-8 display text. Existing string subscription and
  publication overloads remain available; implicit byte-buffer conversions copy their
  inputs. Channel equality compares bytes independently of literal/pattern/sharded
  metadata. See [#300](https://github.com/thomhurst/Respire/issues/300) and the
  [Pub/Sub guide](../website/docs/guides/pub-sub.md).
