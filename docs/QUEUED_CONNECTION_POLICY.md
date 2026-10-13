# Queued connection policy

Never replay a command Redis may have accepted. Only ordinary queues may recover **rejected**
commands through the Cluster routing helpers. WATCH leases and import sessions carry
connection-local state and never take that recovery path; a watched redirect requests a fresh
WATCH/read/queue/EXEC attempt instead.

`QueuedConnectionPolicy` is a non-owning, immutable view of a batch or transaction's existing
import-session and WATCH owners. Queues recreate it for each decision. It allocates no owner, is not
kept per command and takes no lease; the original queue or session still disposes.

Separately, an opt-in `RespireOptions.RetryPolicy` can resend a whole standalone or Sentinel batch,
or an unwatched transaction, when every member's retry category allows it. That whole-unit retry also
excludes import sessions, WATCH leases and Cluster batches.

## Execution and recovery inventory

| Path | Policy decision and safety boundary |
| --- | --- |
| `RespireBatch.TryExecuteAsync` | Select the import connection when pinned. Only ordinary batches enter Cluster slot grouping; import batches use ordered pinned admission. |
| Batch Cluster retirement | `CompleteClusterSendAsync` checks the batch's replay eligibility before resuming the one rejected command. Other entries may have run and are never replayed together. |
| Batch Cluster read-role fallback | Check replay eligibility before the read-role rejection classifier. Cursor affinity and per-slot FIFO routing are unchanged. |
| Batch Cluster MOVED/ASK/READONLY recovery | Check replay eligibility and the Cluster recovery classifier before resuming the rejected command. |
| `RespireTransactionBase.CommitCoreAsync` | Select the import or WATCH connection before ordinary acquisition. Import transactions confirm MULTI separately on their original connection. Ordinary and watched transactions keep MULTI/EXEC framing. |
| Transaction retirement | Check policy and the Cluster retry budget. Only an ordinary, unwatched transaction may replace a connection, and only after its complete frame was rejected before admission. |
| Transaction MOVED/READONLY/other recoverable rejection | WATCH state requests `RespireTransactionRetryException`; import state reports the original rejection; only an ordinary transaction follows a replacement route. Redirect limits and malformed-redirect checks still apply. |
| Transaction ASK | Watched: fresh attempt. Import: report ASK without replay. Ordinary: the unsupported-ASK error. |
| Import batch uncertain completion | Expire the session through policy and fault entries not yet admitted. Confirmed server errors may keep prepared fieldsets; disconnect, cancellation after admission, redirects, READONLY and protocol/conversion failures expire them. |
| Import transaction uncertain completion | Same expiration classifier. If MULTI started and server transaction state is not confirmed cleared, force expiration even for a recoverable server error. Keep proven-not-submitted cancellation/timeout behavior. |
| Immediate import operation | Same expiration classifier. The send stays pinned with streaming rerouting disabled. |
| Durability batch | Policy rejects execution from an import session. Ordinary execution rents a fresh dedicated connection and sends directly, without Cluster recovery. Writes and WAIT/WAITAOF never replay; the connection is discarded afterward. |
| `RunTransactionAsync` helpers | Each attempt runs a new watched transaction and callback; a queue is never replayed. A WATCH conflict repeats up to `RespireTransactionRetryOptions.MaxAttempts`. Transient failures repeat only when `RespireOptions.RetryPolicy` is set and every queued command's category allows it, under a separate budget. Timeouts, cancellation and Cluster routing rejections propagate. Import sessions cannot use this helper. |

The policy does not own replies, buffers, cache fences, pending completion, credential sequence leases
or WATCH pool return. Those stay at their original execution boundaries.

## Tests

- Import disconnect tests: immediate, batch and transaction execution, before and after server
  execution, RESP2 and RESP3.
- Policy boundaries: cancellation, retirement, MOVED, ASK, READONLY, queue errors and malformed EXEC
  replies.
- Ordinary batch, transaction and retirement suites remain compatibility checks.
