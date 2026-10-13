---
title: Endpoint circuit breakers
description: Opt-in endpoint admission, bounded recovery probes, and command health outcomes.
---

# Endpoint circuit breakers

Set `RespireOptions.CircuitBreaker` to stop new standalone, Sentinel or Redis Cluster commands from
reaching an unhealthy endpoint. The default is `null`, which adds no per-command work.

```csharp
await using var redis = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = { new RespireEndpoint("localhost", 6379) },
    CircuitBreaker = new RespireCircuitBreakerOptions
    {
        FailureRateThreshold = 0.5,
        MinimumFailureCount = 5,
        SamplingWindow = TimeSpan.FromSeconds(30),
        MaximumSampleCount = 1024,
        OpenDuration = TimeSpan.FromSeconds(5),
        HalfOpenProbeCount = 2,
    },
});
```

| Setting | Default | Meaning |
| --- | --- | --- |
| `FailureRateThreshold` | `0.5` | Failure fraction that opens the circuit, greater than 0 and at most 1. |
| `MinimumFailureCount` | `5` | Failures required before the circuit can open. At most `MaximumSampleCount`. |
| `SamplingWindow` | 30 seconds | Maximum age of the outcomes that count. |
| `MaximumSampleCount` | `1024` | Most recent outcomes kept, from 1 through 65536. |
| `OpenDuration` | 5 seconds | Delay before an open circuit admits recovery probes. |
| `HalfOpenProbeCount` | `1` | Concurrent recovery probes, and the successes needed to close. From 1 through 65536. |

## How a circuit opens and recovers

1. Each endpoint keeps its own outcome history, limited by `SamplingWindow` and `MaximumSampleCount`.
2. The circuit opens when the history holds at least `MinimumFailureCount` failures and the failure fraction reaches `FailureRateThreshold`.
3. After `OpenDuration`, it admits up to `HalfOpenProbeCount` concurrent probe commands.
4. That many successful probes close the circuit. One failed probe reopens it for another `OpenDuration`.

A probe that ends without a health outcome, such as a cancelled command, frees its slot.

## Rejections

Respire checks the circuit right before sending. An open circuit throws
`RespireCircuitOpenException` without sending anything.

- `Endpoint` is the rejected data endpoint.
- `RetryAfter` is the remaining open delay, or null when all probe slots are taken. It is a snapshot, not permission to resend.

Circuit state is per endpoint, shared by every connection to it and by key-prefixed and
cache-bypass views of the client. Not checked: client-side cache hits, socket setup, reconnect
handshakes and explicit health checks.

The circuit breaker never removes sent commands, cancels an accepted write, retries a rejected
command or replays an ambiguous write. A command already queued keeps its place when the circuit
opens.

No health outcome is recorded (and a half-open probe slot is freed) for:

- Cancellation, even after queuing. An accepted write may still execute on Redis.
- A timeout waiting for connection capacity.
- A command writer that fails before sending.
- A streamed GET cancelled or disposed before its reply ends. Reading to the end records success.

Blocking commands are exempt from the response timeout, so pass a cancellation token to bound
them. Streamed uploads keep their usual command deadline.

During a maintenance handoff, a command not yet accepted by the old connection moves to the
replacement endpoint and must be admitted there under its original deadline. The client keeps at
most 16 idle endpoint histories beyond those in use; returning to an evicted endpoint starts a
fresh history. DNS changes reuse the configured hostname's history.

## Health classification

| Outcome | Counts as |
| --- | --- |
| `RespireConnectionException` (except authentication), `RespireProtocolException`, `RespireTimeoutException` from a sent command, including socket and TLS errors | Failure |
| No healthy connection to select, even though nothing was sent | Failure |
| Ordinary Redis error replies (`WRONGTYPE`, ACL denials, missing scripting engine) | Success: a reply arrived. Your exception is unchanged. |
| Authentication, configuration and validation errors, serializer and source exceptions, cancellation | Not recorded |

While the circuit is open, later selection failures are rejected; a selection failure on a
half-open probe starts a new open interval.

Standalone fire-and-forget sends are checked, but a successful send records nothing because its
reply is discarded, so fire-and-forget alone cannot close a half-open circuit. Transport failures
are still recorded, and fire-and-forget commands that wait for a reply (for client-side caching)
record it.

## Batches and transactions

Queuing needs no admission, and an empty batch or transaction sends nothing.

**Batches**: each command is checked when execution reaches it.

