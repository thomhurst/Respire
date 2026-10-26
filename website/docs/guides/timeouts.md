---
title: Timeouts and thread-pool starvation
description: Read RespireTimeoutException snapshots and distinguish connection, server, payload, and scheduling delays.
---

# Timeouts and thread-pool starvation

A `RespireTimeoutException` means a client wait exceeded its budget. It does not by
itself prove that Redis is slow, the network is broken, or the thread pool is starved.
Start with `CommandName`, `Timeout`, `Endpoint`, and `Diagnostics.Stage`, then correlate
the snapshot with server latency and application scheduling at the same time.

## What the I/O path does

Respire's [native APIs are async-only](async-only.md). Awaiting a command releases the
calling thread while the socket is busy. Commands share multiplexed connections and
are automatically coalesced by a persistent flush loop. The receive loop parses replies
in wire order and delivers response completions in batches. The delivery runner can run
on the receive loop, and by default it runs fast application continuations inline to
avoid a separate work item for every reply. Slow continuations move to the thread pool;
see [`ContinuationDispatch`](../fundamentals/connections.md#command-continuations).

This reduces scheduling work; it does **not** remove the dependency on the .NET thread
pool. An inline continuation's CPU work or blocking wait can delay replies behind it.
Respire has stalled-delivery recovery controlled by `StalledDeliveryThreshold` (20 ms
by default), but that is a fallback, not permission to block. Socket
progress, completion delivery, cancellation, and timeout notification can still be
delayed when the process cannot schedule work. The reported timeout budget is not a
guarantee that the caller regains control at exactly that instant.

Use `await` throughout the calling path. `.Result`, `.Wait()`, and
`.GetAwaiter().GetResult()` can starve the pool even on an async-only client; the
included **RESP009** analyzer flags these patterns. Wrapping blocking I/O in `Task.Run`
still occupies a worker. `ConfigureAwait(false)` does not make blocking safe.

### Comparing with StackExchange.Redis v3

This guide compares against **StackExchange.Redis 3.4.5**. Its default continuations
use the global thread pool; the v2 dedicated `mgr` pool and `SocketManager` are no
longer the v3 model. Its [timeout documentation][ser-timeouts] still discusses raising
minimum thread counts, but also contains historical v1/v2 advice: do not treat those
sections as a description of v3 internals.

Since [3.1.31][ser-release], StackExchange.Redis also offers opt-in `DedicatedThreads`
and the **SER307** sync-over-async analyzer. Its [threading guide][ser-async] explains
the dedicated reader/writer threads and their limitations. Respire's async-only API
and completion batching are different choices, not a claim that v3 has no starvation
mitigations or that Respire is immune to starvation.

## Read the timeout stage

`Diagnostics` is an immutable, best-effort snapshot. Counters are sampled concurrently,
not atomically. `null` (rendered as `unknown` in the message) means unavailable, not zero.
Stages describe the inspected physical command, not proof of what Redis executed.

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
earlier attempts. See [command retries](command-retries.md) and
[transport write evidence](transport-write-evidence.md).

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

These fields come from the [3.4.5 exception formatter][ser-exceptions]. The mappings
are diagnostic equivalents, not identical counters or a message parsing contract.

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
| Historical v2 `mgr` availability | No equivalent; do not apply the v2 dedicated-pool interpretation to StackExchange.Redis v3 or Respire. |

## Choose timeout settings after diagnosis

`RespireOptions.CommandTimeout` defaults to ten seconds; `null` disables that cap.
`ConnectTimeout` separately bounds connection setup. Intentionally blocking calls
such as `BLPOP` use their own blocking semantics rather than the ordinary command
cap. Caller cancellation normally surfaces as `OperationCanceledException` and does
not undo a command already submitted.

Raise a command budget only when the observed latency is acceptable for the application.
It does not repair starvation or drain a backed-up queue. For detecting a stalled
connection, review the separate [receive and write liveness bounds](../fundamentals/connections.md#independent-liveness-bounds):
those can abort a whole connection, affecting other commands. Borrowed large-write
memory must remain unchanged until public completion, even after a deadline.

Replica function propagation has its own five-second retry ceiling, or a shorter
`CommandTimeout`. Its timeout message describes that ceiling; increasing or disabling
`CommandTimeout` cannot extend it. Check library replication and replica health as
described in [scripting](scripting.md).

[ser-timeouts]: https://github.com/StackExchange/StackExchange.Redis/blob/3.4.5/docs/Timeouts.md
[ser-release]: https://github.com/StackExchange/StackExchange.Redis/releases/tag/3.1.31
[ser-async]: https://github.com/StackExchange/StackExchange.Redis/blob/3.4.5/docs/SyncOverAsync.md
[ser-exceptions]: https://github.com/StackExchange/StackExchange.Redis/blob/3.4.5/src/StackExchange.Redis/ExceptionFactory.cs
