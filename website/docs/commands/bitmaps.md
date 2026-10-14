---
title: Bitmaps and bit fields
description: Read and combine bitmap values and store packed integer fields.
---

# Bitmaps and bit fields

`redis.Bitmaps` is part of the core `Respire` package. Redis stores bitmaps in string values;
bit offsets start at zero. Large offsets can grow the stored string substantially.

```csharp
await using var redis = await RespireClient.ConnectAsync("redis://localhost:6379");
bool previous = await redis.Bitmaps.SetAsync("{activity}:today", 42, true);
bool active = await redis.Bitmaps.GetAsync("{activity}:today", 42);
long count = await redis.Bitmaps.SetBitCountAsync("{activity}:today");
long? firstActive = await redis.Bitmaps.PositionAsync("{activity}:today", true);
```

`SetAsync` returns the previous bit. `PositionAsync` returns `null` when Redis finds no match.
Range overloads of `SetBitCountAsync` and `PositionAsync` use inclusive byte offsets by default;
choose `BitIndexUnit.Bit` for bit offsets (Redis 7+).

## Combine bitmaps

```csharp
await using var redis = await RespireClient.ConnectAsync("redis://localhost:6379");
long bytes = await redis.Bitmaps.OperateAsync(
    BitOperation.Or, "{activity}:week", "{activity}:today", "{activity}:yesterday");
```

`OperateAsync` returns the destination's byte length, not its set-bit count. `And`, `Or`,
`Xor`, and `Not` map to `BITOP`; `Not` takes exactly one source. Redis 8.2+ also supports
`Diff`, `Diff1`, `AndOr`, and `One`. All source and destination keys must share a Cluster
slot after prefixing. Member bit offsets are unaffected by key prefixes.

## Packed integer fields

`FieldAsync` sends `BITFIELD` (Redis 3.2+). `FieldReadOnlyAsync` sends `BITFIELD_RO`
(Redis 6+) and accepts only `Get` operations.

```csharp
await using var redis = await RespireClient.ConnectAsync("redis://localhost:6379");
var encoding = BitFieldEncoding.Unsigned(8);
var offset = BitFieldOffset.Fields(0);
long?[] results = await redis.Bitmaps.FieldAsync("counters",
    BitFieldOperation.SetOverflow(BitFieldOverflow.Fail),
    BitFieldOperation.Increment(encoding, offset, 1),
    BitFieldOperation.Get(encoding, offset));
```

Use `Signed(1..64)` or `Unsigned(1..63)` for the encoding. `Bits(n)` is an absolute bit
offset; `Fields(n)` multiplies the index by the encoding width. `Set` returns the old
value, `Increment` the new value, and `Get` the current value. `SetOverflow` changes
subsequent writes and has no result element. `Fail` returns `null` on overflow and leaves
that field unchanged; `Wrap` and `Saturate` select the other overflow behaviors.

The `batch.Bitmaps` and `transaction.Bitmaps` facets queue these operations without the
`Async` suffix. See [batches and transactions](../guides/batches-and-transactions.md).
