# Connection recovery

`RespireOptions.ReconnectPolicy` configures replacement of failed multiplexed command
connections, including connections owned by Redis Cluster nodes, and retries of failed
dedicated connection acquisitions. It is null by default:
replacement starts immediately, and a failed replacement is retried on the next use.
Setting a policy preserves that demand-driven scheduling while adding backoff and limits.
It does not create a perpetual retry loop or replay any Redis command.

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
This includes repeated authentication, permission, or protocol handshake failures: the
policy does not classify those failures as permanent. Use a finite limit or caller
cancellation when a misconfigured endpoint must fail within a bounded acquisition episode.

Connect timeouts and handshake command deadlines remain per attempt. Use caller
cancellation to bound the entire rent, including all delays and handshakes. Cancelling
one rent does not cancel another. Retirement and disposal cancel pending rents and wait
for their owned connection cleanup. Retirement still lets borrowed operations finish;
disposal aborts them. Corrective fencing acquisitions retain their own deadline and retry
rules and bypass this policy, including its attempt cap.

## Lifecycle and telemetry

`ConnectionStateChanged` retains its existing endpoint health aggregation and adds:

| Property | Meaning |
| --- | --- |
| `ReconnectAttempt` | One-based configured attempt; zero for transitions without policy metadata |
| `ConnectionSlot` | Source slot within this endpoint's multiplexer generation, including null-policy recovery; null for endpoint-wide transitions |
| `NextReconnectDelay` | Actual scheduled delay before this attempt; null when no attempt is scheduled |
| `ReconnectExhausted` | This slot or dedicated rent reached its configured limit |
| `ReconnectSource` | `Dedicated` for a dedicated rent, `Command` for a multiplexer event; `Unspecified` for other paths |
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
The ordered observer queue has no capacity limit. Slow or blocked callbacks can accumulate
pending observations across concurrent renters; keep handlers short and hand off work to
an application queue with an explicit capacity policy. A finite retry limit bounds each
rent's retries, not total concurrent rents or the observer queue.

The `Respire` meter records `respire.connection.reconnect.attempt` (attempt number) and
`respire.connection.reconnect.delay` (seconds), tagged with `server.address`, `server.port`,
and `respire.connection.source` (`command` or `dedicated`).
They record scheduled replacement attempts, including waits later cancelled by disposal.
They are histograms of events, not live countdown gauges. The counter
`respire.connection.reconnect.exhausted` records each episode that reaches its limit, with the same
endpoint tags, so operators can alert on terminal recovery failures. Successful command execution
does not record these instruments or inspect policy counters.

## Remaining recovery paths

The policy covers command multiplexers and dedicated pools. Pub/sub retains its existing reconnect/resubscribe
loop (#545); Sentinel and Cluster discovery retain their current fallback behavior (#546).
Those native children extend the same policy contract under parent #401. Sentinel currently
resolves at connection time; automatic Sentinel failover is tracked separately in #396,
and periodic Cluster refresh in #397. Setting this option does not enable those features.
