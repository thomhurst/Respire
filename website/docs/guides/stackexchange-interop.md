---
title: Incremental StackExchange.Redis migration
description: Reuse StackExchange.Redis values at the native Respire command boundary.
---

# Incremental StackExchange.Redis migration

Prefer Respire's native typed APIs for normal application code. Install
`Respire.StackExchangeCompat` when migrating code that still passes `RedisKey`,
`RedisValue`, or consumes `RedisResult`. The package uses the repository's pinned
StackExchange.Redis version, currently 3.3.1, and targets .NET 8 and .NET 10.

```csharp
using Respire;
using Respire.StackExchangeCompat;
using StackExchange.Redis;

await using var client = await RespireClient.ConnectAsync("redis://localhost:6379");
RedisKey key = "customer:42";
RedisValue value = new byte[] { 0xff, 0, 0x80 };
CancellationToken cancellationToken = default;

await client.Strings.SetAsync(key.ToRespireKey(), value.ToRespireValue());
RedisResult reply = await client.ExecuteStackExchangeAsync(
    RespireCommands.String.GET, [(byte[]?)key!],
    cancellationToken: cancellationToken);
byte[]? bytes = (byte[]?)reply;
```

The bridge accepts an `IRespireClient`, a native `RespireCommand`, and
`ReadOnlySpan<RedisValue>` arguments. It forwards `RespireCommandFlags` and
`CancellationToken` unchanged. Routing, key-prefix handling, blocking commands,
and server exceptions follow native `ExecuteAsync` semantics. Catalog descriptors
retain catalog routing metadata; a caller-supplied command string retains the
native raw-command behavior. This does not translate StackExchange.Redis
`CommandFlags` or emulate its database API.

Native key-prefixed views can reject raw/catalog commands whose layouts are not
prefixable. Use typed facets with `ToRespireKey`/`ToRespireValue` for those calls;
the bridge preserves the rejection rather than applying additional rewriting.

## Arguments and buffer ownership

`ToRespireKey` rejects null keys and preserves empty keys. `ToRespireValue`
preserves null versus empty values. The bridge rejects null arguments before
I/O because Redis command arguments cannot be null.

Conversions use StackExchange.Redis's byte representation. Binary keys and
values are never decoded as UTF-8. Integers, unsigned integers, doubles, booleans,
strings, memory slices, and sequences retain StackExchange.Redis's wire bytes
without conversion through floating point or the current culture.

Binary storage can be borrowed from the caller. Do not mutate or release the
underlying array, memory, or sequence until the native command completes. The
bridge creates its own argument array; changing the caller's `RedisValue` array
after invocation does not change that array, but its binary buffers remain
subject to the same lifetime requirement. Copy binary buffers first when an
independent snapshot is needed.

## Supported replies

`ToStackExchangeResult` copies supported replies into standalone `RedisResult`
instances. The copy remains valid after the native root is disposed, including
nested aggregates and binary payloads.

| Native reply | `RedisResult.Resp3Type` |
| --- | --- |
| Simple string, bulk string | `SimpleString`, `BulkString` |
| Integer, boolean, double, big number | `Integer`, `Boolean`, `Double`, `BigInteger` |
| Array, map, set | `Array`, `Map`, `Set` |
| RESP2 null bulk string, null array; RESP3 null | `Null` |

Null bulk strings retain `Resp2Type.BulkString`; null arrays retain
`Resp2Type.Array`. RESP3 null uses the factory's scalar null identity. Empty
strings and empty aggregates remain non-null, and map pairs remain flattened
key/value elements. `RespireResult.NullWireType` exposes the original null
framing while `Type` remains `RespDataType.Null` for every null reply.

Error elements, bulk errors, verbatim strings, push replies, attributes, and
other unsupported shapes are rejected with `NotSupportedException` and native
API guidance when presented to the converter. Public
[`RedisResult` factories](https://github.com/StackExchange/StackExchange.Redis/blob/3.3.1/src/StackExchange.Redis/RedisResult.cs)
cannot faithfully construct their full semantics. Errors are never converted
into successful values. Native connection handling consumes attributes and
routes push messages before command results reach this boundary; this bridge
does not expose those out-of-band messages. Top-level server errors keep the
native `RespireServerException` behavior.

The direct converter leaves the caller responsible for disposing the native
root. `ExecuteStackExchangeAsync` disposes its root after conversion, including
conversion failures. It never disposes the caller's client.

## Exact compatibility boundary

This package provides value conversion and a native asynchronous command
bridge. It does not implement `IConnectionMultiplexer`, `IDatabase`,
`ISubscriber`, `IServer`, transactions, or any other StackExchange.Redis client
interface. Downstream libraries that require those interfaces cannot use this
package as a replacement. That compatibility remains pending under
[#889](https://github.com/thomhurst/Respire/issues/889).

Migrate one call site at a time, then replace the bridge with native typed
commands when its callers no longer require StackExchange.Redis values. The
boundary allocates argument arrays and standalone reply copies; native APIs
avoid those migration costs.
