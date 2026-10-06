# Queued connection policy

`QueuedConnectionPolicy` is a non-owning, immutable view of a batch or transaction's
existing import-session and WATCH owners. Queues recreate it when making a policy
decision. They do not allocate another owner, retain a policy per command, or transfer
the lease to the policy. The original queue/session remains responsible for disposal.

Ordinary queues may recover **rejected** commands using the existing cluster routing
helpers. This is not permission to replay a command accepted by Redis. WATCH leases
and import sessions carry connection-local state and must never follow that recovery
path. A watched redirect instead requests a fresh WATCH/read/queue/EXEC attempt.

## Execution and recovery inventory

| Path | Policy decision and existing safety boundary |
| --- | --- |
| `RespireBatch.TryExecuteAsync` | Select the import connection when pinned. Only ordinary batches may enter cluster slot grouping. Import batches use ordered pinned admission, not client routing. |
| Batch cluster retirement | `CompleteClusterSendAsync` consults the owning batch's replay eligibility before resuming the individual rejected command. Other entries may already have executed and are never replayed together. |
| Batch cluster read-role fallback | Consult replay eligibility before the existing read-role rejection classifier. Keep cursor affinity and per-slot FIFO routing unchanged. |
| Batch cluster MOVED/ASK/READONLY recovery | Consult replay eligibility and the existing cluster recovery classifier before resuming the rejected command. |
| `RespireTransactionBase.CommitCoreAsync` | Select the import or WATCH connection before ordinary acquisition. Import transactions confirm MULTI separately on their original connection. Ordinary and watched transactions retain their existing MULTI/EXEC framing. |
| Transaction retirement | Consult policy and the cluster retry budget. Only an ordinary, unwatched transaction may replace a connection after rejection of its complete frame before admission. |
| Transaction MOVED/READONLY/other recoverable rejection | WATCH state requests `RespireTransactionRetryException`; import state reports the original rejection; only an ordinary transaction follows a replacement route. Existing redirect limits and malformed-redirect checks remain in force. |
| Transaction ASK | A watched transaction requests a fresh attempt; an import transaction reports ASK without replay. Ordinary transactions retain the existing unsupported-ASK error. |
| Import batch uncertain completion | Expire the session through policy and fault entries not yet admitted. Confirmed server errors may preserve prepared fieldsets; disconnect, cancellation after admission, redirects, READONLY, and protocol/conversion failures expire them. |
| Import transaction uncertain completion | Apply the same expiration classifier. If MULTI started and server transaction state is not confirmed cleared, force expiration even for an otherwise recoverable server error. Preserve proven-not-submitted cancellation/timeout behavior. |
| Immediate import operation | Use the same policy expiration classifier. The send remains pinned with streaming rerouting disabled. |
| Durability batch | Policy rejects execution from an import session. Ordinary durability execution rents a fresh dedicated connection and sends directly, without cluster recovery handlers. Writes and WAIT/WAITAOF never replay; the connection is discarded afterward. |
| `RunTransactionAsync` helpers | Repeat only a false WATCH-conflict result from commit, with a new transaction and callback. They do not replay an existing queue or catch routing/transport failures. Import sessions cannot enter this helper. |

The policy does not own replies, buffers, cache fences, pending completion, credential
sequence leases, or WATCH pool return. Those responsibilities stay at their original
execution boundaries. Import disconnect tests cover immediate, batch, and transaction
execution before/after server execution with RESP2 and RESP3. Cancellation, retirement,
MOVED, ASK, READONLY, queue errors, and malformed EXEC replies cover the other policy
boundaries; ordinary batch/transaction and retirement suites remain compatibility checks.
