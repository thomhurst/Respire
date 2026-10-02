# Internal transport retirement

`RespireConnection.RetireAsync()` rejects new command acceptance under the same write gate that publishes serialized frames to the response FIFO. A command already accepted continues writing its complete frame and consumes its reply, even if its caller has cancelled. An operation waiting for FIFO capacity has not been accepted and wakes immediately with `RespireConnectionRetiredException`.

The retirement task is shared across callers. It completes after accepted writes, response parsing, scheduled response completions, and socket cleanup finish. `IsAcceptingCommands` becomes false immediately; physical `IsConnected` remains true while draining. No cancellation interrupts a partially written frame. An ordinary socket failure or configured timeout retains its existing behavior. `CommandTimeout`
only abandons the caller's wait; it does not remove the accepted FIFO slot or prove that Redis
finished the command. Retirement therefore still waits for its reply. There is no implicit drain
timeout: an owner can bound its wait and explicitly dispose to abort a silent peer. A configured
connection response watchdog still aborts the socket according to its existing policy.

When a delivered reply's inline continuation requests retirement, the completion scheduler
hands its remaining replies to another worker in their original order. Retirement can then
finish without waiting for that same continuation to return. The current reply is already
delivered; its source reference is released when the continuation returns. Other queued
completions still drain before retirement completes.

Owners that explicitly choose abortive escalation can call the multiplexer overload
`RetireAsync(abortOnCancellation)`, passing a token with their chosen grace deadline. It awaits
graceful retirement normally; cancellation triggers `DisposeAsync`, awaits cleanup, and throws
`OperationCanceledException`. The parameterless primitive retains its graceful-only contract.
Cancellation is not proof of successful drain or correction safety: retained fence IDs must still
be reconciled by their owner, and the disposed multiplexer cannot perform a later fence retry.

`RespireConnectionMultiplexer.RetireAsync()` stops connection selection and background reconnects, cancels pending handshakes, and prevents initialization or reconnect publication after retirement. It retires existing transports immediately, waits for unpublished connection cleanup, and then awaits their drain tasks. Lifecycle notifications are queued in transition order and delivered outside lifecycle locks.

The retirement `Disconnected` notification means the multiplexer has stopped accepting work.
Accepted commands may still be draining; await the retirement task to observe completion.

## Retry boundary

`RespireConnectionRetiredException` is internal and distinct from an ambiguous `RespireConnectionException`. A router may select the current generation and retry only an operation rejected with the retired exception: that operation did not enqueue any bytes. It must not retry an accepted command merely because topology changed. Cluster topology publication detaches old generations before starting their graceful retirement.

Cluster command, tracked-read, fire-and-forget, script, lock, batch, and unwatched transaction
paths retry that rejection with the existing bounded redirect budget. ASK retries retain the
temporary target and ASKING prefix without changing the slot owner. Tracked executions publish
the replacement identity before writing, and cached reads refresh their continuity token.
An accepted batch entry is never replayed because another entry was rejected.

Route acquisition also retries retirement before returning a connection, a dedicated pool, or
an entire primary snapshot. Retirement-owned cancellation of an unpublished handshake is
recognized separately from caller cancellation. Typed FUNCTION/SCRIPT mutations, database
flush/size fan-outs, SCAN pages, and server-node discovery retry only the rejected endpoint;
already accepted peers are never replayed. Socket-pinned CLIENT operations and existing WATCH
state do not use this endpoint replacement helper. SCAN retains its usual weak iteration
semantics during topology changes; this does not provide a resharding-resumable cursor.

Blocking commands, WATCH creation, and durability batches can also reselect a pool retired
between selection and rent, including retirement cancellation during the handshake. This retry
ends at successful rent; it never replays application commands or WATCH state. The returned pool
stays with its lease through return or disposal. Caller cancellation and client disposal stop retries.

`DedicatedLeaseAcquisition` owns this rental loop for standalone, Sentinel, and Cluster
callers. Route state is a constrained value type: standalone/Sentinel selection uses
`ClientCore`, while Cluster retains its slot/read/ASK route and lazily creates one discovery
scope after the first retirement. Returning the same stopped pool terminates with the original
failure. WATCH and durability batches use these same entry points; durability retains its
fresh-connection requirement. Corrective fences that deliberately pin a captured server do
not follow topology replacements.

The original cancellation token, including an upload's acquisition deadline, passes unchanged
through every rental and reselection. `DedicatedConnectionPool.Recovery` still owns transient
connection retries within a rental; pool lifetime cancellation ends that recovery before the
outer loop selects a replacement. Discovery accounting does not reset at each pool change.

`RespireClient.StreamedUploads` owns standalone/Sentinel and Cluster upload orchestration,
separately from blocking commands. Uploads retain one command deadline across acquisition,
MOVED/ASK, pre-header retirement and maintenance relaxation. Blocking sends retain their
response-timeout exemption and read-role fallback. Both paths use the shared rental helper
and return or discard a lease through the pool that supplied it.

