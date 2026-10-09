---
title: Standalone circuit breakers
description: Opt-in endpoint admission, bounded recovery probes, and command health outcomes.
---

# Standalone circuit breakers

Set `RespireOptions.CircuitBreaker` to stop new standalone commands from entering an unhealthy
endpoint. The default is `null`: the client creates no circuit registry or per-command permit,
and retains its existing response sources and queue behavior.

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

The failure rate uses completed outcomes within both the sampling window and the bounded
sample count. Once the minimum failure count and fraction are reached, the endpoint opens.
After `OpenDuration`, it admits at most `HalfOpenProbeCount` concurrent probes. That many
successful probes close the circuit; a failed probe starts a new open interval. Ignored
probes release capacity and can be replaced immediately. Timing uses a monotonic clock.

## Admission and queue behavior

Admission occurs immediately before transport dispatch. An open circuit throws
`RespireCircuitOpenException` with the data `Endpoint` and a `RetryAfter` snapshot. A null
`RetryAfter` means recovery slots are occupied; completion determines when admission can
resume. Rejection sends no application command, including no tracking prelude for a cache miss.

Circuit state is shared by key-prefixed and cache-bypass views, across all command connections
to the same endpoint. Client-side cache hits do not require admission. Different endpoints,
including maintenance handoff destinations, keep separate histories. Socket setup, reconnect
handshakes, and explicit health-check probes retain their existing lifecycle.

A maintenance handoff can move a command only before its frame is accepted. The old admission
is released as ignored, and the replacement endpoint requires fresh admission under the
original command deadline. An open replacement circuit rejects without writing there. The
registry retains at most 16 endpoint histories unless more entries are needed by current routing
or outstanding admissions. Only idle, non-current histories can be evicted; returning to an
evicted endpoint starts fresh history. DNS changes reuse the configured hostname.

An operation already accepted by the transport remains in its FIFO position when another
operation opens the circuit. Circuit breaking does not remove queued frames, cancel accepted
writes, retry a rejected command, or replay an ambiguous write. `RetryAfter` is not permission
to resend: a later operation must acquire its own admission.

Every admission is completed on success, exception, timeout, or cancellation. Application
cancellation is ignored, including cancellation after enqueue; an accepted write may still
execute on Redis. A timeout while waiting for transport capacity, a rejected connection
generation, or a command writer failing before dispatch is ignored. These paths release
half-open capacity even when the transport must retain a response placeholder to drain a
later reply in FIFO order.

For a streamed GET, admission remains active until the wire frame finishes. Reading EOF records
success even when disposal precedes the transport's completion callback. Canceling or disposing
the returned stream before EOF releases admission as ignored immediately; the transport can
continue discarding the payload to preserve FIFO. Blocking commands retain their existing
response-timeout exemption. Supply a cancellation token to bound the blocking wait. Streamed
uploads retain their existing command deadline and source cancellation behavior.

## Health classification

Submitted command failures represented by `RespireConnectionException` (except authentication),
`RespireProtocolException`, or `RespireTimeoutException` count as endpoint failures. Socket and
TLS errors reported by the transport retain their connection classification. Authentication,
configuration, command validation, source/serializer exceptions, and application cancellation
do not count as endpoint health failures.

A connection-selection failure because no healthy connection exists also counts as endpoint
failure, even though no application frame was submitted. An open circuit rejects subsequent
selection failures, and an unavailable half-open probe starts a new recovery delay.

Ordinary Redis command errors, including WRONGTYPE, ACL, and missing scripting-engine replies,
prove that a reply arrived and count as healthy outcomes. Raw error replies and typed command
exceptions use the same health policy. Health sampling does not change the exception delivered
to the caller.

Fire-and-forget dispatch checks circuit admission, but successful queue acceptance is ignored
because its reply is discarded. It therefore cannot close a half-open circuit on its own.
Fire-and-forget operations that observe a transport failure still report that failure.
Cache-fenced fire-and-forget commands that already await a reply retain their existing behavior
and can contribute a completed reply outcome.

## Batches and transactions

Adding commands to a queue requires no admission. An empty ordinary batch or transaction
requires no admission because it sends no frame.
Each batch command acquires its own permit when execution reaches transport dispatch on the
selected connection. An open endpoint sends none of the rejected commands. `TryExecuteAsync`
returns failures in original queue order; `ExecuteAsync` throws the first failure after completing
every pending. Successful pending results retain their existing ownership.

A batch can dispatch partially: admitted commands can execute while later entries are rejected,
including when all half-open slots are occupied. Rejected entries are not retried or replayed.
Accepted replies remain in FIFO order, including after cancellation releases a permit. Circuit
admission does not make a pipeline atomic. Circuit-enabled batches stay on their selected
connection rather than moving individual entries during a maintenance handoff.

A nonempty transaction acquires one permit for the entire MULTI/EXEC sequence immediately before
dispatch. Open rejection sends neither MULTI nor its queued commands nor EXEC, and faults all
pending results with `RespireCircuitOpenException`. A normal transaction can follow a maintenance
replacement only before the sequence is accepted; the replacement requires fresh admission.
Watched transactions and hash import transactions retain their original connection. WATCH setup
is an immediate command with its own admission; a rejected commit discards its dedicated WATCH
lease. A WATCH abort or ordinary Redis error still demonstrates a healthy reply.

Hash import batches admit their entries in connection order. Rejection before dispatch preserves
the import session and its prepared fieldsets. Durability batches also guard each write and the
subsequent WAIT or WAITAOF separately. A failed write prevents the acknowledgement; a rejected
acknowledgement cannot undo writes that already completed. Empty durability batches still require
admission for their acknowledgement command.

Cancellation, validation failure, and undispatched exceptions release recovery capacity as ignored.
Each transaction or batch permit completes even when response conversion fails. These boundaries
preserve the existing transaction completion, batch failure, and result disposal contracts.

## Current scope

This option covers standalone immediate typed, raw, interpolated, fire-and-forget, cache-miss,
blocking, streamed, batch, and transaction command dispatch.

Configuration with Redis Cluster, Sentinel, or standalone replica endpoints is rejected.
Topology-specific circuit admission is separate work. This option does not change
`RespireFailoverGroup` probe/failback behavior. The wider
[resilience work](https://github.com/thomhurst/Respire/issues/863) remains open for retry and
telemetry integration.
