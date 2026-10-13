---
title: Endpoint circuit breakers
description: Opt-in endpoint admission, bounded recovery probes, and command health outcomes.
---

# Endpoint circuit breakers

Set `RespireOptions.CircuitBreaker` to stop new standalone, Sentinel or Redis Cluster commands from
reaching an unhealthy endpoint. The default is `null`, which adds no circuit state or per-command
work.

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

Each endpoint keeps its own history of completed outcomes, limited by both `SamplingWindow` and
`MaximumSampleCount`. When the history holds at least `MinimumFailureCount` failures and the
failure fraction reaches `FailureRateThreshold`, the circuit opens. After `OpenDuration`, it admits
at most `HalfOpenProbeCount` concurrent probe commands. That many successful probes close the
circuit; one failed probe opens it again for another `OpenDuration`. A probe that ends without a
health outcome, such as a cancelled command, frees its slot for another command. Timing uses a
monotonic clock.

## Rejections

Respire checks the circuit immediately before it sends a command. An open circuit throws
`RespireCircuitOpenException` without sending anything. The exception's `Endpoint` is the
rejected data endpoint, and `RetryAfter` is the remaining open delay. `RetryAfter` is null when all
recovery probe slots are taken; the next admission then depends on those probes completing.
`RetryAfter` is a snapshot, not permission to resend: every later command is checked again.

Circuit state is per endpoint, shared by every command connection to it and by key-prefixed and
cache-bypass views of the client. Client-side cache hits are not checked. Socket setup, reconnect
handshakes and explicit health checks are not checked either.

Circuit breaking never removes commands that were already sent, cancels an accepted write,
retries a rejected command or replays an ambiguous write. A command already queued on the
connection keeps its place when another command opens the circuit.

Cancelling a command never counts as an endpoint failure, even after it was queued; an accepted
write may still execute on Redis. A timeout while waiting for connection capacity, a rejected
connection generation and a command writer that fails before sending also leave no health
outcome. All of these free a half-open probe slot.

For a streamed GET, the command stays admitted until its reply frame finishes. Reading to the end
records success. Cancelling or disposing the stream before the end leaves no health outcome and
frees the slot immediately. Blocking commands keep their exemption from the response timeout, so
pass a cancellation token to bound the wait. Streamed uploads keep their usual command deadline.

During a maintenance handoff, a command that the old connection has not accepted moves to the
replacement endpoint and must be admitted there under its original deadline. An open replacement
circuit rejects it without writing. The client keeps at most 16 idle endpoint histories beyond
those used by current routing or outstanding commands. Returning to an evicted endpoint starts a
fresh history. DNS changes reuse the configured hostname's history.

## Health classification

These count as endpoint failures:

- `RespireConnectionException` (except authentication failures), `RespireProtocolException` and
  `RespireTimeoutException` from a sent command, including socket and TLS errors.
- A connection-selection failure because no healthy connection exists, even though no command was
  sent. While the circuit is open, later selection failures are rejected; a selection failure on
  a half-open probe starts a new open interval.

These do not count as failures:

- Authentication, configuration and command validation errors, serializer and source exceptions,
  and application cancellation.
- Ordinary Redis error replies, such as `WRONGTYPE`, ACL denials and missing scripting-engine
  replies. A reply arrived, so they count as healthy outcomes. The exception you receive is
  unchanged.

Standalone fire-and-forget sends are checked against the circuit, but a successful send records
no outcome because its reply is discarded. Fire-and-forget sends alone therefore cannot close a
half-open circuit. A fire-and-forget send that observes a transport failure still records it.
Fire-and-forget commands that wait for a reply because of client-side caching record that reply.

## Batches and transactions

Queuing commands needs no admission, and an empty batch or transaction sends nothing. Each batch
command is checked when execution reaches it on the selected connection. `TryExecuteAsync` returns
failures in queue order; `ExecuteAsync` throws the first failure after every pending completes.

A batch can be partly sent: admitted commands can execute while later entries are rejected,
including when all half-open slots are taken. Rejected entries are not retried. Accepted replies
keep their order. Circuit admission does not make a pipeline atomic. A standalone batch stays on
its selected connection during a maintenance handoff. Cluster batches keep their usual recovery
for commands rejected before they were accepted; each new target needs admission. After a
Sentinel primary change, an ordinary batch entry that was never accepted can move to the current
primary and be admitted there.

A nonempty transaction is checked once, for the whole `MULTI`/`EXEC` sequence, immediately before
it is sent. A rejection sends neither `MULTI` nor its commands nor `EXEC`, and faults every pending
result with `RespireCircuitOpenException`. A transaction can move to a maintenance replacement only
before it is accepted, and needs fresh admission there. Watched transactions and hash import
transactions stay on their original connection. `WATCH` is checked as its own command; a rejected
commit releases the connection held for `WATCH`. A `WATCH` abort or an ordinary Redis error still
counts as a healthy reply.

Hash import batches check their entries in connection order. A rejection before sending keeps the
import session and its prepared fieldsets. [Durability batches](durability-acknowledgements.md)
check each write and the following `WAIT` or `WAITAOF` separately. A failed write prevents the
acknowledgement; a rejected acknowledgement cannot undo writes that already completed.

