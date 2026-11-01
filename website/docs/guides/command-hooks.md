---
title: Logical command hooks
description: Observe, time and reject logical commands, including scripts, cursor pages, blocking replies, streaming results and subscriptions.
---

# Logical command hooks

Set `RespireOptions.CommandHook` to observe, time or reject the commands your application
runs. The default is `null`, which adds no overhead. Client views created with a key prefix,
read preference or cache bypass share the client's hook.

A hook sees **logical operations**: one admission and one completion per public API call,
however many wire commands, retries or redirects that call needs internally.

<!-- doc-test-tail-declaration: split-before=sealed class CommandAudit -->
```csharp
var options = new RespireOptions
{
    Endpoints = [new("localhost", 6379)],
    CommandHook = new CommandAudit(),
};
await using var client = await RespireClient.ConnectAsync(options);
var value = await client.Strings.GetStringAsync("key");

sealed class CommandAudit : RespireCommandHook
{
    public override object? OnExecuting(RespireCommandContext context)
    {
        // Applies only to the supported dispatch paths described below.
        if (context.Command == "SET")
            throw new InvalidOperationException("Writes are disabled for this operation.");
        return System.Diagnostics.Stopwatch.GetTimestamp();
    }

    public override void OnCompleted(RespireCommandContext context, object? state,
        RespireCommandOutcome outcome, Exception? error)
    {
        var elapsed = state is long start
            ? System.Diagnostics.Stopwatch.GetElapsedTime(start)
            : TimeSpan.Zero;
        Console.WriteLine($"{context.Command}: {outcome} in {elapsed.TotalMilliseconds:F1} ms");
    }
}
```

## Callbacks and outcomes

**`OnExecuting`** runs synchronously after argument validation and before the operation
sends anything or reads the client-side cache. Return any object to carry per-operation
state (such as a start timestamp) to the completion callback.

- **Observe or time**: return state and inspect it in `OnCompleted`.
- **Reject**: throw. Nothing is sent, the caller receives your exception, and `OnCompleted` runs once with `Rejected` and null state. This applies even if you throw an `OperationCanceledException`.

**`OnCompleted`** runs once, before the caller's task completes, with the same context and
state and one of these outcomes:

| Outcome | Meaning |
| --- | --- |
| `Succeeded` | The operation returned its result. |
| `Failed` | The operation threw, including reply conversion errors and validation performed inside a composite operation. |
| `Canceled` | The operation threw `OperationCanceledException`. |
| `Rejected` | `OnExecuting` threw. |

Completion includes the API's own reply conversion and resource cleanup. A conversion you
perform later on a raw `RespireResult` (for example `result.AsDouble()`) is outside it.
Exceptions thrown from `OnCompleted` are ignored and never change the caller's result.

Things to know:

- Internal retries, redirects, hedge attempts, `NOSCRIPT` fallback and library reloads stay inside one operation and produce no extra callbacks. An external retry is a new operation.
- With a hook configured, most operations still admit a call whose token is already cancelled: a rejecting hook wins and reports `Rejected`; otherwise completion reports `Canceled`. No command is sent either way.
- Fire-and-forget calls complete after the write; success does not mean the server executed the command.
- APIs that return per-node errors as data (server fan-out, `ProbeHealthAsync`) report `Succeeded` when they return their result array, even if some entries failed.
- For results transferred to you (leases, streams, subscriptions, import sessions, lock handles), completion happens at the transfer. It does not span the resource's lifetime.
- Hooks are independent of tracing and metrics.

## Rejection is not authorization

Hook names describe logical operations, not every wire command. `GETORSET`, `LOCK ACQUIRE`
and `RATE LIMIT ACQUIRE` write through internal `SET` or script legs, and an `EVAL` or `FCALL`
can run arbitrary commands on the server, so denying `SET` alone does not deny every write.
The context carries the command name only, never keys or values. Use Redis ACLs for
server-enforced access control.

