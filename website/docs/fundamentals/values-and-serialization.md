---
title: Values and serialization
description: Understand RespireKey, RespireValue, typed objects, and leased reads.
---

# Values and serialization

Respire separates flexible command inputs from convenient application outputs.

## Keys and input values

`RespireKey` accepts `string`, `byte[]`, or `ReadOnlyMemory<byte>`. `RespireValue` accepts keys, text, binary data (`byte[]`, `Memory<byte>`, `ReadOnlyMemory<byte>`, or `ArraySegment<byte>`), numeric primitives, booleans, `Guid`, `DateTimeOffset`, `TimeSpan`, and `char` through implicit conversions.

```csharp
RespireKey key = "counter";
RespireValue value = 42;

await redis.SetAsync(key, value);
```

These small readonly structs keep command overloads manageable without forcing a protocol union type on every result.

Keys and values use value equality. `RespireValue` compares the exact bulk-string payload sent to Redis, so `5`, `"5"`, and the UTF-8 bytes for `5` are equal and share a hash code. GUIDs use the invariant `D` format, `DateTimeOffset` uses the invariant round-trip `O` format, `TimeSpan` uses the invariant constant `c` format, and `char` uses its one-character textual representation.

## Typed output

Choose the representation at the call site:

```csharp
string? text = await redis.GetStringAsync("payload");
byte[]? bytes = await redis.GetBytesAsync("payload");
Order? order = await redis.GetAsync<Order>("order:42");
```

A missing key returns `null` from string, byte-array, reference-type, and explicitly nullable reads. `GetAsync<T>` returns `default(T)` for a non-nullable value type, so a missing `GetAsync<int>` result is `0`. Numeric and condition commands return `long`, `double`, or `bool` as appropriate.

## Missing keys versus stored defaults

`TryGetAsync<T>` returns a `RespireGet<T>` that reports presence alongside the value, so a missing key stays distinguishable from a stored `default(T)` without a second existence round trip:

```csharp
var (found, hits) = await redis.TryGetAsync<int>("page:hits");

if (!found) { /* key is absent */ }
else if (hits == 0) { /* key holds 0 */ }
```

`RespireGet<T>` also exposes `GetValueOrDefault(fallback)`. The same method exists on `redis.Strings` and, per field, on `redis.Hashes`.

## Object serialization

`SetAsync<T>` and `GetAsync<T>` use `RespireOptions.Serializer` for object values. `SystemTextJsonSerializer` is the default. Supply an `IRespireSerializer` for another format:

<!-- doc-test-ignore: MyMessagePackSerializer is the application's omitted IRespireSerializer implementation. -->
```csharp
var options = new RespireOptions
{
    Endpoints = { new("localhost") },
    Serializer = new MyMessagePackSerializer(),
};
```

Custom serializers must implement all four `IRespireSerializer` methods: generic
`Serialize<T>` / `Deserialize<T>` and runtime-type `Serialize(..., Type, object?)` /
`Deserialize(Type, ...)`. Runtime-type methods use the supplied declared type. They have no
default throwing implementation, so a missing method is reported at compile time.

Typed `string`, `byte[]`, `char`, Boolean, and numeric primitive values bypass object serialization. Numbers use invariant Redis text. Boolean writes use Redis-native `1`/`0`; reads also accept `true`/`false` for interoperability with existing data. Nullable forms use the same fast path when they contain a value.

Objects, enums, and other types use the configured serializer. Custom serializers therefore do not control primitive encoding. Pass a `RespireValue` explicitly when a command input must use raw Redis scalar conventions, as shown in [Keys and input values](#keys-and-input-values).

Serializing overloads sit next to the `RespireValue` ones wherever a facet takes a single payload — `Hashes.SetAsync<T>`, `Sets.ContainsAsync<T>`, `SortedSets.AddAsync<T>`, and the `Lists.LeftPopAsync<T>` / `RightPopAsync<T>` reads. An argument already typed as `RespireValue` selects the raw overload; anything else selects the generic one. The new facet overloads preserve raw `ReadOnlyMemory<byte>`, textual characters, and non-finite floating-point arguments because those previously bound to `RespireValue`. Boolean values use the same `1`/`0` encoding on generic, raw, and collection-member paths.

## Zero-copy leased reads

Normal reads prioritize convenient managed values. Large or hot-path payloads can opt into pooled memory:

```csharp
using RespireLease lease = await redis.Strings.GetLeaseAsync("blob:4mb");

if (!lease.IsNull)
{
    Process(lease.Span);
}
```

The memory remains valid only until `Dispose`. The `Lease` name makes that ownership obligation visible.

## Streaming large reads

`GetStreamAsync` exposes a Redis bulk value as a readable stream without creating a value-sized
managed array:

```csharp
await using Stream? value = await redis.Strings.GetStreamAsync("archive:latest");
if (value is not null)
{
    await value.CopyToAsync(destination, cancellationToken);
}
```

Respire uses a bounded 64 KiB pipe between the socket reader and your stream. If you read slowly,
the socket reader pauses when that pipe fills. The connection uses one ordered response reader, so
later command replies on that connection wait until the streamed value is consumed or disposed.
Dispose the stream when you stop early; Respire drains the remaining bulk frame before reading the
next reply. Use another client connection for long-running reads that must not delay ordinary
commands. Streaming reads bypass the client-side value cache. The cancellation token passed to
`GetStreamAsync` applies while waiting for the reply header; pass a token to stream read/copy calls
to cancel payload consumption.

## Expiry without sentinels

Redis represents missing keys and persistent keys with negative TTL values. Respire returns `RespireTtl` instead:

```csharp
RespireTtl expiry = await redis.Keys.ExpiryAsync("session:42");

if (!expiry.Exists) { /* missing */ }
else if (!expiry.HasExpiry) { /* persistent */ }
else Console.WriteLine(expiry.TimeToLive);
```
