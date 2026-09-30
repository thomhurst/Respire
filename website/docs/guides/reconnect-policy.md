# Connection recovery

`RespireOptions.ReconnectPolicy` configures replacement of failed multiplexed command
connections, including connections owned by Redis Cluster nodes, dedicated connection acquisitions,
and pub/sub recovery. It is null by default. For command connections,
replacement starts immediately, and a failed replacement is retried on the next use.
Setting a policy preserves that demand-driven scheduling while adding backoff and limits.
It does not create a perpetual command retry loop or replay any accepted Redis command.

```csharp
var options = new RespireOptions
{
    Endpoints = { new RespireEndpoint("localhost") },
    ReconnectPolicy = new RespireReconnectPolicy
    {
        InitialDelay = TimeSpan.FromMilliseconds(250),
        BackoffMultiplier = 2,
        MaxDelay = TimeSpan.FromSeconds(5),
        JitterRatio = 0.2,
        MaxAttempts = 10,
    },
};
```

The first replacement waits `InitialDelay`. Later attempts multiply that delay by
`BackoffMultiplier` for each failed attempt, capped at `MaxDelay`. Symmetric jitter
scales the capped delay by a random factor from `1 - JitterRatio` through
`1 + JitterRatio`; the actual delay is capped again. Zero initial delay stays zero.
Maximum delay cannot exceed one day, the multiplier must be finite and at least one,
and the jitter ratio must be finite and between zero and one. Invalid policies fail
before connection I/O. Policies are immutable records and can be shared across clients.

Attempts count separately for each slot in a multiplexer generation. Concurrent callers
share an in-progress replacement; they do not each create another attempt. A successful
replacement, including its handshake and required connection identity, resets the count.
The counter tracks consecutive replacement failures, not connections that fail after
a successful handshake. A peer that repeatedly accepts and then drops connections
therefore starts a new episode each time, with the initial delay. There is no healthy
duration window or first-command condition for reset.
`MaxAttempts = null` permits unlimited attempts. Exhaustion remains in effect for that
failed slot; repeatedly submitting commands does not reset it. Recreate the client to
begin a new episode after exhaustion. A newly discovered Cluster generation has its own
slots and counters. Healthy slots remain usable when another slot is exhausted.

When every slot at an endpoint has exhausted recovery, acquisition throws
`RespireReconnectLimitException`, a `RespireConnectionException`. Individual failure
events retain the original connection error. Identity setup requires every slot, so it
throws when any required slot is exhausted even if another slot can still serve ordinary
commands. Failed correction fences retain their server-side identity obligations; a
terminal recovery error does not mark an unacknowledged fence complete.
Fences connect directly to the captured physical peer with their own ConnectTimeout;
they do not acquire or revive an exhausted command slot. A fence may therefore succeed
after command recovery is exhausted, or report its own connection/permission error
while retaining the owed identity. The command reconnect limit does not cap attempts
to discharge that separate correctness obligation.
A command that notices a failed connection
can still fail while replacement proceeds, as it did before configuring this policy.
Each connection attempt retains `ConnectTimeout`; initial multiplexer setup is not retried
by this policy. Command deadlines and caller cancellation still bound their own waits.
Cancelling one caller does not cancel recovery shared by other callers. Retiring or
disposing the multiplexer cancels its scheduled delays and awaits owned recovery work.

Commands accepted by a failed connection are never replayed. A missing reply can leave
the server outcome uncertain. This policy only creates replacement connections; application
retry decisions still need to account for command idempotency.

## Dedicated connection acquisitions

Dedicated pools serve blocking operations, watched transactions, and other operations
that require an exclusive connection. A healthy idle connection is reused immediately.
When no healthy idle connection exists, each rent makes one immediate connection attempt.
With a null policy, its failure is returned immediately, preserving the existing on-demand
behavior. With a policy, a failed attempt starts a retry episode owned by that rent.
The first retry waits `InitialDelay`; subsequent retries use the same backoff and jitter
calculation as command replacements. Only connection establishment and its handshake are
retried. No accepted application command, transaction, or blocking operation is replayed.

