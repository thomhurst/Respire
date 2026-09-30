# Durability acknowledgements

Redis `WAIT` acknowledges earlier writes from the same physical connection. `WAITAOF`
adds acknowledgement of AOF fsync. Sending either command through a separate ordinary
client call cannot establish that relationship with earlier pooled writes.

Use a batch execution method that keeps its writes and acknowledgement together:

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

`ExecuteAndWaitForReplicationAsync` uses Redis 3.0+ `WAIT`. It returns the actual number
of acknowledging replicas. A server timeout returns that count even when it is below
the requested level; it does not throw a client timeout exception or roll back writes.
Check the count explicitly. Successful pending results describe command success,
independently of whether the requested replication level was reached.

For Redis 7.2+ AOF acknowledgements:

```csharp
using var batch = redis.CreateBatch();
var write = batch.Set("payment:{42}", "recorded");
RespireAofAcknowledgement acknowledgement = await batch.ExecuteAndWaitForAofAsync(
    requireLocal: true, replicas: 1, timeout: TimeSpan.FromSeconds(2));
bool persistenceTargetMet = acknowledgement.Local >= 1 && acknowledgement.Replicas >= 1;
```

`Local` is the actual local fsync count (0 or 1); `Replicas` is the number of replicas
that acknowledged fsync. `requireLocal: false` sends `numlocal = 0`, disabling the
local requirement; the server may still report a local acknowledgement. A true local
requirement needs AOF enabled on the primary. Replica AOF acknowledgements also need
the corresponding server persistence configuration. Unsupported versions and AOF
configurations retain their server errors; there is no fallback from WAITAOF to WAIT.
The server can reject the acknowledgement after the batch's writes have succeeded.

## Connection and execution contract

Each execution rents one exclusive connection from the dedicated pool, pipelines all
queued commands on it, and waits for every command reply. Only when every command has
succeeded does it send WAIT or WAITAOF on that same connection. Other client operations
continue on multiplexed connections while the acknowledgement blocks. A successful
acknowledgement returns the healthy lease for reuse; a failed or cancelled execution
discards it, so a server-side wait cannot block a later borrower.

This is a single-shot, non-atomic pipeline. It supports the same typed command facets
and key prefixes as a normal batch. It acknowledges the writes queued in this batch,
not earlier calls made through the client. Queue helper code can still use
`IRespireCommandQueue`; execution remains on the concrete `RespireBatch`. No new client
or queue interface members are required. Empty batches are rejected before sending and remain
unsent: add commands to the same batch and execute it again.

If any queued command fails, all command pendings finish and the earliest failure in
queue order is rethrown. The acknowledgement is not sent. Other commands may already
have succeeded. If the acknowledgement fails, successful write pendings remain
readable. The client never repeats writes after a disconnect, cancellation, or
Cluster redirect; failure after a send may leave the write outcome uncertain.

After an execution exception, inspect each queued pending's `Status` and `Error`.
If every status is `RespirePendingStatus.Succeeded`, all command replies completed
successfully and execution failed afterward, for example during acknowledgement or
cancellation. Those results remain readable, but durability is unconfirmed. A failed
command pending does not prove its write was never applied. Do not replay writes
merely because the acknowledgement failed.

Durability execution conservatively invalidates the client-side cache before writes
and again when execution finishes, just like ordinary batches. Borrowed input buffers
must remain unchanged until execution completes. Dispose the batch after use;
disposing an unsent batch faults its pendings, while completed results remain readable.

## Timeouts and cancellation

The timeout is the server's acknowledgement wait, not a deadline for the whole batch.
Zero waits indefinitely; use a cancellation token when the caller needs a bound.
Negative values and negative replica counts are rejected before sending. Positive
durations round **up** to whole milliseconds, so a sub-millisecond timeout never
accidentally becomes an infinite server wait.

Connection acquisition and each queued command retain their existing timeouts.
`RespireOptions.CommandTimeout` and `ConnectionIdleReadTimeout` do not end WAIT or
WAITAOF early. The execution cancellation token applies to acquisition, the writes,
and the acknowledgement. Cancellation closes an outstanding lease; it does not undo
writes or prove they did not execute. Disposing the owning client aborts leased waits.

## Cluster and transaction restrictions

In Cluster mode, every queued command must have a routing key, and those routing keys
must share a slot after prefixing. Keyless commands and batches spanning routing slots
are rejected before connection acquisition and before the batch is marked sent.
Multi-key commands retain their usual key validation and Redis CROSSSLOT errors; all
keys within such a command must also share the slot. A server-rejected multi-key
command can coexist with successful neighboring commands in this non-atomic batch.

Respire selects the slot's primary before starting. Once writes start, MOVED, ASK,
READONLY, and transport failures surface without redirecting or replaying the batch.
It does not send an acknowledgement on a replacement connection. This keeps the
acknowledgement tied to the connection that actually received the writes.

These execution methods are available on batches, not MULTI/EXEC or watched
transactions. A WAIT/WAITAOF inside MULTI does not acquire normal blocking behavior;
it returns the currently available counts. Committing an ordinary transaction and
then executing an unrelated acknowledgement batch does not acknowledge that
transaction. Scripts or modules that propagate writes only to AOF and suppress
replication propagation are incompatible with WAITAOF's server contract.

WAIT and WAITAOF improve data safety but do not make Redis strongly consistent or
guarantee that acknowledged writes survive every failover or restart. Choose the
required counts and persistence configuration for the application's failure model.
See the Redis [WAIT](https://redis.io/docs/latest/commands/wait/) and
[WAITAOF](https://redis.io/docs/latest/commands/waitaof/) contracts.
