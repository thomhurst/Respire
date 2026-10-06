# Typed friend-test inspection

Use `owner.InspectForTests()` for the connection, pending-response, cache-barrier,
and transaction inspection introduced by the transaction deadline tests. Each
owner defines the same pattern in a separate `.TestInspection.cs` partial:
an internal factory returning a nested `readonly ref struct TestInspection`.
The view reads its owner's private state directly. It does not create a delegate,
box, allocate an object, acquire a lease, or transfer ownership.

The existing `InternalsVisibleTo` declarations remain the visibility boundary.
No public hooks are added. Production coordination must use the owner's ordinary
methods, not this test surface. A temporary view works in an async test; a view
cannot be boxed, captured in a lambda, stored in a heap object, or retained across
an `await`. References returned by the view are still borrowed and require the
following synchronization even after the temporary view has gone out of scope.

| View member | Consumer inventory | Required boundary |
| --- | --- | --- |
| `RespireConnection.TestInspection.Inflight` | `TransactionDeadlineTests`, `HashImportTests` | Pause admission and reply consumption before reading slots. `Count` is only an observation, not an admission reservation. Peeking acquires no source reference and grants no enqueue/dequeue ownership. |
| `PendingResponse.TestInspection.RegisteredCancellationToken` | `TransactionDeadlineTests` | Inspect after registration/admission has finished, while the source is live and registration/disposal cannot race. Never retain a pooled source after completion consumption and reply drainage allow recycling. |
| `ClientSideCacheCoordinator.TestInspection.SharedReadGate` | `TransactionDeadlineTests` | Hold an `EnterScope` lease to control the shared-read barrier or inspect protected state. Release that lease on the owning thread; do not await while holding it or dispose the borrowed gate. |
| `RespireTransactionBase.TestInspection.WatchConnection` | `TransactionDeadlineTests` | The transaction owns the pinned lease. Do not dispose or return the connection, or race transaction disposal. A transaction without WATCH returns null. |

The four former direct accessors are removed. Existing tests still establish
their server/worker barriers, inspect the same source and token, and assert the
same deadline, caller-cancellation, import preservation, and FIFO behavior.
Future inspection of these owners belongs in the corresponding typed view,
with its lifetime and synchronization requirements documented next to the member.