`DedicatedStreamRoute` carries the owner, pool, connection, slot generation and ASK identity
by value. The streaming writer validates it after the first source read and again after ASKING,
immediately before admitting the SET header. A default route permits direct connection sends.
Publication checks remain in `ClientCore` and `ClusterRouter`; a stopped pool is not the only
invalidation signal. Pre-header retries preserve prefetched bytes, while completed redirect
replies reset only replayable sources. One telemetry scope follows each logical upload.

`DedicatedStreamRouteTests` measures construction and validation for standalone, Cluster and
ASK routes without socket work in the measured interval. Warm no-inline loops run in the
shared no-GC boundary; escaping legacy closures provide the positive allocation control.

The shared scenario matrix is covered by these deterministic wire tests:

| Scenario | Standalone / Sentinel | Cluster |
| --- | --- | --- |
| Rental after publication, fresh or idle lease | `MovingReplacesUploadPoolAndDrainsAcceptedUpload` | `RetiredPoolSelectionRetriesBeforeRentAndKeepsItsOwner` |
| Retirement during handshake | `MovingRetriesDedicatedHandshakeRetiredBeforeDispatch` | `DedicatedHandshakeRetriesOnlyRetirement` |
| Publication before old-pool retirement | `SameEndpointPublicationRevalidatesUploadBeforePoolRetirement` | `AskUploadRevalidatesMovingPoolBeforeOldPoolStartsStopping` |
| Explicit disposal during drain | `DisposeAbortsUploadWhileMovedPoolIsRetiring` | `DisposeAbortsUploadWhileMovedPoolIsRetiring` |
| WATCH and durability ownership | `PromotionDoesNotMoveAnExistingWatchedTransaction`, `NewBatchUsesThePromotedGenerationAndDurabilityKeepsOneSocket` | Cluster WATCH and batch durability suites |

`DedicatedLeaseAcquisitionTests` additionally covers same-pool rejection, cancellation during
selection, cancellation before idle rental, disposed owners, and allocation-free warmed rentals
for both route-state implementations with positive allocation controls.

## Correction ownership

Successful drains need no server-side kill: all accepted replies have been consumed. Failed transports with a known Redis client ID retain their `CLIENT KILL` obligation, including identities obtained during interrupted correction bootstrap. Retirement and disposal wait for an in-progress CLIENT ID bootstrap to publish before completing
identity ownership; a reply dequeued before retirement cannot publish an untracked ID afterward.
Multiplexer retirement fences these IDs through an unpooled control connection with the captured network peer address and original TLS name and authentication settings. This connection is never published as a replacement and does not enable client tracking.

A fence failure faults the retirement task and preserves unresolved IDs. Owners must retain the generation while `HasPendingCorrectionFences` is true and retry `FenceRetiredConnectionsAsync()` before releasing its correction ownership. A successful explicit retry clears the obligations; the original retirement task retains its failure. Never treat a faulted retirement task as proof that pending server commands are harmless.

## Explicit disposal

`DisposeAsync()` remains abortive. It rejects selection, cancels initialization and fencing, closes accepted operations even when the peer never replies, and waits for owned transport cleanup. Concurrent disposal callers share cleanup completion. Calling disposal during retirement escalates that retirement; a cancelled fence remains observable to its retirement caller. Full client disposal does not wait indefinitely for graceful replies or an unavailable fencing peer.

Disposal that prevents a required fence faults retirement with an `OperationCanceledException`,
including when disposal happens before fencing starts or retirement is first requested after
disposal. The unresolved client IDs remain visible through `HasPendingCorrectionFences`.
Abortive cleanup alone is never proof of correction ordering. Since a disposed multiplexer
cannot retry fencing, a generation owner that still needs correction guarantees must retain
its ownership and complete fencing before disposal, or arrange that obligation outside the
disposed transport. Retirement after disposal succeeds only when no fence obligations remain.

## Cluster generation ownership

A successful topology publication prunes departed and superseded generations from endpoint,
node-ID, reverse-ID, health-handler, redirect, and dedicated-pool lookup. Newer MOVED and ASK
routes retain their existing version protection. Configured seed addresses remain available for
discovery even without slots. Re-adding a departed address creates a new generation; old cleanup
cannot remove its replacement.