## Redis Cluster

Set `UseCluster = true` alongside `CircuitBreaker` to check Cluster data nodes. Each node, primary
or replica, has its own history. An open node does not reject commands routed to healthy nodes,
and a rejection does not select another replica or bypass the configured `ReadFrom` policy.

Every kind of send is checked on its selected node: typed, converted, raw, fire-and-forget, batch
and transaction. `MOVED` and `ASK` recovery checks the target node before sending the command or
`ASKING`. A redirect reply counts as a healthy response from the source node; a target rejection
reports the target's `Endpoint` and `RetryAfter`. Accepted commands are never replayed because
another node opens. Pinned node-local reads, such as `SCAN` and `CLUSTERSCAN`, are checked without
changing their destination. Blocking commands and streamed uploads are checked on every attempt,
including redirects.

A disconnected node is checked before the client reconnects to it. Socket, TLS and connection
setup failures count toward that node's history, while caller cancellation does not. Background
topology refresh is not checked. Cluster fire-and-forget calls that wait for replies to handle
routing record those reply outcomes.

Live primary, replica, seed and redirect endpoints keep their histories, even in clusters with
more than 16 nodes. New sockets to the same configured endpoint share its history. A result
converter that throws, even with a Redis-shaped exception, cannot change endpoint health or cause
a replay.

## Sentinel

In Sentinel mode, circuit state belongs to the actual data endpoint, including discovered replicas
when read routing selects them. Sentinel monitor connections, discovery and primary `ROLE`
validation are not checked, so they can discover a healthy replacement while the old primary's
circuit is open. The replacement has its own state. A removed replica keeps its history until
reads in flight finish; after that, its idle history can be evicted.

When a primary generation is retired before a command is accepted, ordinary immediate sends,
batch entries and unwatched transactions can move to the current primary and be admitted there.
Accepted commands are never replayed. Scan cursor pages, `WATCH` state, import sessions and
durability acknowledgements stay on their connection; they cannot move to another primary to
bypass a rejection.

## Circuit metrics

The `Respire` meter reports data-endpoint circuit activity when
`RespireMetricGroups.Resiliency` is selected and a listener enables the instrument:

| Counter | Unit | Counting |
| --- | --- | --- |
| `respire.circuit.transitions` | `{transition}` | One measurement per state change, with `respire.circuit.state.from` and `respire.circuit.state.to` set to `closed`, `open`, or `half_open` |
| `respire.circuit.rejections` | `{operation}` | One measurement per rejected admission attempt, while open or while all half-open probe slots are taken |

Both counters identify the data endpoint with `server.address` and `server.port`
(omitted for the default port, 6379), plus `db.system.name=redis` and
`redis.client.library=Respire:<version>`. They contain no command keys, payloads,
exception messages, or credentials. Creating a circuit in the closed state is not a
transition, and evicting an idle history emits nothing.

A batch counts each rejected entry. A rejected transaction counts once, regardless of its
command count. `WATCH` is checked separately and can add its own rejection. Cancellation and
validation before the check are not rejections. A successful admission can still report a
transition, such as `open` to `half_open` when recovery starts. The generic `redis.client.errors`
counter can also report the exception the caller receives.

The [failover group](failover-groups.md) health-probe circuit reports only its
`respire.failover.*` metrics. A failover member's application commands emit these counters when
that member's own `CircuitBreaker` is set. See
[observability](../integrations/observability.md#endpoint-circuit-metrics) for subscription details.

## Retries

`RetryPolicy` and `CircuitBreaker` work together. A circuit rejection is never retried, even
though the rejected command was not sent: the endpoint is unhealthy. Every retry attempt is
checked again immediately before it is sent.

- **Same endpoint.** When an attempt's failure opens its endpoint's circuit, the retry is rejected
  before it waits for a replacement connection or sends anything. The same applies when every
  half-open probe slot is taken, including by the failed attempt's own probe; that rejection has a
  null `RetryAfter`. Immediate commands, batches and transactions all stop there. A batch faults
  every pending with the rejection, and a transaction sends neither `MULTI` nor `EXEC`.
- **Another endpoint.** A retry that reaches a different endpoint must be admitted there.
  A Sentinel retry after a connection loss discovers the replacement primary, whose circuit is
  independent of the failed one. A failover-group retry runs on the newly active member and is
  checked by that member's circuit. A Cluster `MOVED` or `ASK` target, including one reached
  during a retry, is checked before the command or `ASKING` is sent; an open target rejects with
  its own `Endpoint`.
- **Cluster slot owners.** When a disconnected slot owner's circuit is open and other primaries
  are known, the retry asks them for the slot's current owner instead of contacting the open
  node. If the slot moved, the new owner admits the retry. If the refresh still names the open
  owner, or finds no owner, the retry is rejected without connecting to it. A lone owner is
  rejected immediately.

A circuit rejection always ends the operation. How the rejection affects the
`respire.command.retries` counter is described under
[failures and dispatch scope](command-retries.md#failures-and-dispatch-scope).

## Limits

Configuration with standalone `ReplicaEndpoints` is rejected. The circuit breaker does not change
`RespireFailoverGroup` probe or failback behavior.