`MaxAttempts` counts retries after the initial attempt: a limit of two allows at most
three connection attempts for one rent. Concurrent renters have independent budgets and
can connect concurrently; the policy is not an endpoint-wide rate limiter. Success ends
the episode. Exhaustion throws `RespireReconnectLimitException` with the last connection
failure as its inner exception. It does not disable the pool: a later rent starts fresh.
An unlimited policy can keep one rent pending until it connects or is cancelled.
That rent retains its acquisition reservation through backoff and handshake cleanup.
The pool limits retained idle connections, not concurrent acquisitions; callers should
bound total waiting work with cancellation and their own concurrency limits.
Connection failures, connection/handshake timeouts, and server errors classified by
`RespireServerException.IsTransient` can retry. Permanent server handshake rejections
(including `WRONGPASS`, `NOAUTH`, `NOPERM`, and ordinary `ERR`), TLS authentication failures,
and explicit `RespireProtocolException`/`RespireConfigurationException` failures return immediately.
A permanent failure encountered
after a retry starts ends that episode without marking its attempt budget exhausted.

Connect timeouts and handshake command deadlines remain per attempt. Use caller
cancellation to bound the entire rent, including all delays and handshakes. Cancelling
one rent does not cancel another. Retirement and disposal cancel pending rents and wait
for their owned connection cleanup. Retirement still lets borrowed operations finish;
disposal aborts them. Corrective fencing acquisitions retain their own deadline and retry
rules and bypass this policy, including its attempt cap.

## Pub/sub reconnection and resubscription

With a null policy, pub/sub retains its existing schedule: an immediate replacement
attempt followed, on failure, by 250 ms exponential waits capped at five seconds.
With a policy, the first replacement waits `InitialDelay`, and every failed connection
or resubscription consumes one attempt from the same episode. The shared policy applies
its multiplier, jitter, and maximum delay to each scheduled attempt. Initial subscription
connection setup is still a single attempt governed by its caller's cancellation.

One recovery loop owns the replacement until every currently registered route has been
acknowledged. A socket that connects but fails while resubscribing does not reset the
budget or start another watcher. A failed resubscription closes its replacement before
the next attempt. Successful resubscription resets the count; a later interruption
starts a new episode. Existing gap markers remain ordered before messages received after
each route's acknowledgement. Redis cannot replay messages missed during an interruption.

While configured recovery is active, new explicit subscriptions fail with
`RespireConnectionException`; they do not open a competing connection or skip the delay.
Retry after a lifecycle event with `ReconnectSource = PubSub` and `SourceState = Connected`.
Removing existing subscriptions
is supported during backoff, and recovery snapshots the remaining routes under the control
gate. A caller cancellation during an explicit subscription does not cancel shared recovery.

When the configured limit is exhausted, the client publishes `Disconnected` with
`ReconnectExhausted = true` and the final connection/resubscription error. All live
subscriptions complete with `RespireSubscriptionEndReason.ReconnectExhausted` and become
disposed. Existing enumerators may drain already buffered items before ending; a new
enumerator cannot be opened on an ended subscription. New subscriptions throw
`RespireReconnectLimitException`. There is no cooldown or implicit reset: recreate the
client to subscribe again. Command connections remain usable subject to their own health.

Disposal cancels configured delays, connection handshakes, and resubscription waits,
then drains owned recovery and socket cleanup. Ordered lifecycle delivery happens outside
the recovery task so a lifecycle handler can synchronously dispose the client. Keep event
handlers short; attempt observations can lag the work they describe.

## Lifecycle and telemetry

`ConnectionStateChanged` retains its existing endpoint health aggregation and adds:

| Property | Meaning |
| --- | --- |
| `ReconnectAttempt` | One-based configured attempt; zero for transitions without policy metadata |
| `ConnectionSlot` | Source slot within this endpoint's multiplexer generation, including null-policy recovery; null for endpoint-wide transitions |
| `NextReconnectDelay` | Actual scheduled delay before this attempt; null when no attempt is scheduled |
| `ReconnectExhausted` | This slot, dedicated rent, or pub/sub episode reached its configured limit |
| `ReconnectSource` | `Dedicated` for a dedicated rent, `Command` for a multiplexer event, `PubSub` for subscription recovery; `Unspecified` for other paths |
| `SourceState` | Source connection state before endpoint health aggregation, when supplied |
| `ReconnectEpisodeId` | Process-local identifier grouping one dedicated rent's retry events; null for other paths |

