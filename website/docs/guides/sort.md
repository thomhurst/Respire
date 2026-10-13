---
title: Sorting, copying, and moving keys
description: Sort lists, sets, and sorted sets with SORT, sample a random key, and copy or move keys between databases.
---

# Sorting, copying, and moving keys

The `Keys` facet sorts collection contents on the server with `SORT` and `SORT_RO`. It also returns a random key, copies a key into another database, and moves a key between databases on one server. Use sorting when you need an ordered view of a list, set, or sorted set, optionally ordered or projected through other keys.

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

`SortAsync` returns `string?[]`. Elements are sorted numerically and in ascending order by default. Set options on `RespireSortOptions`:

| Option | Effect |
| --- | --- |
| `Alpha` | Compare lexicographically instead of numerically. |
| `Descending` | Sort in descending order. |
| `Limit` | Return a slice. See below. |
| `By` | Sort by an external weight pattern. |
| `Get` | Return values from external keys instead of, or with, the members. |
| `ReadOnly` | Send `SORT_RO` (Redis 7.0 or later) instead of `SORT`. |

`Limit = new RespireSortLimit(offset, count)` takes a nonnegative offset; a negative offset throws `ArgumentOutOfRangeException` before sending. Without `Limit`, all entries are returned. A count of zero returns none. A negative count returns every entry from the offset onward.

`ReadOnly = true` also keeps the command's read-only classification for the client-side cache.

### Store the result

`SortStoreAsync(source, destination, options)` sends `SORT ... STORE` and returns the number of stored entries. The destination becomes a list. Redis replaces it, or deletes it when the result is empty. `SortStoreAsync` throws `ArgumentException` when `ReadOnly = true`, because `SORT_RO` has no `STORE`.

Server errors from `SORT` and `SORT_RO`, such as wrong types or nonnumeric members, are returned as Redis reports them.

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

The client prefixes the source and destination keys and every external `By` and `Get` pattern. It leaves `GET #` and patterns without `*` unchanged, because they do not read external keys. Returned members and external values are data and are never rewritten.

A prefix that contains `*`, `->`, or a NUL byte could change how Redis parses a pattern and read keys outside the view. Respire throws `NotSupportedException` when such a prefix is combined with an external pattern. Plain sorting, `BY nosort`, and `GET #` still work with that prefix.

### Typed results

`SortAsync<T>` deserializes each element with the client serializer. Results remain valid after the reply is released. Use `SortAsync<byte[]>` for owned binary values. A missing `Get` value becomes `default(T)`, so choose a nullable type such as `int?` to tell a missing value from zero. `string` and `byte[]` results keep `null` directly.

### Options are snapshots

`RespireSortOptions` copies `By`, the `Get` collection, and each binary pattern at initialization. Later edits to the arrays you supplied do not change the options. Record equality compares every setting and the ordered pattern bytes, so equal patterns from separate arrays compare equal and hash equally. A `with` copy shares the snapshots unless its initializer replaces them.

### Cluster rules

- `SortStoreAsync` requires the source and destination to share a slot after prefixing.
- External patterns need a fixed, nonempty hash tag before the wildcard, and that tag must map to the source's slot. For example, `{jobs}:weight:*` works with `{jobs}:ids`. Respire throws `NotSupportedException` for a pattern whose slot could change with the member.
- Slot mismatches throw `RespireServerException` before sending. Its `Code` is `CROSSSLOT` and its `CommandName` is `SORT` or `SORT_RO`. This is local validation, not a Redis reply.
- External Cluster patterns require Redis 7.4 or later. Older servers return their own error; Respire does not downgrade the command.
- Redis ACL rules may require full key-read permission for external lookups.

## Copy to another database

`CopyAsync(source, destination, destinationDatabase, replace)` sends `COPY source destination DB destinationDatabase [REPLACE]` and requires Redis 6.2 or later. The overload without `destinationDatabase` copies within the current database.

```csharp
await using var client = await RespireClient.ConnectAsync("localhost:6379");
var tenant = client.WithKeyPrefix("tenant:");
var copied = await tenant.Keys.CopyAsync("source", "backup", destinationDatabase: 2, replace: true);

using var batch = tenant.CreateBatch();
var pending = batch.Keys.Copy("source", "backup", destinationDatabase: 2);
await batch.ExecuteAsync();
var copiedByBatch = pending.Result;
```

- Both key names receive the view's prefix. The same name can be used in both databases.
- The source stays in the client's selected database. Copying does not change that database or delete the source.
- It returns `false` when the source is missing, or when the target exists and `replace` is `false`.
- The copy stays on one server. It does not transfer between servers.
- A negative database number throws `ArgumentOutOfRangeException` before sending. Other restrictions are enforced by the server, and its errors are returned rather than copying into the current database.
- [Redis Cluster supports only database 0](https://redis.io/docs/latest/commands/select/). A compatible server with several Cluster databases must accept the requested index. In Cluster, source and destination must share a hash slot after prefixing, even across databases. Respire rejects a mismatch locally with a `RespireServerException` whose `Code` is `CROSSSLOT` and `CommandName` is `COPY`.

Transactions expose the same `Keys.Copy` overload.

## RANDOMKEY

`RandomAsync()` returns an owned binary `RespireKey?`, or `null` when the selected database is empty. `RANDOMKEY` samples the whole database. It cannot stay inside a prefix or sample a cluster evenly, so a prefixed view or a Cluster client throws `NotSupportedException`. Use `ScanAsync` to find keys inside a prefixed or Cluster view.

## MOVE

`MoveAsync(key, database)` moves a key, with its value and expiry, to another database on the same standalone server.

- The key keeps its prefixed name.
- It returns `false` when the source is missing or the destination already has the key.
- A negative database number throws `ArgumentOutOfRangeException`. Same-database, out-of-range, and other server errors are returned unchanged.
- Cluster clients throw `NotSupportedException`, even on compatible servers that offer several databases.
- `MOVE` does not change the client's selected database. It is not a cross-server transfer.

## Batches and transactions

`batch.Keys` and `transaction.Keys` expose `Sort`, `Sort<T>`, `SortStore`, `Random`, `Move`, and `Copy` with the same validation and owned results. Options and binary patterns are copied when the command is queued.

## Custom implementations

Classes that implement `IKeyCommands` or `IBatchKeyCommands` must implement the sort, random, move, and database-copy members.

## Redis references

[SORT](https://redis.io/docs/latest/commands/sort/), [SORT_RO](https://redis.io/docs/latest/commands/sort_ro/), [COPY](https://redis.io/docs/latest/commands/copy/), [RANDOMKEY](https://redis.io/docs/latest/commands/randomkey/), [MOVE](https://redis.io/docs/latest/commands/move/).
