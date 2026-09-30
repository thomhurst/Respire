# Internal transport retirement

`RespireConnection.RetireAsync()` rejects new command acceptance under the same write gate that publishes serialized frames to the response FIFO. A command already accepted continues writing its complete frame and consumes its reply, even if its caller has cancelled. An operation waiting for FIFO capacity has not been accepted and wakes immediately with `RespireConnectionRetiredException`.

The retirement task is shared across callers. It completes after accepted writes, response parsing, scheduled response completions, and socket cleanup finish. `IsAcceptingCommands` becomes false immediately; physical `IsConnected` remains true while draining. No cancellation interrupts a partially written frame. An ordinary socket failure or configured timeout retains its existing behavior.

`RespireConnectionMultiplexer.RetireAsync()` stops connection selection and background reconnects, cancels pending handshakes, and prevents initialization or reconnect publication after retirement. It retires existing transports immediately, waits for unpublished connection cleanup, and then awaits their drain tasks. Lifecycle notifications are queued in transition order and delivered outside lifecycle locks.

## Retry boundary

`RespireConnectionRetiredException` is internal and distinct from an ambiguous `RespireConnectionException`. A router may select the current generation and retry only an operation rejected with the retired exception: that operation did not enqueue any bytes. It must not retry an accepted command merely because topology changed. This primitive does not itself change Cluster routing; generation ownership and routing integration are tracked by #466.

## Correction ownership

Successful drains need no server-side kill: all accepted replies have been consumed. Failed transports with a known Redis client ID retain their `CLIENT KILL` obligation, including identities obtained during interrupted correction bootstrap. Multiplexer retirement fences these IDs through an unpooled control connection with the original endpoint and authentication settings. This connection is never published as a replacement and does not enable client tracking.

A fence failure faults the retirement task and preserves unresolved IDs. Owners must retain the generation while `HasPendingCorrectionFences` is true and retry `FenceRetiredConnectionsAsync()` before releasing its correction ownership. A successful explicit retry clears the obligations; the original retirement task retains its failure. Never treat a faulted retirement task as proof that pending server commands are harmless.

## Explicit disposal

`DisposeAsync()` remains abortive. It rejects selection, cancels initialization and fencing, closes accepted operations even when the peer never replies, and waits for owned transport cleanup. Concurrent disposal callers share cleanup completion. Calling disposal during retirement escalates that retirement; a cancelled fence remains observable to its retirement caller. Full client disposal does not wait indefinitely for graceful replies or an unavailable fencing peer.