Rejection also does not undo earlier commands, and housekeeping never asks the hook:
connection authentication, database selection, tracking setup, topology and capability
discovery, sampled health PINGs, background lock renewal, and cleanup owed after a started
operation. Use [connection hooks](./connection-hooks) to control connection establishment.

## Concurrency and reentrancy

- Callbacks can run concurrently on one hook instance. Keep per-operation data in the state returned by `OnExecuting` and synchronize shared state.
- Keep callbacks short. They run outside transport locks, but a slow callback delays its caller.
- A callback can start another command; that command gets its own callbacks. There is no automatic suppression, so guard against unbounded recursion and do not block on work that depends on the calling operation.
- The context is immutable and safe to keep. Completion exceptions can contain server or application data, so choose what you log.

## What hooks cover

Hooks apply to a concrete `RespireClient` and its views. Each entry below is one logical
operation.

### Commands and Execute APIs

- Typed facet methods (`GetStringAsync`, `GetAsync<T>`, `SetAsync`, `Strings.LengthAsync` and so on), including cache hits, replica reads and bulk writes such as `MSET`, `MSETNX`, `MSETEX`, `HSET`, `HSETEX`, `ARMSET` and `RESTORE`. A rejected GET returns nothing, even when the value is cached.
- `ExecuteAsync`, `ExecuteLeaseAsync` and `ExecuteFireAndForgetAsync`, for catalog descriptors, raw command names and interpolated commands. The name is normalized and arguments are never included. With a hook, interpolated arguments are snapshotted before admission, so the hook cannot change what is sent. `ExecuteLeaseAsync` completes when it hands you the lease.
- Server operations: pinned `CLIENT`/`HOTKEYS` commands, `CLIENT ID`, `OnNode` calls and server fan-out. Cluster `DBSIZE`, `FLUSHDB`/`FLUSHALL` and `SCRIPT LOAD`/`EXISTS`/`FLUSH` are one operation across all nodes.

### Scripts and functions

- `Scripts.Execute*` uses one `EVALSHA` (or `EVALSHA_RO`) operation, including `EVAL` fallback, retries and result conversion. The name stays `EVALSHA` even when fallback sends `EVAL`.
- `Functions.Execute*` uses one `FCALL` or `FCALL_RO`, including retries and missing-library recovery.
- `Functions.LoadAsync`, `DeleteAsync`, `FlushAsync` and `RestoreAsync` report `FUNCTION LOAD`, `DELETE`, `FLUSH` and `RESTORE`, covering every Cluster node.

### Cursor pages, blocking reads and streams

- `Keys.ScanAsync`, `SSCAN`/`HSCAN`/`ZSCAN` enumerations and array scans (`ARSCAN`) admit each page separately, before its items are yielded. Rejecting a later page stops further sends; earlier pages stay delivered.
- `ScanClusterPageAsync` is one `SCAN` per page (`CLUSTERSCAN` for `ScanValkeyClusterPageAsync`). Asking a completed cursor for another page is still admitted, though nothing is sent.
- Blocking list, sorted-set and stream commands complete after the reply and dedicated-connection cleanup.
- `Streams.ReadAllAsync` is one `XREAD` per fetched page, including resolution of `$` start positions, recovery and redirects. The whole page is validated before any entry is yielded.
- `Streams.AddAsync` is one `XADD`, including automatic idempotency, retries and IDMP fallback on older servers.
- `Streams.CreateGroupAsync` (`XGROUP CREATE`) reports `Succeeded` with `false` when the group already exists.

### Streaming values and subscriptions

- `Strings.SetAsync` with a `Stream` or `ReadOnlySequence<byte>` is one `SET`, admitted before the source is read. You still own and dispose the source.
- Large value `SET` calls complete only after the socket has released your buffer, so the completion callback may reuse it.
- `Strings.GetStreamAsync` is one `GET` that completes when it returns the stream. Reading the stream does not invoke the hook; dispose it to release its connection.
- `SubscribeAsync` reports `SUBSCRIBE`, `PSUBSCRIBE` or `SSUBSCRIBE` and completes once the subscription is active. `RespireSubscription.DisposeAsync` reports the matching unsubscribe; rejecting it leaves the subscription live so you can retry. Reconnects, resubscription and message delivery do not invoke the hook.

