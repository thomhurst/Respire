# Sorting, random keys, and moving between databases

`Keys.SortAsync` sorts a list, set, or sorted set numerically by default. It returns
an array of strings, including nulls for missing external GET values. Set `Alpha`
for lexicographical ordering, `Descending` for descending order, or `Limit` for a
nonnegative offset/count slice. Omitting the limit returns all entries; count zero
returns none. Negative limits are rejected locally.

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

`ReadOnly = true` selects Redis 7.0+ `SORT_RO` and preserves read-only cache
classification. The default selects `SORT`. `SortStoreAsync(source, destination,
options)` always uses `SORT STORE`, returns the number of stored entries, and
rejects `ReadOnly = true`. The destination is replaced, or removed for an empty
result. SORT/SORT_RO server errors (including wrong types and nonnumeric members)
are preserved.

## BY, GET, and ownership

`By` specifies an external weight pattern. `Get` specifies ordered external result
patterns; repeated GET patterns yield a flat array in member/pattern order. Use
`#` to return each member itself. Redis substitutes the first `*` with the member;
`->field` accesses a hash field. A BY pattern without `*`, such as `nosort`, skips
sorting. A GET pattern without `*` returns null unless it is `#`.

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

The client prefixes source/destination keys and external BY/GET patterns. It leaves
`GET #` and patterns without `*` unchanged because they do not access external keys.
Prefixes containing `*`, `->`, or NUL are rejected when external patterns are used:
these bytes could change Redis's pattern parsing and escape the intended prefix.
Ordinary sorting, `BY nosort`, and `GET #` still work with those prefixes. These
rules keep key lookup inside the view; returned members and external values are
ordinary data and are never stripped or rewritten.

Use `SortAsync<byte[]>` for owned binary results. Generic overloads use the client's
serializer, and their returned values survive reply disposal. A missing GET yields
`default(T)`; choose a nullable value type, such as `int?`, to distinguish missing
values from zero. String/byte-array results preserve null directly.

Cluster STORE requires source and destination to share a slot after prefixing.
External Cluster patterns require a fixed, nonempty hash tag before the wildcard,
matching the source slot. For example, `{jobs}:weight:*` works with `{jobs}:ids`.
Patterns whose member substitution could change their slot are rejected before
sending or enqueueing. Redis 7.4+ is required for external Cluster BY/GET patterns;
older servers' errors are preserved. Redis ACL rules may also require full key-read
permissions for external lookups.

`RespireSortOptions` record equality compares `Get` by its memory backing store and
slice, not by pattern contents. Separately allocated equal pattern arrays therefore
produce unequal options. A `with` copy shares that memory; enqueueing snapshots its
patterns as described below.

## RANDOMKEY and MOVE

`Keys.RandomAsync()` returns an owned binary `RespireKey?`, or null when the selected
database is empty. It requires an unprefixed, non-Cluster client. RANDOMKEY cannot
filter a namespace or sample a whole cluster uniformly, so those views are rejected
before execution. Use `ScanAsync` when you need keys within a prefixed/Cluster view.

`Keys.MoveAsync(key, database)` moves the key, with its value and expiry, to another
database on the same standalone server. It keeps the prefixed key name and returns
false when the source is missing or the destination exists. Negative database
numbers are rejected locally; same-database, out-of-range, and server-specific
errors are preserved. Cluster clients reject MOVE, including compatible servers
that might offer separate multiple-database extensions. MOVE does not switch the
client's selected database. It is not a cross-server transfer.

Batches and transactions expose `Sort`, `Sort<T>`, `SortStore`, `Random`, and `Move`
with the same validation and owned results. Queued options and binary patterns are
snapshotted at enqueue time. External adapters implementing `IKeyCommands` or
`IBatchKeyCommands` must implement these new members.

References: Redis [SORT](https://redis.io/docs/latest/commands/sort/),
[SORT_RO](https://redis.io/docs/latest/commands/sort_ro/),
[RANDOMKEY](https://redis.io/docs/latest/commands/randomkey/), and
[MOVE](https://redis.io/docs/latest/commands/move/).

Slot mismatches detected before sending use `RespireServerException` with code `CROSSSLOT`,
matching the other multi-key facets. Such failures are local validation, not Redis replies;
`CommandName` identifies `SORT` or `SORT_RO`. Unsupported server versions still surface the
server's error rather than triggering an implicit command downgrade.
