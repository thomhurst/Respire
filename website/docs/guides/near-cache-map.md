---
title: Near-cache maps
---

# Near-cache maps

`RespireMap<TKey, TValue>` stores a typed map in one Redis hash. Reads reuse Respire's
[client-side cache](../fundamentals/client-side-caching.md); optional callbacks load missing
entries and persist changes to another store. It is included in the core `Respire` package.

```csharp
using System.Globalization;
using System.Text;
using Respire;

await using var client = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = { new("localhost", 6379) },
    ClientSideCache = new(),
});

await using var scores = new RespireMap<int, int>(
    client, "game:scores",
    key => key.ToString(CultureInfo.InvariantCulture),
    value => Encoding.UTF8.GetBytes(value.ToString(CultureInfo.InvariantCulture)),
    bytes => int.Parse(Encoding.UTF8.GetString(bytes.Span), CultureInfo.InvariantCulture));

await scores.SetAsync(42, 100);
var score = await scores.GetAsync(42);
if (score.Found)
    Console.WriteLine(score.Value);

await scores.RemoveAsync(42);
```

The key formatter must be stable and give each distinct key a distinct field name. Supply
byte codecs for values; source-generated JSON codecs can support records and Native AOT.
Codecs must return independent values on decode, and keys must remain immutable while in use.
Stored `default(TValue)` is distinct from an absent field through `RespireGet<TValue>.Found`.

## Loading and persistence

Pass `RespireMapOptions<TKey, TValue>` as the last constructor argument. This factory
adapts an application persistence service:

<!-- doc-test-declaration -->
```csharp
public interface IScoreStore
{
    ValueTask<int?> ReadScoreAsync(int key, CancellationToken cancellationToken);
    ValueTask UpsertScoreAsync(int key, int value, CancellationToken cancellationToken);
    ValueTask DeleteScoreAsync(int key, CancellationToken cancellationToken);
}

static RespireMapOptions<int, int> CreateOptions(IScoreStore database) => new()
{
    Loader = async (key, ct) =>
    {
        int? score = await database.ReadScoreAsync(key, ct);
        return score is { } value ? new(true, value) : default;
    },
    Writer = async (changes, ct) =>
    {
        foreach (var change in changes)
        {
            if (change.Entry.Found)
                await database.UpsertScoreAsync(change.Key, change.Entry.Value, ct);
            else
                await database.DeleteScoreAsync(change.Key, ct);
        }
    },
};
```

`database` represents your application's persistence service. A loader runs only when Redis
lacks the field. Its result is installed with `HSETNX` (or `HSETEX FNX` with expiry), so a
concurrent writer wins. Loader fills do not call the writer. Concurrent misses within one map
are serialized; separate map instances can invoke their loaders independently.

Write-through is the default: `SetAsync` and `RemoveAsync` update Redis and then await the
writer. Deletions reach the writer even if Redis already lacks the field. A writer failure
propagates **after the Redis mutation**; Redis and an external database are not a transaction.
Retry or reconcile failed operations in the application. A timeout or cancellation during a
Redis command can have an unknown outcome, as with other Redis writes.

Callbacks must not call back into the same map, since operations and callbacks are serialized
per instance. Writers must treat batch contents as immutable. Changes made directly through
Redis, automatic expiry, and loader fills do not invoke the writer.

This serialization includes near-cache hits: a slow loader, writer (including background
flushes), or preload blocks all reads on that map, even for unrelated cached keys. Read
throughput is bounded by the map gate. Both writer modes receive a decoded snapshot of the
encoded value so caller mutations during Redis I/O cannot change what the external store
receives.

## Batched write-behind and shutdown

Set `WriteBehind = true` with a `Writer` to return after Redis accepts a mutation and its
writer change is queued. `BatchSize` defaults to 100, `MaxPendingWrites` to 1,000, and
`FlushInterval` to one second. The capacity bounds the number of changes, not payload bytes.
At capacity, the next mutation must successfully flush a batch before it changes Redis.

Batches preserve the order of mutations within the map, including repeated writes and deletes
of the same entry. They are not coalesced. Background writer failures retain the failed batch
for the next interval. Inspect `LastWriterError` or call `FlushAsync` to observe a failure.
Writers must be idempotent: a partially applied batch may be delivered again. Ordering across
different map instances is not guaranteed; use external version checks when that matters.
Before invoking a loader on a Redis miss, the map drains all of its pending writer batches.
This prevents a read after deletion or expiry from reloading an external value that predates
this map's queued changes. The read (including keyed preload) waits for persistence; if a
writer fails, the read throws without calling the loader and retains the failed batch for
retry. Cache hits and reads without a loader do not trigger a flush. Other map instances'
queues are independent: use external version checks or tombstones when coordinating deferred
writes across instances.

**Dispose the map before its client.** `DisposeAsync` rejects new operations, waits for accepted
operations and the active writer call, then drains all queued batches, including a final short
batch. Shutdown does not cancel accepted writer calls. Give the application enough shutdown
time, and bound external I/O in the writer itself. If draining fails, disposal throws and
retains the failed batch: fix the writer and retry `DisposeAsync` (or `FlushAsync` first).
The map stays closed to new operations. Repeated successful disposal is harmless.

The queue lives in memory. Clean shutdown drains it; a process crash or forced termination can
lose pending external writes. Redis data is written immediately, subject to the server's own
persistence settings.

## Preload, invalidation, and expiry

- `PreloadAsync(keys)` reads known keys and uses the loader on misses; it returns the number
  found. `PreloadAsync()` scans existing Redis fields and warms their `HGET` cache entries
  without callbacks. The latter uses `HSCAN NOVALUES` and requires Redis 7.4+.
  Both overloads read sequentially and hold the map gate for the whole preload. Each uncached
  field needs its own Redis round trip, in addition to scan pages for the scanning overload.
  Other map operations wait until preload completes; disposal waits for the accepted preload.
- `ClearLocalCachesAsync()` invalidates the hash's local views across connected tracking
  clients, including clients on different application nodes. A single-key Lua script adds
  and removes a collision-free temporary field atomically. It preserves map values and their
  expiry metadata. It requires script execution and hash write permissions, and can appear
  in keyspace notifications. It does not call the writer.
- Set `EntryExpiration` in the options, or pass an `expiration` to `SetAsync`, for per-entry
  TTLs. Both use atomic [HSETEX](https://redis.io/docs/latest/commands/hsetex/) and require Redis
  8+. Lifetimes must be at least one millisecond. An ordinary write without expiry removes
  the field's previous TTL. An expired entry can be loaded again on the next read.

Cache capacity, local expiration, tracking mode, and reconnect behavior come from the client's
`ClientSideCache` options. Its prefix filters must cover the map's physical key, including any
`WithKeyPrefix` prefix. A cache-bypassing client view also bypasses the map's near cache. Map
reads use the primary read policy. A hash change invalidates all cached fields of that hash.
Remote invalidations arrive asynchronously; this is the existing client-side cache consistency
contract, not a synchronous cross-process barrier. Preload obeys the cache's capacity and is
not an atomic snapshot, so warming a map larger than the cache cannot keep every entry resident.
