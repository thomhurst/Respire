# Durability acknowledgements

Redis `WAIT` confirms replication of earlier writes on the same connection; `WAITAOF` also
confirms AOF fsync. A separate client call cannot do this for pooled writes, so use a batch
method that sends the writes and the acknowledgement together:

```csharp
using var batch = redis.CreateBatch();
var first = batch.Set("order:{42}", "accepted");
var second = batch.Hashes.Set("audit:{42}", "state", "accepted");
long acknowledgedReplicas = await batch.ExecuteAndWaitForReplicationAsync(
    replicas: 2, timeout: TimeSpan.FromSeconds(1));
bool written = first.Result;
bool fieldCreated = second.Result;
bool replicationTargetMet = acknowledgedReplicas >= 2;
```

`ExecuteAndWaitForReplicationAsync` uses `WAIT` (Redis 3.0+) and returns the number of
acknowledging replicas. On a server timeout it returns that count, even below the requested level;
it does not throw or roll back. Check the count yourself: pending results report command success
only.

For AOF acknowledgements (Redis 7.2+):

```csharp
using var batch = redis.CreateBatch();
var write = batch.Set("payment:{42}", "recorded");
RespireAofAcknowledgementResult acknowledgement = await batch.ExecuteAndWaitForAofAsync(
    requireLocal: true, replicas: 1, timeout: TimeSpan.FromSeconds(2));
bool persistenceTargetMet = acknowledgement.Local >= 1 && acknowledgement.Replicas >= 1;
```

- `Local` is the local fsync count (0 or 1); `Replicas` is the number of replicas that acknowledged fsync.
- `requireLocal: false` sends `numlocal = 0`; the server may still report a local acknowledgement.
- `requireLocal: true` needs AOF enabled on the primary. Replica acknowledgements need matching replica persistence configuration.
- Unsupported versions and AOF configurations return their server errors. There is no fallback from `WAITAOF` to `WAIT`.
- The server can reject the acknowledgement after the writes succeeded.

## Connection and execution contract

1. Each execution opens a fresh exclusive connection and pipelines all queued commands on it.
2. If every command succeeds, it sends `WAIT` or `WAITAOF` on that same connection.
3. It always closes the connection afterwards, so every execution includes connection setup.

Other client operations continue on multiplexed connections meanwhile.

- The batch is single-shot and non-atomic. It supports the usual typed facets and key prefixes.
- It acknowledges only this batch's writes, not earlier client calls.
- The execution methods are on the concrete `RespireBatch`; helpers can still take `IRespireCommandQueue` to queue commands.
- An empty batch is rejected and stays unsent: add commands and execute it again.
- The client-side cache is invalidated before writes and again after execution, as with ordinary batches.
- Borrowed input buffers must not change until execution completes.
- Dispose the batch after use. Disposing an unsent batch faults its pendings; completed results stay readable.

### Failures

- If a command fails, all pendings finish, the earliest failure in queue order is rethrown, and no acknowledgement is sent. Other commands may already have succeeded.
- If the acknowledgement fails, successful write results stay readable.
- The client never repeats writes after a disconnect, cancellation or Cluster redirect.

After an exception, inspect each pending's `Status` and `Error`. If every status is
`RespirePendingStatus.Succeeded`, all writes succeeded and execution failed later (during the
acknowledgement or cancellation): results are readable but durability is unconfirmed.

:::warning
A failure after sending can leave the write outcome uncertain. A failed pending does not prove its
write was never applied. Do not replay writes just because the acknowledgement failed.
:::

## Timeouts and cancellation

The timeout is the server's acknowledgement wait, not a deadline for the whole batch.

- Zero waits indefinitely; use a cancellation token to bound it.
- Negative timeouts and replica counts are rejected before sending.
- Positive durations round **up** to whole milliseconds, so a sub-millisecond timeout never becomes an infinite wait.
- Connection acquisition and each command keep their own timeouts. `RespireOptions.CommandTimeout` and `ConnectionIdleReadTimeout` do not end `WAIT` or `WAITAOF` early.
- The cancellation token covers acquisition, writes and acknowledgement. Cancelling closes the connection but does not undo writes or prove they did not run.
- Disposing the client aborts outstanding waits.

## Cluster and transaction restrictions

In Cluster mode, every queued command needs a routing key, and all keys must share a slot after
prefixing. Keyless commands and cross-slot batches are rejected before connecting. Multi-key
commands keep their usual validation and `CROSSSLOT` errors.

Respire picks the slot's primary up front. Once writes start, `MOVED`, `ASK`, `READONLY` and
transport failures surface as errors; the batch is not redirected or replayed, and no
acknowledgement is sent on a replacement connection.

- The methods are on batches only, not `MULTI`/`EXEC` or watched transactions.
- `WAIT`/`WAITAOF` inside `MULTI` does not block; it returns the current counts.
- An acknowledgement batch run after a transaction does not acknowledge that transaction.
- Scripts or modules that propagate writes only to AOF, suppressing replication, are incompatible with `WAITAOF`.

:::warning
`WAIT` and `WAITAOF` improve data safety but do not make Redis strongly consistent or guarantee
that acknowledged writes survive every failover or restart. Choose counts and persistence settings
for your failure model. See the Redis [WAIT](https://redis.io/docs/latest/commands/wait/) and
[WAITAOF](https://redis.io/docs/latest/commands/waitaof/) contracts.
:::
