# Typed friend-test inspection

These four owners expose `InspectForTests()` through `.TestInspection.cs` partials,
returning nested internal `readonly ref struct TestInspection` views. They read
private state directly without allocation, delegates, leases, or ownership transfer.
Existing `InternalsVisibleTo` declarations remain the boundary; no public hooks
are added. Production coordination must use ordinary owner methods.

The compiler prevents boxing, capture, heap storage, and use across `await` for
the view itself. Returned references and value copies can escape; their borrowing
and synchronization rules remain test obligations, including after the view expires.

| View member | Consumer inventory | Required boundary |
| --- | --- | --- |
| `RespireConnection.TestInspection.Inflight` | `TransactionDeadlineTests`, `HashImportTests` | Pause admission and reply consumption before reading slots. `Count` is only an observation, not an admission reservation. Peeking acquires no source reference and grants no enqueue/dequeue ownership. |
| `PendingResponse.TestInspection.RegisteredCancellationToken` | `TransactionDeadlineTests` | Inspect after registration/admission has finished, while the source is live and registration/disposal cannot race. Never retain a pooled source after completion consumption and reply drainage allow recycling. |
| `ClientSideCacheCoordinator.TestInspection.SharedReadGate` | `TransactionDeadlineTests` | Hold an `EnterScope` lease to control the shared-read barrier or inspect protected state. Release that lease on the owning thread; do not await while holding it or dispose the borrowed gate. |
| `RespireTransactionBase.TestInspection.WatchConnection` | `TransactionDeadlineTests` | The transaction owns the pinned lease. Do not dispose or return the connection, or race transaction disposal. A transaction without WATCH returns null. |

The table lists every current state consumer of these views. Update it when adding
one; put new inspection members in the owner's view and document their boundaries.
Preserve the tests' barriers, deadlines, cancellation, import, and FIFO assertions.

`TestInspectionArchitectureTests` checks factory/view metadata and rejects the four
legacy names, with a synthetic positive control. It cannot detect differently named
accessors or enforce returned-reference lifetimes. Ordinary operational members
remain valid. Broader static enforcement is tracked in
[#1045](https://github.com/thomhurst/Respire/issues/1045).
