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

A missing key returns `null`. Numeric and condition commands return `long`, `double`, or `bool` as appropriate.

## Object serialization

`SetAsync<T>` and `GetAsync<T>` use `RespireOptions.Serializer`. `SystemTextJsonSerializer` is the default. Supply an `IRespireSerializer` for another format:

```csharp
var options = new RespireOptions
{
    Endpoints = { new("localhost") },
    Serializer = new MyMessagePackSerializer(),
};
```

Typed `string` and `byte[]` values bypass object serialization. Other values passed to `SetAsync<T>`, including numeric and Boolean primitives, use the configured serializer. Pass a `RespireValue` explicitly when the value must be stored as a raw Redis scalar, as shown in [Keys and input values](#keys-and-input-values).

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

Redis represents missing keys and persistent keys with negative TTL values. Respire returns `RespireExpiry` instead:

```csharp
RespireExpiry expiry = await redis.Keys.ExpiryAsync("session:42");

if (!expiry.KeyExists) { /* missing */ }
else if (!expiry.HasExpiry) { /* persistent */ }
else Console.WriteLine(expiry.TimeToLive);
```