Policy attempt events are delivered even if aggregate endpoint health has not changed.
An endpoint can remain disconnected because another slot has failed while the source slot
starts recovery. Use the metadata for the source attempt, not as an endpoint-wide counter.
Do not block event handlers. Synchronous disposal remains supported.

Dedicated recovery does not mark a command slot unhealthy. Its event `State` remains the
aggregate endpoint health, while `SourceState` describes the dedicated attempt. Dedicated
events and measurements are dispatched asynchronously in pool order so observers can
dispose the client without blocking the acquisition disposal must drain. Events can lag
the corresponding attempt; they are observations, not a mechanism for gating retries.
After client disposal, pending dedicated lifecycle events are suppressed. Episode IDs are
event metadata only and are not metric tags.
Caller cancellation and pool retirement/disposal stop a rent without emitting a dedicated
`Disconnected` failure. Scheduled-attempt measurements may still be delivered after pool
shutdown. Queued lifecycle callbacks are skipped once the dispatcher observes that the
pool is stopping; callbacks already in flight are not joined because they can dispose it.
The ordered observer queue has no capacity limit. Slow or blocked callbacks can accumulate
pending observations across concurrent renters; keep handlers short and hand off work to
an application queue with an explicit capacity policy. A finite retry limit bounds each
rent's retries, not total concurrent rents or the observer queue.

The `Respire` meter records `respire.connection.reconnect.attempt` (attempt number) and
`respire.connection.reconnect.delay` (seconds), tagged with `server.address`, `server.port`,
and `respire.connection.source` (`command`, `dedicated`, or `pubsub`). Pub/sub attempts have a null
`ConnectionSlot` and use one shared counter for the current connection/resubscription episode.
They record scheduled replacement attempts, including waits later cancelled by disposal.
They are histograms of events, not live countdown gauges. The counter
`respire.connection.reconnect.exhausted` records each episode stopped by its attempt limit, with the same
endpoint tags, so operators can alert on terminal recovery failures. Successful command execution
does not record these instruments or inspect policy counters.

## Sentinel discovery fallback

Sentinel resolution keeps its first candidate immediate. With a configured policy,
each later configured or learned Sentinel candidate consumes one fallback attempt and
waits the policy delay before I/O. `MaxAttempts = 1` therefore permits the first
candidate plus one fallback. A rejected primary `ROLE` response consumes that same
candidate attempt; peer expansion and primary connection do not start nested budgets.
For example, with configured candidates A and B and peers C and D learned from A,
`MaxAttempts = 1` allows A then B. If both fail, C and D remain untried and the policy
exhaustion counter increments. With only A and B available, both still run, but their
failure ends through candidate depletion rather than policy exhaustion.
The existing finite traversal still stops when candidates run out, even with a null
`MaxAttempts`. Endpoints are not cycled indefinitely.

A successful validated primary ends the resolution. Exhaustion preserves the final
`RespireConnectionException` and its underlying discovery or primary-validation error.
A new explicit `ConnectAsync` call starts a fresh resolution budget. Null policy retains
immediate fallback across all available candidates. Caller cancellation bounds every
wait, while each candidate retains its existing discovery and connection timeout.
Policy delays sit outside those per-candidate timeouts. Supply a caller deadline to
bound the total resolution, including backoff and every candidate.
No application command is replayed during discovery.

The same attempt/delay/exhaustion instruments carry the candidate's `server.address`
and `server.port` plus `respire.reconnect.scope = sentinel-discovery`. Exhaustion is
attributed to the final failed fallback. Scheduling is recorded before delay, including
waits later cancelled by the caller. Exhaustion is recorded only when the failed fallback
uses the budget and at least one candidate remains untried. Successful fallback and running
out of candidates do not record exhaustion, even when the final candidate coincides with
the attempt limit. Observe the returned connection exception to detect every terminal
discovery failure.
Initial Sentinel resolution has no returned client for `ConnectionStateChanged`
subscriptions; lifecycle events for ongoing failover belong to #396. This policy does
not enable automatic failover or lazy Sentinel routing.

## Remaining recovery paths

The policy covers command multiplexers, dedicated pools, pub/sub, and Sentinel fallback.
Cluster discovery retains its current fallback behavior (#568 under #546).
That native child extends the same policy contract under parent #401. Sentinel currently
resolves at connection time; automatic Sentinel failover is tracked separately in #396,
and periodic Cluster refresh in #397. Setting this option does not enable those features.
