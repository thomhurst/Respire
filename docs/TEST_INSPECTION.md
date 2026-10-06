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

`TestInspectionArchitectureTests` checks compiled factory/view metadata and rejects
the four legacy names on both supported frameworks. The single-target analyzer-test
host adds `TestInspectionSourceArchitectureTests`, reading embedded production
source with the net8.0 and net10.0 preprocessor symbols:

- `TestInspectionOwnerSurface.txt` is an explicitly reviewed inventory of directly
  declared public, internal, and protected member headers on the four owners. A new
  accessor or overload fails the inventory comparison regardless of its name.
  Existing operational members remain permitted. When adding or changing an
  operational declaration, review and update its inventory entry deliberately;
  do not accept a new test-only accessor into that inventory. Put inspection state
  in the designated nested view instead. Implementation bodies, initializers, and
  private-only helpers are not inventoried.
- `InspectForTests` is a reserved factory name throughout production source. Its
  identifier references, including direct calls, conditional calls, and method
  groups, are forbidden there. Declarations and deliberate `nameof` metadata
  references remain permitted. Friend-test source is outside the production
  resource set and may call the factories. The rule uses this exact reserved name,
  not guesses about names that sound like testing or inspection.

Positive controls cover a renamed accessor, a new operational overload, production
calls, and a method group. Negative controls cover reviewed operations, private
implementation changes, another type with the same simple owner name, friend-test
consumption, metadata references, comments, and string literals.

These are architecture checks, not a lifetime or ownership analysis. They cannot
detect repurposing an existing inventoried member, reflection-based state access,
or code hidden behind other preprocessor configurations. Nested implementation
member bodies are not part of the owner-header inventory. Returned references and
copies still require the boundaries in the consumer table above. Update that table
when adding consumers or inspection members; passing a guard does not establish
quiescence, source lifetime, or ownership.
