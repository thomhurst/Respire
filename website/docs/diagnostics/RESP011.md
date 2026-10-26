---
title: RESP011 — Bound collection reads
description: Scan or page Redis collections instead of materializing unbounded replies.
---

# RESP011: bound collection reads

The bundled analyzer warns about typed calls that can return an entire keyspace
or collection. Large replies consume memory and keep Redis busy while it builds
them.

| Flagged operation | Alternative for large collections |
| --- | --- |
| `RespireServerNode.KeysAsync` (`KEYS`) | `client.Keys.ScanAsync` (`SCAN`) |
| `Sets.MembersAsync` (`SMEMBERS`) | `client.Sets.ScanAsync` (`SSCAN`) |
| `Hashes.GetAllAsync` (`HGETALL`) | `client.Hashes.ScanAsync` (`HSCAN`) |
| `Lists.RangeAsync` / queued `Range` with `start: 0, stop: -1` | Bounded `LRANGE` pages |
| `SortedSets.RangeAsync` / `RangeWithScoresAsync` (or queued equivalents) with `start: 0, stop: -1` | `client.SortedSets.ScanAsync` (`ZSCAN`) or bounded rank ranges |

Generic overloads and the interface facets are covered. List and sorted-set rank
bounds include omitted defaults, named arguments and compile-time constants.
Sorted-set ranges are checked in either ordering. Nonconstant bounds, score and
lexicographic ranges, raw commands, scripts and custom wrappers are not analyzed.

Queued `Sets.Members` and `Hashes.GetAll` calls in batches and transactions are
not flagged. A complete cursor loop cannot replace a single queued command,
and moving the read outside a transaction changes its atomicity. Queued list
and sorted-set ranges still warn because explicit bounds are available on those
commands. Batching does not bound replies: keep queued sets and hashes small by
construction, or redesign the operation when they can grow without a limit.

```csharp
async Task ReadAsync(IRespireClient client, Func<string, Task> processAsync)
{
    // RESP011
    var members = await client.Sets.MembersAsync("large-set");

    // Process incrementally instead.
    await foreach (var member in client.Sets.ScanAsync("large-set"))
    {
        await processAsync(member);
    }

    // Lists have no scan command: choose a bounded range.
    var page = await client.Lists.RangeAsync("list", start: 0, stop: 99);
}
```

See [collections](../commands/collections.md) for the typed APIs. Scanning is not
a snapshot: concurrent changes can affect iteration, duplicate entries are
possible, and a scan count is a hint. Sorted-set scanning does not preserve rank
order; use bounded rank ranges when ordering matters. List index and sorted-set
rank paging also need an explicit policy for concurrent mutations.
No automatic code fix is offered because
changing a materialized result into iteration changes the caller's control flow
and consistency assumptions. In particular, a scan outside a transaction does
not preserve the transaction's atomicity.

For collections with a known small bound, keeping the original call is reasonable.
Suppress locally with `#pragma warning disable RESP011` / `restore RESP011`, or
configure the warning in `.editorconfig`:

```ini
dotnet_diagnostic.RESP011.severity = suggestion
```
