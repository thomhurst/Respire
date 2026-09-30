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
failed attempts at Debug level, and retains the generation until success or explicit client
disposal. Client disposal aborts active and detached transports, borrowed connections, and control
attempts before waiting for cleanup.
An expired control-attempt deadline surfaces as `RespireTimeoutException` for `CLIENT KILL`;
caller cancellation and explicit disposal retain their cancellation behavior. A failed pool
drain faults generation retirement and retains ownership for disposal instead of reporting success.
A permanently unreachable peer therefore keeps its generation alive until explicit client disposal.

The router's `_nodesGate` protects lookup and ownership changes. Snapshot the owned objects under
that gate, then release it before renting, draining, disposing, awaiting, or invoking lifecycle
callbacks. Node and pool lifecycle locks must be released before callbacks acquire `_nodesGate`.
Observer installation and peer revalidation occur together under the router gate; they do not
take a node or pool lifecycle lock.

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
