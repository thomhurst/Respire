# Release notes

## Unreleased

### Breaking API changes

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