Detached multiplexers drain accepted frames and replies. Their dedicated pools reject new rents
and wait for borrowed operations to return. There is no implicit timeout that aborts accepted
application commands. Failed tracked sockets retain their server-local client IDs and captured
network peers until CLIENT KILL is acknowledged. Each control attempt is bounded by ConnectTimeout;
the Cluster owner retries failed fences with exponential delays from one to 30 seconds, logs
failed attempts at Debug level (Warning once the delay reaches 30 seconds, including the current
retiring-generation count), and retains the generation until success or explicit client
disposal. Client disposal aborts active and detached transports, borrowed connections, and control
attempts before waiting for cleanup.
An expired control-attempt deadline surfaces as `RespireTimeoutException` for `CLIENT KILL`;
caller cancellation and explicit disposal retain their cancellation behavior. A failed pool
drain faults generation retirement and retains ownership for disposal instead of reporting success.
A permanently unreachable peer therefore keeps its generation alive until explicit client disposal.
Failures before the multiplexer finishes drain and identity collection fault generation retirement;
an empty set of pending fence IDs cannot make those failures successful. MOVED/ASK connection setup
can re-resolve a generation retired by concurrent topology publication, with bounded retries and
caller cancellation. This happens before sending the redirected command and never replays accepted work.

The router's `_nodesGate` protects lookup and ownership changes. Snapshot the owned objects under
that gate, then release it before renting, draining, disposing, awaiting, or invoking lifecycle
callbacks. Node and pool lifecycle locks must be released before callbacks acquire `_nodesGate`.
Observer installation and peer revalidation occur together under the router gate; they do not
take a node or pool lifecycle lock.
Routing and correction ownership intentionally share that gate so topology detachment, pool reservations,
and identity publication remain atomic. `WaitForRetirementAsync` observes detached generation completion;
late correction reservations have separate lifetimes. Explicit disposal snapshots every owned pool and
awaits each pool's shared abortive cleanup, including pools already being retired in the background.

Correction pools share live multiplexer/peer/TLS identities, including replacement sockets on the
same peer. A changed peer gets a separate pool; obsolete entries detach from lookup. A correction
reservation protects asynchronous rent through command completion, so topology cleanup cannot
close a pool between selection and rent. Detached pools close after their reservations return.
A late fence for an already successfully drained socket needs no server command. Other late
corrections can create a temporary client-owned pool for the original captured peer even
after routing ownership has been released. They never resolve a new server through the old hostname.
Idempotent script corrections arriving after retirement wait for drain and fence completion before
executing through that original peer. TLS authentication keeps the original configured name.
After an owner successfully retries a failed fence, late corrections proceed even though the shared
retirement task retains its original failure. Errors before drain and identity collection complete
still propagate; an empty fence set alone is not proof of completed retirement. Unexpected
generation cleanup failures are logged at Warning level and remain observable to retirement/disposal callers.
These unexpected cleanup failures are terminal for that generation's retirement task; they do not
enter the retry loop for unacknowledged fences. The generation remains owned until explicit client
disposal aborts its transports and observes the original failure. Retrying an already faulted,
memoized transport cleanup task cannot restart cleanup or prove a successful drain.

## Dedicated pool ownership

`DedicatedPoolLedger` keeps the dedicated pools owned by `ClientCore`, `ClusterRouter`,
and each Sentinel generation. Current route lookup remains with those owners. Publication
adds a replacement before exposing it, while the previous pool stays in the ledger until
its graceful retirement succeeds. A failed retirement remains owned for explicit disposal.
Successful cleanup removes the pool; the ledger does not retain completed pool history.

Each ledger shares its owner's publication gate. This makes shutdown snapshots wait for
publications already in progress. Owners prevent new publication after shutdown starts.
The ledger snapshots membership under that gate, then starts retirement or disposal outside
it. Explicit disposal starts every owned pool's abort before awaiting any completion, so
one cleanup failure cannot prevent another borrowed lease from being aborted. Concurrent
retirement and disposal use the pool's existing shared cleanup task.

Client shutdown observes each owner separately in disposal order. A pool, subscription hub,
or router failure cannot skip a later owner. A single failure is rethrown unchanged; multiple
owner failures are preserved in an `AggregateException` after cleanup finishes.

This bookkeeping does not participate in healthy command dispatch or lease acquisition.
Route-version validation, ASK target selection, MOVING publication, cancellation deadlines,
and accepted-command drain rules remain with their existing owners.

## Retirement diagnostics

`RespireClient.GetClusterRetirementSnapshot()` captures aggregate retained-generation
counts, monotonic oldest age, published fence obligations, transport-drain state,
unexpected cleanup failures, and dedicated operation-pool leases/acquisitions. It adds
no command-path counters or I/O. The snapshot owns only scalar observations; retaining
it cannot retain a generation, connection, pool, router, or exception.

Capture uses `_nodesGate` before reading a dedicated pool's `_gate`. Pool code never
calls back into the router while holding `_gate`, and completion continuations are
asynchronous. Fence counts use the existing concurrent identity set. Membership is
stable during capture, but independently changing transport counters are observational,
not a completion barrier. Completed generations are not retained for history. Fresh
capture after disposal begins throws; existing snapshots remain valid.

See the [observability guide](../website/docs/integrations/observability.md#cluster-retirement)
for category overlap and the exact scope of each counter. No retention-age threshold can
abandon an owed fence or establish correction ordering. This diagnostic API leaves retry
backoff, ownership, and explicit-disposal semantics unchanged.
