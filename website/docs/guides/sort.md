---
title: Sorting, copying, and moving keys
description: Sort lists, sets, and sorted sets with SORT, sample a random key, and copy or move keys between databases.
---

# Sorting, copying, and moving keys

The `Keys` facet sorts a list, set, or sorted set on the server with `SORT` and `SORT_RO`, optionally ordering or projecting through other keys. It also returns a random key and copies or moves keys between databases on one server.

## Sort a collection

```csharp
await using var client = await RespireClient.ConnectAsync("localhost:6379");
await client.Lists.RightPushAsync("scores", "10", "2", "1");
var sorted = await client.Keys.SortAsync("scores", new RespireSortOptions
{
    Descending = true,
    Limit = new RespireSortLimit(0, 2),
    ReadOnly = true,
});
```

`SortAsync` returns `string?[]`, sorted numerically and ascending by default. `RespireSortOptions`:

| Option | Effect |
| --- | --- |
| `Alpha` | Compare lexicographically instead of numerically. |
| `Descending` | Sort in descending order. |
| `Limit` | Return a slice. See below. |
| `By` | Sort by an external weight pattern. |
| `Get` | Return values from external keys instead of, or with, the members. |
| `ReadOnly` | Send `SORT_RO` (Redis 7.0 or later) instead of `SORT`. |

`Limit = new RespireSortLimit(offset, count)`:

- A negative offset throws `ArgumentOutOfRangeException` before sending.
- A count of zero returns nothing; a negative count returns everything from the offset on.
- Without `Limit`, all entries are returned.

`ReadOnly = true` also lets the client-side cache treat the command as a read.

### Store the result

`SortStoreAsync(source, destination, options)` sends `SORT ... STORE` and returns the number of stored entries. Redis replaces the destination with a list, or deletes it when the result is empty. `ReadOnly = true` throws `ArgumentException`, because `SORT_RO` has no `STORE`.

## BY and GET patterns

`By` names an external weight pattern. `Get` lists external result patterns in order. With several `Get` patterns, the result is a flat array in member-then-pattern order.

- `#` returns the member itself.
- Redis replaces the first `*` with the member. `->field` reads a hash field.
- A `By` pattern without `*`, such as `nosort`, skips sorting.
- A `Get` pattern without `*` returns null, unless it is `#`.
- A missing external `Get` value is `null` in the result.

```csharp
await using var client = await RespireClient.ConnectAsync("localhost:6379");
var view = client.WithKeyPrefix("tenant:");
var values = await view.Keys.SortAsync("ids", new RespireSortOptions
{
    By = "weight:*->rank",
    Get = new RespireKey[] { "#", "object:*", "object:*->name" },
    ReadOnly = true,
});
```

### Key prefixes

The client prefixes the source, destination, and every external `By` and `Get` pattern. `GET #` and patterns without `*` read no external keys and stay unchanged. Returned values are never rewritten.

A prefix containing `*`, `->`, or a NUL byte could make a pattern read keys outside the view, so combining it with an external pattern throws `NotSupportedException`. Plain sorting, `BY nosort`, and `GET #` still work.

### Typed results

`SortAsync<T>` deserializes each element with the client serializer. Use `SortAsync<byte[]>` for owned binary values. A missing `Get` value becomes `default(T)`; use a nullable type such as `int?` to tell it from zero. `string` and `byte[]` keep `null`.

### Options are snapshots

`RespireSortOptions` copies `By`, `Get`, and each binary pattern at initialization, so later edits to your arrays have no effect. Record equality compares every setting and the ordered pattern bytes.

### Cluster rules

- `SortStoreAsync` requires the source and destination to share a slot after prefixing.
- External patterns need a fixed, nonempty hash tag before the wildcard that maps to the source's slot, for example `{jobs}:weight:*` with `{jobs}:ids`. Otherwise Respire throws `NotSupportedException`.
- Slot mismatches throw `RespireCrossSlotException` before sending.
- External Cluster patterns require Redis 7.4 or later; older servers return their own error.
- Redis ACL rules may require full key-read permission for external lookups.

## Copy to another database

`CopyAsync(source, destination, destinationDatabase, replace)` sends `COPY ... DB destinationDatabase [REPLACE]` (Redis 6.2+). Omit `destinationDatabase` to copy within the current database.

```csharp
await using var client = await RespireClient.ConnectAsync("localhost:6379");
var tenant = client.WithKeyPrefix("tenant:");
var copied = await tenant.Keys.CopyAsync("source", "backup", destinationDatabase: 2, replace: true);

using var batch = tenant.CreateBatch();
var pending = batch.Keys.Copy("source", "backup", destinationDatabase: 2);
await batch.ExecuteAsync();
var copiedByBatch = pending.Result;
```

- Both names get the view's prefix. The same name can be used in both databases.
- The source is not deleted, and the client's selected database does not change.
- Returns `false` when the source is missing, or the target exists and `replace` is `false`.
- The copy stays on one server.
- A negative database number throws `ArgumentOutOfRangeException` before sending. The server enforces other limits and its errors are returned.
- [Redis Cluster supports only database 0](https://redis.io/docs/latest/commands/select/); compatible servers with several Cluster databases must accept the index. In Cluster, source and destination must share a hash slot after prefixing, or Respire throws `RespireCrossSlotException` before sending.

Transactions expose the same `Keys.Copy` overload.

## RANDOMKEY

`RandomAsync()` returns an owned `RespireKey?`, or `null` when the database is empty. `RANDOMKEY` cannot stay inside a prefix or sample a cluster evenly, so a prefixed view or Cluster client throws `NotSupportedException`; use `ScanAsync` there.

## MOVE

`MoveAsync(key, database)` moves a key, with its value and expiry, to another database on the same standalone server.

- The key keeps its prefixed name.
- Returns `false` when the source is missing or the destination already has the key.
- A negative database number throws `ArgumentOutOfRangeException`. Other errors come from the server.
- Cluster clients throw `NotSupportedException`, even on servers that offer several databases.
- The client's selected database does not change.

## Batches and transactions

`batch.Keys` and `transaction.Keys` expose `Sort`, `Sort<T>`, `SortStore`, `Random`, `Move`, and `Copy` with the same validation. Options and patterns are copied when queued.

## Custom implementations

Classes that implement `IRespireKeyCommands` or `IRespireBatchKeyCommands` must implement the sort, random, move, and database-copy members.

## Redis references

[SORT](https://redis.io/docs/latest/commands/sort/), [SORT_RO](https://redis.io/docs/latest/commands/sort_ro/), [COPY](https://redis.io/docs/latest/commands/copy/), [RANDOMKEY](https://redis.io/docs/latest/commands/randomkey/), [MOVE](https://redis.io/docs/latest/commands/move/).