### Cache operations

- `Strings.GetManyAsync` is one `MGET` and `Hashes.GetManyAsync` one `HMGET`, covering cached and fetched values together. `Hashes.GetAllAsync<T>` is one `HGETALL`.
- `GetOrSetAsync<T>` is one `GETORSET`, covering lookup, your factory, serialization and publication, with or without `RespireCacheAsideOptions`. Concurrent callers sharing one load each get their own callbacks; cancelling one leaves the load running for the others.
- `RespireDistributedCache` reports `GET`, `SET`, `REFRESH` and `UNLINK`, for both sync and async methods.

### Imports, bulk writes and health probes

- `Hashes.CreateImportSessionAsync` is `HIMPORT OPEN`, completing when it hands you the session; rejecting it opens no connection. Session `PrepareAsync`, `SetAsync`, `DiscardAsync` and `DiscardAllAsync` report `HIMPORT PREPARE`, `SET`, `DISCARD` and `DISCARDALL`; a rejection keeps existing fieldsets usable. Session disposal always runs and is not hooked.
- Bulk writes use the ordinary command names listed above.
- `ProbeHealthAsync` is one `PROBE HEALTH`. It reports `Succeeded` when it returns results, even if every node failed; check each result's `Error`. Its internal PINGs are not hooked, but your own PING calls are.

### Batches, transactions and WATCH

Batch and transaction members are admitted when the unit executes, not when queued:

- If a `PipelineHook` is set, it runs first, then `CommandHook.OnExecuting` for each member in queue order (`WAIT`/`WAITAOF` last in a durability batch). All admissions finish before anything is sent.
- If a member is rejected, nothing is sent and every member fails with that exception. The rejected member reports `Rejected`, earlier members report `Failed` (or `Canceled`), and later members get no callbacks.
- Members complete in queue order with their own outcome, and the pipeline hook completes last. A failed `WAIT` does not turn successful writes into failures. Members of an aborted watched transaction report `Failed` with `RespireTransactionAbortedException`.
- `MULTI`, `EXEC` and internal retries are not hooked. An empty unit has no member callbacks.
- Creating a watched transaction with keys reports one `WATCH`, admitted before a dedicated connection is acquired. Each attempt of the watched-transaction retry helper is a new `WATCH` plus member execution.

### Locks and coordination

Coordination APIs report logical names, not the Redis commands they use. Rejecting `SET` or
`EVALSHA` does not reject a lock operation; use these names instead.

| Public operation | Hook name |
| --- | --- |
| `Locks.AcquireAsync`, `AcquireOrThrowAsync`, `TryTakeAsync` (including polling and `keepAlive`) | `LOCK ACQUIRE` |
| Token-based or managed lock release, renewal, owner lookup/verification | `LOCK RELEASE`, `LOCK RENEW`, `LOCK VERIFY` |
| Managed lock disposal; explicit keep-alive start/stop | `LOCK DISPOSE`; `LOCK KEEPALIVE START`, `LOCK KEEPALIVE STOP` |
| Semaphore acquisition and lease verification, renewal, release, disposal | `SEMAPHORE ACQUIRE`, `SEMAPHORE VERIFY`, `SEMAPHORE RENEW`, `SEMAPHORE RELEASE`, `SEMAPHORE DISPOSE` |
| Immediate fenced lock acquisition; notification-assisted waiting | `LOCK ACQUIRE FENCED`; `LOCK WAIT FENCED` |
| Read/write lock acquisition | `LOCK ACQUIRE READ`, `LOCK ACQUIRE WRITE` |
| Read/write lock verification, renewal, release, disposal | `LOCK VERIFY READWRITE`, `LOCK RENEW READWRITE`, `LOCK RELEASE READWRITE`, `LOCK DISPOSE READWRITE` |
| Hash-field lease immediate acquisition; notification-assisted waiting | `COORDINATION LEASE ACQUIRE`; `COORDINATION LEASE WAIT` |
| Hash-field lease verification, renewal, release, disposal | `COORDINATION LEASE VERIFY`, `COORDINATION LEASE RENEW`, `COORDINATION LEASE RELEASE`, `COORDINATION LEASE DISPOSE` |
| Countdown latch creation, joining and reset | `LATCH CREATE`, `LATCH JOIN`, `LATCH RESET` |
| Countdown latch signal and wait | `LATCH COUNTDOWN`, `LATCH WAIT` |
| Rate limiter `AcquireAsync`, including composite limiters | `RATE LIMIT ACQUIRE` |
| Rate limiter `UpdateAsync`, including local adoption | `RATE LIMIT UPDATE` |
| Public deny-list membership read and write | `RATE LIMIT DENY LIST READ`, `RATE LIMIT DENY LIST WRITE` |

