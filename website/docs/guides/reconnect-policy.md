# Connection recovery

Endpoint-specific recovery failures expose `RespireReconnectLimitException.Endpoint` and retain their underlying causes. Recovery across several Cluster discovery candidates leaves the endpoint null; inspect the inner exceptions for candidate context. See [exception endpoint context](../fundamentals/connections.md#exception-endpoint-context).

`RespireOptions.ReconnectPolicy` adds backoff and attempt limits to connection recovery:
command connection replacement (including Cluster node connections), dedicated connection
acquisitions, pub/sub recovery, and Cluster and Sentinel discovery fallback.

The default is `null`: a failed command connection is replaced immediately, and a failed
replacement is retried while queued standalone commands need it, or when a later command needs
it. A policy adds delays and limits to recovery. It never resends an accepted command; for that, see the
[command retry policy](command-retries.md).

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

| Setting | Default | Bounds |
| --- | --- | --- |
| `InitialDelay` | 250 milliseconds | Zero or more, at most `MaxDelay`. |
| `BackoffMultiplier` | `2` | Finite, at least 1. |
| `MaxDelay` | 5 seconds | At most one day. Caps the delay after jitter. |
| `JitterRatio` | `0.2` | Finite, from 0 through 1. |
| `MaxAttempts` | `null` (unlimited) | Positive, or `null`. |

- The first replacement waits `InitialDelay`. Each failed attempt multiplies the delay by `BackoffMultiplier`, capped at `MaxDelay`.
- Jitter scales the capped delay by a random factor from `1 - JitterRatio` to `1 + JitterRatio`, then caps it again. Zero initial delay stays zero.
- An invalid policy throws `RespireConfigurationException` before any connection I/O.
- Policies are immutable records and can be shared across clients.
- Leave `MaxAttempts` null for long-lived clients that must keep trying after an outage. [Failover group](failover-groups.md) candidates require it.

## Command connections

Each endpoint has `RespireOptions.Connections` command connections (one by default), and each
counts attempts separately.

- Concurrent callers share an in-progress replacement.
- A successful replacement, including its handshake, resets the count. The count tracks consecutive replacement failures, so a peer that accepts and then drops connections starts a new episode each time.
- Exhaustion is permanent for that connection; more commands do not reset it. Recreate the client to start over. Healthy connections at the endpoint stay usable.
- After a Cluster topology change, the new node connections have their own counters.

When every connection at an endpoint is exhausted, commands throw
`RespireReconnectLimitException` (a `RespireConnectionException`). Operations that need every
connection's server client ID throw when any connection is exhausted. Cleanup connections that
Respire opens after an ambiguous outcome use their own `ConnectTimeout` and ignore this policy.

### Standalone and Sentinel command backlog {#standalone-command-backlog}

After a standalone or Sentinel client has connected, `RespireOptions.BacklogPolicy` defaults to
`RespireBacklogPolicy.Queue`. When its primary command connections are unavailable during
replacement, definitely-unsent operations wait in a bounded FIFO backlog. This works with
`RetryPolicy = null`: waiting to send a command for the first time does not require permission
to retry a command that may already have run.

```csharp
var options = new RespireOptions
{
    Endpoints = { new RespireEndpoint("localhost") },
    BacklogPolicy = RespireBacklogPolicy.Queue, // Default.
    BacklogCapacity = 1024,                    // Default; must be positive.
    CommandTimeout = TimeSpan.FromSeconds(10),
};
```

- Typed, native string/byte, raw and fire-and-forget commands participate. A batch or unwatched
  transaction occupies one queue entry and sends no member before admission.
- New arrivals stay behind older queued operations while the backlog drains. The capacity counts
  waiting operations; a dispatched operation no longer occupies a waiting entry. A full queue
  rejects new work with `RespireConnectionException`.
- The drain executes one operation at a time, waiting for its completion (including replies and
  configured retries) before starting the next. This preserves ordering across whole batches,
  transactions and retries, but temporarily prevents pipelining. A slow head operation delays
  later arrivals even after recovery, and sustained traffic can fill the queue. Normal concurrent
  dispatch resumes when it empties. Use `FailFast` when this recovery trade-off is unsuitable.
- Each operation keeps its original `CommandTimeout` and caller cancellation through both queue
  wait and execution. Expiry while waiting throws `RespireTimeoutException` with diagnostics stage
  `Connecting`. A cancelled or expired entry is removed without being sent and releases capacity.
- Setting `CommandTimeout = null` explicitly disables the deadline, including the queue wait.
  With unlimited reconnect attempts, those callers can wait indefinitely. Supply caller
  cancellation, a finite command timeout or a reconnect attempt limit to bound that wait.
- Cancelling one operation does not cancel shared recovery. Terminal reconnect exhaustion fails
  pending work with `RespireReconnectLimitException`; disposing the client also ends pending waits.
- Fire-and-forget still waits for admission. It does not promise successful execution or expose
  server errors after admission.

Set `BacklogPolicy = RespireBacklogPolicy.FailFast` to keep immediate connection-acquisition
failure when no usable command connection is available. Connection recovery itself still runs.
`ReconnectPolicy` controls replacement backoff and attempt limits; `BacklogPolicy` controls
whether eligible unsent work waits for that replacement.

For Sentinel primaries, the FIFO belongs to the router and survives generation retirement.
Its head rediscovers and ROLE-validates the elected primary before dispatch; queued commands
never reconnect a retired primary independently. Commands arriving during discovery or drain
join that same bounded queue, including batches and unwatched transactions. Every next entry
selects the current generation again. A finite reconnect policy bounds repeated discovery rounds
for the head, in addition to the resolver's per-round candidate budget; exhaustion fails all
pending entries. Without an explicit policy, failed discovery rounds use Sentinel's default
backoff. Discovery timeouts propagate to the waiting head. The command's original deadline
still bounds its entire wait and execution.

Cluster clients also queue unsent multiplexed commands after initialization. Each hash slot has
its own FIFO and `BacklogCapacity` limit; unkeyed commands share a separate queue. A batch is
grouped by slot, and each group occupies one entry. An unwatched transaction occupies one entry
for its slot. Healthy slots continue independently while another slot recovers. Each queued head
reselects the current owner and read policy before dispatch, so an owner change does not leave
work waiting on the retired generation. Strict replica reads remain on replicas; primary fallback
is allowed only by the configured read policy. Tracked reads retain their tracking prelude and
mutations retain their cache fences. Circuits still govern acquisition and dispatch.

Non-Cluster replica reads use the separate router backlog described below. Initial connection setup, dedicated
connections, watched transactions, import sessions and server-local cursor continuations do not
join this backlog. The specialized `Locks` expiry-update and release paths also retain their
existing acquisition and recovery rules and have no ordering guarantee relative to queued
commands. In particular, `Locks.ResetExpiryAsync` can fail during replacement even with `Queue`
selected. Lock acquisition (`SET NX PX`) and owner-token reads use ordinary queued command dispatch.

### Replica-read backlog

After a non-Cluster read router has successfully selected a connection, definitely-unsent reads
can wait in one bounded FIFO shared by its read-policy views. This applies to standalone and
Sentinel replicas, typed/raw reads, read-only scripts and functions, bulk streams, fire-and-forget,
fresh cursor reads and the initial selection for hedged reads. `BacklogPolicy` and `BacklogCapacity`
have the same defaults, timeout, cancellation and overflow behavior as the primary backlog.

Each head reselects using its original `ReadFrom` policy and current membership. Selection still
validates `ROLE`, respects failed-replica cooldown, Nearest and availability-zone preferences, and
uses primary fallback only when the policy permits it. Sentinel membership replacement does not
discard the queue or pin work to an obsolete replica. New arrivals stay behind a draining head,
even if a ready connection becomes available; primary-only operations use their own dispatch path.

An exhausted candidate does not prevent another eligible candidate from serving the read. When
all eligible candidates report terminal exhaustion, the read fails. Initial acquisition failures,
server-local cursor continuations and dedicated/blocking rentals retain their existing behavior.
The backlog never replays an accepted read or an optional hedge. A streamed read releases the
drain when its stream is returned; the consumer still owns reading and disposing that stream.

Other behavior, with or without a reconnect policy:

- A command already accepted by a failed connection can still fail while replacement proceeds.
- Operations outside the reconnect backlog, or with `FailFast`, can fail during replacement.
- Each connection attempt keeps its `ConnectTimeout`. The policy does not retry the client's initial connection.
- Command deadlines and caller cancellation bound their own waits. Cancelling one caller does not cancel shared recovery.
- Disposing the client cancels scheduled delays and waits for recovery to finish.

:::warning
Connection recovery and its backlog never replay commands accepted by a failed connection.
A missing reply leaves the server outcome uncertain. Explicit command retry policies and
application retries must account for idempotency.
:::

## Dedicated connection acquisitions

Dedicated pools serve blocking operations, watched transactions and other exclusive-connection
operations. A healthy idle connection is reused. Otherwise each rent makes one immediate
connection attempt.

- **Null policy**: the failure returns immediately.
- **With a policy**: the rent retries, waiting `InitialDelay` first and then the same backoff and jitter. Only connection setup and handshake retry; no application command, transaction or blocking operation is replayed.
- `MaxAttempts` counts retries after the first attempt: 2 allows three attempts per rent.
- Concurrent renters have independent budgets; the policy is not an endpoint-wide rate limiter.
- Exhaustion throws `RespireReconnectLimitException` with the last connection failure as its inner exception. A later rent starts fresh.
- An unlimited policy can keep a rent pending until it connects or is cancelled. The pool limits idle connections, not concurrent acquisitions, so bound waiting work with cancellation and your own concurrency limits.

| Failure | Retries? |
| --- | --- |
| Connection failures, connect and handshake timeouts | Yes |
| Server errors where `RespireServerException.IsTransient` is true | Yes |
| `WRONGPASS`, `NOAUTH`, `NOPERM`, ordinary `ERR` | No |
| TLS authentication failures | No |
| `RespireProtocolException`, `RespireConfigurationException` | No |

A permanent failure ends the rent without marking its budget exhausted. Connect timeouts and
handshake deadlines are per attempt; use caller cancellation to bound the whole rent. Cancelling
one rent does not affect another. Pool retirement and disposal cancel pending rents; retirement
lets borrowed operations finish, disposal aborts them.

## Pub/sub reconnection and resubscription

- **Null policy**: an immediate replacement attempt, then on failure 250 ms exponential waits capped at five seconds.
- **With a policy**: the first replacement waits `InitialDelay`. Each failed connection or resubscription uses one attempt from the same episode.
- Initial subscription setup is a single attempt bounded by the caller's cancellation.
- Successful resubscription of every registered route resets the count. A socket that connects but fails while resubscribing does not.
- In Cluster mode, sharded subscriptions recover separately from channel and pattern subscriptions. Exhausting one leaves the other usable.

:::warning
Redis cannot replay messages missed during an interruption. Gap markers stay ordered before
messages received after each route's acknowledgement.
:::

While recovery is active, new subscriptions fail with `RespireConnectionException`. Retry after
a lifecycle event with `ReconnectSource = PubSub` and `SourceState = Connected`. You can remove
subscriptions during backoff.

When the limit is exhausted:

1. The client publishes `Disconnected` with `ReconnectExhausted = true` and the final error.
2. Live subscriptions end with `RespireSubscriptionEndReason.ReconnectExhausted`. Existing enumerators may drain buffered items first.
3. New subscriptions throw `RespireReconnectLimitException`. Recreate the client to subscribe again. Command connections are unaffected.

Lifecycle handlers can dispose the client synchronously. Keep them short; events can lag the
work they describe.

## Lifecycle and telemetry

`ConnectionStateChanged` adds these properties:

| Property | Meaning |
| --- | --- |
| `ReconnectAttempt` | One-based configured attempt; zero for transitions without policy metadata |
| `ConnectionSlot` | Source connection slot at this endpoint, including recovery without a policy; null for endpoint-wide transitions |
| `NextReconnectDelay` | Actual scheduled delay before this attempt; null when no attempt is scheduled |
| `ReconnectExhausted` | This slot, dedicated rent, pub/sub episode, or Cluster discovery round reached its configured limit |
| `ReconnectSource` | `Dedicated` for a dedicated rent, `Command` for a command connection, `PubSub` for subscription recovery, `ClusterDiscovery` for topology/endpoint fallback, `SentinelMonitor` for a Sentinel event monitor's subscription; `Unspecified` for other paths |
| `SourceState` | Source connection state before endpoint health aggregation, when supplied |
| `ReconnectEpisodeId` | Process-local identifier grouping one dedicated rent or Cluster discovery round; null for other paths |

- Attempt events fire even when aggregate endpoint health has not changed. Treat the metadata as describing one source attempt, not an endpoint-wide counter.
- `State` stays the aggregate endpoint health; `SourceState` describes the source attempt.
- Episode IDs are scoped by `ReconnectSource`, are event metadata only, and are not metric tags.
- Events from different sources have no shared ordering and can arrive after the triggering operation completes. They are observations, not a way to gate retries.
- Caller cancellation and pool retirement or disposal stop a rent without a `Disconnected` failure event.
- After disposal, pending lifecycle events are suppressed; queued measurements still drain.

:::warning
The observer queue has no capacity limit. Slow or blocked handlers accumulate pending events.
Keep handlers short and hand work off to a bounded application queue. `MaxAttempts` bounds
retries, not the queue.
:::

The `Respire` meter records:

| Instrument | Type | Meaning |
| --- | --- | --- |
| `respire.connection.reconnect.attempt` | Histogram | Attempt number of each scheduled attempt |
| `respire.connection.reconnect.delay` | Histogram | Scheduled delay in seconds |
| `respire.connection.reconnect.exhausted` | Counter | Episodes stopped by the attempt limit; use it to alert on terminal recovery failures |

Tags: `server.address`, `server.port` and `respire.connection.source` (`command`, `dedicated`
or `pubsub`). Discovery and monitor measurements carry `respire.reconnect.scope` instead of
`respire.connection.source`. Scheduled
attempts are recorded even if disposal later cancels the wait. Pub/sub attempts have a null
`ConnectionSlot`. Successful commands record nothing.

## Sentinel discovery fallback

The first Sentinel candidate is tried immediately. With a policy, each later configured or
learned candidate uses one attempt and waits the policy delay first. A rejected primary `ROLE`
reply uses that same attempt.

For example, with configured candidates A and B and peers C and D learned from A,
`MaxAttempts = 1` allows A then B. If both fail, C and D stay untried and the policy is
exhausted.

- Traversal stops when candidates run out, even with null `MaxAttempts`. Endpoints are not cycled.
- A null policy tries all candidates immediately.
- A validated primary ends resolution. Failure throws `RespireConnectionException` with the underlying discovery or validation error.
- Each `ConnectAsync` call or reactive resolution starts a fresh budget.
- Each candidate keeps its discovery and connection timeout; policy delays sit outside those. Supply a caller deadline to bound the whole resolution.
- No application command or `WATCH` state is replayed during discovery or primary handoff.

Measurements use `respire.reconnect.scope = sentinel-discovery`. Exhaustion is recorded only when
untried candidates remain, so check the returned exception to detect every terminal failure.

Disconnects and `READONLY` replies make the next operation resolve Sentinel again with the same
policy. Validated endpoint changes increment `respire.sentinel.failover`. Eager `ConnectAsync`
discovers before returning; lazy clients report initial connection events through
`ConnectionStateChanged`.

### Sentinel event monitors

Sentinel event monitors apply the policy to their own subscription connections, with
`respire.reconnect.scope = sentinel-monitor`. `MaxAttempts` counts replacement attempts, so a
failed initial subscription still gets a retry. An exhausted monitor stops observing that
Sentinel and logs a warning; discovery still runs on demand. A validated primary change gives
each exhausted monitor a fresh budget.

## Cluster discovery fallback

Each Cluster discovery round shares one budget across all candidate sources: cached owners,
known masters, topology queries, configured seeds and connection selection. Pub/sub endpoint
discovery and cluster-wide commands follow the same rules.

- The first candidate is immediate. Each later candidate uses one attempt and waits the policy delay.
- Connecting other masters after a successful topology query uses no attempts.
- For example, with `MaxAttempts = 1`, a failed cached owner may try one known master. If that fails too, seed fallback cannot start a new budget.
- Exhaustion throws `RespireReconnectLimitException`; running out of candidates throws the connection error. A later round starts fresh. Failed rounds do not disable the router.

Redirects and retirement:

- The first `MOVED` or `ASK` in a round follows its target immediately. Later redirects and failures use the budget and back off.
- `READONLY` backs off before its first replacement.
- If recovery fails, the original server exception is preserved.
- Retirement during one command, batch, unwatched transaction, blocking redirect or resumable scan page keeps the same budget until it completes.
- Accepted batch entries and watched transactions are never replayed.

Other rules:

- A null policy keeps immediate fallback.
- If `CLUSTER SLOTS` is unsupported or ACL-restricted, the initial seed connection and redirect routing still work, but cluster-wide commands still need a complete topology.
- Caller cancellation and disposal stop waits. `READONLY` recovery keeps its overall `ConnectTimeout`, and policy waits count against it. Bound other rounds with a caller deadline.

Measurements use `respire.reconnect.scope = cluster-discovery`. Exhaustion counts only when
another fallback would exceed the budget. Lifecycle events use
`ReconnectSource.ClusterDiscovery`, a `ReconnectEpisodeId` shared across clients in the process,
and a null `ConnectionSlot`. A caller-cancelled round emits `Disconnected` with the cancellation
error; that is not exhaustion.

## Periodic Cluster refresh

Background Cluster refresh uses the same discovery budget; it adds no separate retry counter.

`ClusterTopologyRefreshInterval` controls the periodic timer (default 60 seconds). Set it to
`null`, `TimeSpan.Zero` or `Timeout.InfiniteTimeSpan` to disable the timer. Redirects, primary
disconnects and failed-refresh retries still trigger discovery. Each refresh has a 60-second
deadline and keeps the last slot map if discovery fails. See
[Cluster topology refresh](../fundamentals/connections.md#redis-cluster-endpoint-identity).
