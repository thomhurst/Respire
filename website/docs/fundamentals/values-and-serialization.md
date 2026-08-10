---
title: Values and serialization
description: Understand RespireKey, RespireValue, typed objects, and leased reads.
---

# Values and serialization

Respire separates flexible command inputs from convenient application outputs.

## Keys and input values

`RespireKey` accepts `string`, `byte[]`, or `ReadOnlyMemory<byte>`. `RespireValue` accepts text, binary data, numeric primitives, and booleans through implicit conversions.

```csharp
RespireKey key = "counter";
RespireValue value = 42;

await redis.SetAsync(key, value);
```

These small readonly structs keep command overloads manageable without forcing a protocol union type on every result.

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

```csharp
var options = new RespireOptions
{
    Endpoints = { new("localhost") },
    Serializer = new MyMessagePackSerializer(),
};
```

Typed `string`, `byte[]`, Boolean, and numeric primitive values bypass object serialization. Numbers use invariant Redis text. Generic Boolean writes retain the default JSON-compatible `true`/`false` representation; reads also accept Redis-style `1`/`0`. Nullable forms use the same fast path when they contain a value.

Objects, enums, and other types use the configured serializer. Custom serializers therefore do not control primitive encoding. Pass a `RespireValue` explicitly when a command input must use raw Redis scalar conventions, as shown in [Keys and input values](#keys-and-input-values).

Serializing overloads sit next to the `RespireValue` ones wherever a facet takes a single payload — `Hashes.SetAsync<T>`, `Sets.ContainsAsync<T>`, `SortedSets.AddAsync<T>`, and the `Lists.LeftPopAsync<T>` / `RightPopAsync<T>` reads. An argument already typed as `RespireValue` selects the raw overload; anything else selects the generic one. The new facet overloads preserve raw `ReadOnlyMemory<byte>`, character code units, and non-finite floating-point arguments because those previously bound to `RespireValue`. Collection member identity is also an exception to generic Boolean encoding: set lookups and sorted-set writes use Redis-native `1`/`0` so they remain compatible with the existing `RespireValue` member APIs.

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

## Expiry without sentinels

Redis represents missing keys and persistent keys with negative TTL values. Respire returns `RespireTtl` instead:

```csharp
RespireTtl expiry = await redis.Keys.ExpiryAsync("session:42");

if (!expiry.Exists) { /* missing */ }
else if (!expiry.HasExpiry) { /* persistent */ }
else Console.WriteLine(expiry.TimeToLive);
```
