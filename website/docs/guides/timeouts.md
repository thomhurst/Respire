---
title: Timeouts and thread-pool starvation
description: Read RespireTimeoutException snapshots and distinguish connection, server, payload, and scheduling delays.
---

# Timeouts and thread-pool starvation

A `RespireTimeoutException` means a client wait exceeded its budget. On its own it does
not tell you whether Redis is slow, the network is broken, or the thread pool is starved.
Start with `CommandName`, `Timeout`, `Endpoint`, and `Diagnostics.Stage`, then compare the
snapshot with server latency and application scheduling at the same moment.

## How Respire uses threads

Respire's [native APIs are async-only](async-only.md). Awaiting a command releases the
calling thread while the socket is busy. Commands share multiplexed connections and are
written in coalesced batches. Replies are parsed in wire order and, by default, fast
continuations run inline on the delivery thread while slow ones move to the thread pool;
see [`ContinuationDispatch`](../fundamentals/connections.md#command-continuations).

Respire still depends on the .NET thread pool. A continuation that does heavy CPU work or
blocks delays the replies behind it. `StalledDeliveryThreshold` (20 ms by default) hands
delivery to another thread when that happens, but it is a fallback, not permission to block.
When the process cannot schedule work, socket progress, completions, cancellation and timeout
notification are all delayed, so a caller may regain control later than the timeout budget.

Use `await` throughout the calling path. `.Result`, `.Wait()`, and
`.GetAwaiter().GetResult()` can starve the pool even on an async-only client; the
included **RESP009** analyzer flags these patterns. Wrapping blocking I/O in `Task.Run`
still occupies a worker. `ConfigureAwait(false)` does not make blocking safe.

## Read the timeout stage

`Diagnostics` is an immutable, best-effort snapshot. Counters are sampled concurrently,
not atomically. `null` (rendered as `unknown` in the message) means unavailable, not zero.
The stage describes the client's view of the command, not what Redis executed.

| `Diagnostics.Stage` | Meaning at capture | First checks |
| --- | --- | --- |
| `Unknown` | No physical command was available to inspect. Publicly constructed exceptions can have no observations. | Inspect the inner exception, operation, and surrounding logs; do not infer a connection state. |
| `Connecting` | Waiting for a connection or initialization. | Endpoint availability, DNS, TCP/TLS, authentication, reconnect logs, and `ConnectTimeout`. |
| `WaitingForCapacity` | Waiting for space in the in-flight queue; not yet enqueued on that connection. | Outstanding replies, slow commands, large responses, and producer concurrency. |
| `Buffered` | Serialized into the write buffer; no bytes of this command have been sent at capture. | Pending writes, large payloads ahead, and flush scheduling. |
| `Writing` | Some command bytes have been sent, but the whole frame has not been written. | Network throughput, socket backpressure, large uploads, and whether the server is reading. |
| `AwaitingReply` | The complete command has been written; its reply has not completed. | Server execution time, large replies ahead, network latency, and client scheduling. |

`Connecting` and `WaitingForCapacity` messages say the command was not enqueued on
the inspected connection. For buffered or submitted commands, timing out abandons the
wait; a command can still execute later. Sending bytes is not an acknowledgement from
Redis. Do not use a stage alone as permission to replay a write, especially after
earlier attempts. See [command retries](command-retries.md).

## Read the remaining fields

Prefer structured properties over parsing the exception message. Message wording and
`Diagnostics.Hint` can change; the hint suggests checks rather than declaring a cause.

| Properties | Interpretation |
| --- | --- |
| `Endpoint`, `ConnectionId`, `ServerClientId` | Physical endpoint, process-local connection identity, and Redis `CLIENT ID` when already known. `ConnectionId` is not the server ID. Capture does not query Redis. |
| `InflightCount` | Outstanding reply slots, including abandoned waits and transaction intermediates; not just actively waiting application calls. |
| `InflightBytes` | Serialized request bytes whose final replies have not been dequeued. This is not reply size. Multi-command frames retain their full count until the final reply. |
| `PendingWriteBytes` | Serialized bytes not yet accepted by the socket or TLS stream. A growing value suggests writes are falling behind. |
| `TimeSinceLastRead`, `TimeSinceLastWrite` | Time since successful connection I/O, not the age of this command. Recent reads may belong to earlier replies. |
| `IsConnected`, `IsReconnecting` | Best-effort connection/owner state when known. An open socket does not prove a healthy server. |
| `BusyWorkerThreads`, `MinWorkerThreads`, `BusyIoThreads`, `MinIoThreads` | Process-wide busy/minimum worker and I/O completion counts. The minimum is a growth threshold, not the maximum capacity. |
| `PendingWorkItems`, `PossibleThreadPoolStarvation` | Queued work and a heuristic: work is queued and busy workers have reached the minimum. This is evidence to investigate, not a diagnosis. |
| `ThreadPoolProbe` | Latest process-wide scheduling sample: check `SchedulingDelay`, `CapturedAt`, and `IsPending`. A pending sample's delay is a lower bound; an old sample may not describe this incident. |

The snapshot contains no keys, values, credentials, or command arguments. The message
does include endpoint host/port when known. For example, capture the structured fields
with your application's logger:

```csharp
try
{
    var session = await redis.GetStringAsync("session", cancellationToken);
}
catch (RespireTimeoutException error)
{
    var d = error.Diagnostics;
    logger.LogWarning(error,
        "Redis {Command} timed out at {Endpoint}: stage {Stage}, in-flight {Inflight}, pending bytes {PendingBytes}, probe delay {ProbeDelay}",
        error.CommandName, error.Endpoint, d.Stage, d.InflightCount,
        d.PendingWriteBytes, d.ThreadPoolProbe?.SchedulingDelay);
    throw;
}
```

## Diagnose the remaining causes

1. **Connection or reconnect delay.** For `Connecting` or `IsReconnecting == true`,
   correlate lifecycle logs with endpoint, DNS, TLS, authentication, and failover
   events. Reuse clients rather than creating a new connection for each command.
2. **Server latency.** For `AwaitingReply` or capacity pressure, inspect Redis
   `SLOWLOG`, latency diagnostics, CPU, and expensive scripts or commands. Use the
   [server diagnostics](server-diagnostics.md) and [command logs](command-logs.md)
   guides. A quiet slow log does not rule out network or client-side delays.
3. **Large values and head-of-line delays.** Replies share an ordered byte stream.
   A small `GET` behind a large reply must wait for those bytes. Compare payload
   sizes, command mix, network throughput, pending write bytes, and read/write ages.
   Bound batches and producer concurrency, reduce payload sizes, or isolate measured
   bulk traffic. Increasing `MaxInflightCommands` alone can deepen the queue.
4. **Blocked continuations or handlers.** Inspect stacks for synchronous waits,
   locks, long CPU work, and blocking logging or telemetry callbacks. Keep work
   following an `await` short. Pub/sub uses buffered async streams: a slow consumer
   can overflow its buffer and produce delivery gaps; see [pub/sub](pub-sub.md).
   A handler that blocks a worker can also contribute to process-wide starvation.
5. **Thread-pool scheduling.** Correlate repeated scheduling delays, pending work,
   worker counts, CPU, and GC pauses. A single `Busy > Min` observation is not proof
   of starvation. Capture runtime counters and thread stacks during the incident to
   distinguish blocked workers from CPU saturation or a transient burst.

Respire enables a shared scheduling probe by default. Its background thread samples
once per second, and `Respire.ThreadPool` warns when scheduling delay reaches
`ThreadPoolWarningThreshold` (500 ms by default). Metrics and event details are in
[thread-pool observability](../integrations/observability.md#thread-pool-scheduling).
`ThreadPoolMonitoring = false` opts this client out; other clients can still supply
the process-wide snapshot. Respire never changes thread-pool limits.

Remove blocking work first. If measurements still show worker ramp-up delays under
legitimate bursts, evaluate a minimum-thread change under representative load and
measure latency, CPU, and memory before and after. There is no universal
`ThreadPool.SetMinThreads` value to copy from a timeout message; more threads cannot
fix slow Redis commands, insufficient bandwidth, or saturated CPUs.

## Map StackExchange.Redis timeout messages

If you are migrating from StackExchange.Redis 3.x, this table maps the fields in its timeout
messages to the closest Respire diagnostics. They are rough equivalents, not identical
counters. StackExchange.Redis v3 uses the global thread pool by default (with opt-in
`DedicatedThreads`); advice in its [timeout documentation][ser-timeouts] about the v2 `mgr`
pool does not apply to v3 or to Respire.

| StackExchange.Redis field/symptom | Respire equivalent or next check |
| --- | --- |
| `Timeout performing GET ...` | `CommandName`, `Timeout`, `Diagnostics.Stage`, and `Endpoint`. |
| `qs` (sent commands awaiting responses) | Closest: `InflightCount`, but Respire also counts buffered slots, abandoned waits, and transaction intermediates. Use the stage to determine write progress. |
| `qu` (awaiting write), `bw` (backlog writer), `ws` (write state) | `Buffered`/`Writing`, `PendingWriteBytes`, and `TimeSinceLastWrite`. Respire does not expose the same backlog-writer state machine. |
| `rs` (reader state) | No direct reader-state enum. Inspect stage, `TimeSinceLastRead`, scheduling delays, and blocked application work. |
| `in` (bytes available on the socket), `in-pipe` (bytes buffered in the read pipe) | No direct inbound-byte counter in the timeout snapshot. `InflightBytes` counts requests, not unread replies. Distinguish socket availability from bytes already buffered when checking payload sizes and read progress. |
| `out-pipe` (outbound buffered bytes) | Closest: `PendingWriteBytes`; transport boundaries differ. |
| `inst` (operations since heartbeat) | No direct timeout field. Correlate command throughput using [observability](../integrations/observability.md). |
| `IOCP: Busy/Min`, `WORKER: Busy/Min` | `BusyIoThreads`/`MinIoThreads` and `BusyWorkerThreads`/`MinWorkerThreads`; message labels are `IO busy/min` and `workers busy/min`. |
| `POOL: QueuedItems` | `PendingWorkItems` (`pending work` in the message), plus `ThreadPoolProbe` to measure scheduling delay. |
| Historical v2 `mgr` availability | No equivalent. |

## Choose timeout settings after diagnosis

`RespireOptions.CommandTimeout` defaults to ten seconds; `null` disables that cap.
`ConnectTimeout` separately bounds connection setup. Intentionally blocking calls
such as `BLPOP` use their own blocking semantics rather than the ordinary command
cap. Caller cancellation normally surfaces as `OperationCanceledException` and does
not undo a command already submitted.

Raise a command budget only when the observed latency is acceptable for the application.
It does not repair starvation or drain a backed-up queue. For detecting a stalled
connection, review the separate [receive and write liveness bounds](../fundamentals/connections.md#independent-liveness-bounds):
those can abort a whole connection, affecting other commands. Keep the input of a large
binary SET unchanged until the call completes, even after a deadline.

Replica function propagation has its own five-second retry ceiling, or a shorter
`CommandTimeout`. Its timeout message describes that ceiling; increasing or disabling
`CommandTimeout` cannot extend it. Check library replication and replica health as
described in [scripting](scripting.md).

[ser-timeouts]: https://github.com/StackExchange/StackExchange.Redis/blob/3.4.5/docs/Timeouts.md