- `TryExecuteAsync` returns failures in queue order; `ExecuteAsync` throws the first failure after every pending completes.
- A batch can be partly sent: admitted commands can execute while later ones are rejected. Rejected entries are not retried. The circuit does not make a pipeline atomic.
- A standalone batch stays on its connection during a maintenance handoff. Cluster and Sentinel entries not yet accepted can move to a new target and need admission there.

**Transactions**: a nonempty transaction is checked once, right before sending `MULTI`/`EXEC`.

- A rejection sends nothing and faults every pending result with `RespireCircuitOpenException`.
- A transaction can move to a maintenance replacement only before acceptance, with fresh admission.
- Watched and hash import transactions stay on their connection. `WATCH` is checked as its own command; a rejected commit releases the `WATCH` connection. A `WATCH` abort or Redis error counts as a healthy reply.

**Hash imports** check entries in connection order; a rejection before sending keeps the session
and its prepared fieldsets.

**[Durability batches](durability-acknowledgements.md)** check each write and its `WAIT` or
`WAITAOF` separately. A failed write prevents the acknowledgement; a rejected acknowledgement
cannot undo completed writes.

## Redis Cluster

Set `UseCluster = true` alongside `CircuitBreaker`. Each node, primary or replica, has its own
history.

- An open node does not affect commands routed to healthy nodes. A rejection does not pick another replica or bypass `ReadFrom`.
- Every send kind is checked on its node: typed, converted, raw, fire-and-forget, batch and transaction.
- `MOVED` and `ASK` check the target before sending the command or `ASKING`. The redirect counts as healthy for the source; a target rejection reports the target's `Endpoint`.
- Pinned node-local reads (`SCAN`, `CLUSTERSCAN`) are checked without changing destination.
- Blocking commands and streamed uploads are checked on every attempt, including redirects.
- A disconnected node is checked before reconnecting. Socket, TLS and setup failures count; caller cancellation does not. Background topology refresh is not checked.
- Live endpoints keep their histories regardless of cluster size.
- A throwing result converter cannot change endpoint health or cause a replay.

## Sentinel

Circuit state belongs to the data endpoint, including discovered replicas used for reads.
Sentinel monitors, discovery and `ROLE` validation are not checked, so they can find a healthy
replacement while the old primary's circuit is open. The replacement has its own state.

Before acceptance, immediate sends, batch entries and unwatched transactions can move to a new
primary and be admitted there. Scan cursor pages, `WATCH` state, import sessions and durability
acknowledgements stay on their connection.

## Circuit metrics

The `Respire` meter reports circuit activity when `RespireMetricGroups.Resiliency` is selected and
a listener enables the instrument:

| Counter | Unit | Counting |
| --- | --- | --- |
| `respire.circuit.transitions` | `{transition}` | One measurement per state change, with `respire.circuit.state.from` and `respire.circuit.state.to` set to `closed`, `open`, or `half_open` |
| `respire.circuit.rejections` | `{operation}` | One measurement per rejected admission attempt, while open or while all half-open probe slots are taken |

- Tags: `server.address`, `server.port` (omitted for 6379), `db.system.name=redis`, `redis.client.library=Respire:<version>`. No keys, payloads, exception messages or credentials.
- Creating a closed circuit is not a transition; evicting an idle history emits nothing.
- A batch counts each rejected entry; a transaction counts once. `WATCH` can add its own rejection.
- `redis.client.errors` can also report the exception the caller receives.

The [failover group](failover-groups.md) health-probe circuit reports only `respire.failover.*`
metrics. A member's commands emit these counters when that member sets its own `CircuitBreaker`.
See [observability](../integrations/observability.md#endpoint-circuit-metrics).

## Retries

A circuit rejection is never retried, even though nothing was sent. Every retry attempt is
checked again right before sending.

- **Same endpoint.** If a failure opens the circuit, or every half-open slot is taken (`RetryAfter` null), the retry is rejected before sending. A batch faults every pending; a transaction sends neither `MULTI` nor `EXEC`.
- **Another endpoint.** The retry must be admitted there: a Sentinel replacement primary, a failover group's new active member, or a Cluster `MOVED`/`ASK` target.
- **Cluster slot owners.** If a disconnected owner's circuit is open and other primaries are known, the retry asks them for the current owner. A moved slot's new owner admits the retry. If the owner is unchanged or unknown, or it is the only owner, the retry is rejected without connecting.

For how rejections affect `respire.command.retries`, see
[failures and dispatch scope](command-retries.md#failures-and-dispatch-scope).

## Limits

Configuration with standalone `ReplicaEndpoints` is rejected. The circuit breaker does not change
`RespireFailoverGroup` probe or failback behavior.