- Fenced lock handles use the managed `LOCK` names for later operations.
- An unsuccessful acquisition attempt reports `Succeeded` (it returned a result); a failed `AcquireOrThrowAsync` reports `Failed`.
- Rejecting release or disposal leaves the handle usable for a later attempt. Rejecting `LOCK KEEPALIVE STOP` leaves renewal running.
- Background renewal is not hooked, so rejecting `LOCK RENEW` does not stop keep-alive. Control it through `LOCK ACQUIRE` or `LOCK KEEPALIVE START`.
- `LATCH WAIT` is one operation covering polling, its notification subscription and teardown.
- Completion covers foreground cleanup; it does not wait for background recovery that the operation handed off.

### Multi-client Redlock

`RespireRedlockGroup.TryAcquireAsync`, lease `ResetExpiryAsync`, `ReleaseAsync` and
`DisposeAsync` report `REDLOCK ACQUIRE`, `REDLOCK RENEW`, `REDLOCK RELEASE` and
`REDLOCK DISPOSE`.

- Every participating client's hook admits the whole group operation, in the order the clients were supplied, before any node command is sent. A shared hook instance is called once per client. Clients without a hook are skipped.
- If one hook rejects, nothing is sent: that hook reports `Rejected`, earlier hooks report `Failed` (or `Canceled`), and later hooks get no callbacks. A rejected renewal, release or disposal keeps the lease, so you can retry.
- Hooks complete in client order after the group operation finishes. They all see the group's outcome: missing quorum is a successful unacquired result, not a hook failure.
- Internal per-node legs and cleanup are not re-admitted, so cleanup cannot be vetoed once it starts. See the [Redlock guide](./coordination.md#multi-node-redlock) for timing behavior.
- Group admission is guaranteed only for clients exposing Respire's native lock facet (including prefixed views). A custom lock-facet decorator keeps its own per-node behavior.

### Typed modules

Typed RedisJSON, Search, TimeSeries and Probabilistic operations are admitted before
serialization and sending, and complete after typed parsing. A malformed reply or invalid
JSON (for example a JSONPath matching several values in `GetAsync<T>`) reports `Failed`.

| Module | Hook names |
| --- | --- |
| JSON | `JSON.GET` (`GetAsync<T>`, `GetManyAsync<T>`, `GetJsonAsync`), `JSON.MGET`, `JSON.SET`, `JSON.MSET`, `JSON.MERGE`, `JSON.DEL`, `JSON.FORGET`, `JSON.DEBUG MEMORY` |
| Search | `FT.SEARCH` (including vector search), `FT.AGGREGATE`, `FT.HYBRID`, `FT.PROFILE`, `FT.INFO`, `FT.EXPLAIN`, `FT.EXPLAINCLI`, `FT._LIST`, `FT.ALIASLIST`, `FT.ALIASADD`, `FT.ALIASDEL`, `FT.ALIASUPDATE`, `FT.CREATE`, `FT.ALTER`, `FT.DROPINDEX`, `FT.CONFIG GET`, `FT.CONFIG SET`, `FT.CURSOR READ`, `FT.CURSOR DEL`, `FT.DICTADD`, `FT.DICTDEL`, `FT.DICTDUMP`, `FT.SPELLCHECK`, `FT.SUGADD`, `FT.SUGDEL`, `FT.SUGGET`, `FT.SUGLEN`, `FT.SYNUPDATE`, `FT.SYNDUMP`, `FT.TAGVALS` |
| TimeSeries | `TS.CREATE`, `TS.ALTER`, `TS.ADD`, `TS.MADD`, `TS.INCRBY`, `TS.DECRBY`, `TS.GET`, `TS.MGET`, `TS.RANGE`, `TS.REVRANGE`, `TS.MRANGE`, `TS.MREVRANGE`, `TS.DEL`, `TS.CREATERULE`, `TS.DELETERULE`, `TS.INFO`, `TS.QUERYINDEX`, `TS.NRANGE`, `TS.NREVRANGE`, `TS.QUERYLABELS`, `TS.READ` |
| Bloom | `BF.RESERVE`, `BF.ADD`, `BF.EXISTS`, `BF.MADD`, `BF.MEXISTS`, `BF.INSERT`, `BF.CARD`, `BF.SCANDUMP`, `BF.LOADCHUNK` |
| Cuckoo | `CF.RESERVE`, `CF.ADD`, `CF.ADDNX`, `CF.INSERT`, `CF.INSERTNX`, `CF.DEL`, `CF.EXISTS`, `CF.MEXISTS`, `CF.COUNT`, `CF.SCANDUMP`, `CF.LOADCHUNK` |
| Count-Min | `CMS.INITBYDIM`, `CMS.INITBYPROB`, `CMS.INCRBY`, `CMS.QUERY`, `CMS.MERGE` |
| Top-K | `TOPK.RESERVE`, `TOPK.ADD`, `TOPK.INCRBY`, `TOPK.QUERY`, `TOPK.COUNT` |
| t-digest | `TDIGEST.CREATE`, `TDIGEST.RESET`, `TDIGEST.MERGE`, `TDIGEST.ADD`, `TDIGEST.MIN`, `TDIGEST.MAX`, `TDIGEST.QUANTILE`, `TDIGEST.CDF`, `TDIGEST.RANK`, `TDIGEST.REVRANK`, `TDIGEST.BYRANK`, `TDIGEST.BYREVRANK`, `TDIGEST.TRIMMED_MEAN` |

Module notes:

- The JSON client's `Commands` property and the Probabilistic `*InfoAsync` and `TopKListAsync` methods return raw results; completion happens before you convert them.
- `FT.HYBRID` includes its `COMMAND INFO` capability probe. `AggregatePagesAsync` admits the first `FT.AGGREGATE` and each `FT.CURSOR READ` separately; cleanup after early exit is not hooked.
- `TimeSeries.MultiAddAsync` is one `TS.MADD` across all chunks. Chunks are still not atomic. `FollowAsync` admits each `TS.READ` separately. Label-name and label-value queries both report `TS.QUERYLABELS`.

## Decorators and custom clients

Respire never unwraps an `IRespireClient` decorator or looks for its hook. Coverage depends
on what the wrapper calls:

| Client or wrapper | Behavior |
| --- | --- |
| `RespireClient` and its prefix, read-preference and cache-bypass views | Share the hook; covered as described above. |
| Decorator returning a native facet unchanged (for example `inner.Strings`) | The native facet keeps its coverage. |
| Decorator forwarding `ExecuteAsync` | The inner raw command is hooked. A typed parser outside that call can still fail after the hook reported success. |
| Custom facet or adapter composing public calls | Each forwarded native call is hooked separately; there is no single outer operation. |

If a wrapper needs a single policy over its whole operation, it must enforce that itself.
