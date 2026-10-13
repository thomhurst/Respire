# Typed friend-test inspection

Tests read private state of four owners only through `InspectForTests()`. Do not add test-only
accessors, overloads or public hooks to the owners. Put new inspection state in the owner's
nested view, add its consumers to the table below, and follow each member's boundary.

| Owner | View |
| --- | --- |
| `RespireConnection` | `RespireConnection.TestInspection` |
| `PendingResponse` | `PendingResponse.TestInspection` |
| `ClientSideCacheCoordinator` | `ClientSideCacheCoordinator.TestInspection` |
| `RespireTransactionBase` | `RespireTransactionBase.TestInspection` |

Each owner declares the factory in a `.TestInspection.cs` partial. It returns a nested internal
`readonly ref struct TestInspection` that reads private state directly, with no allocation,
delegates, leases or ownership transfer. `RespireTelemetry.ErrorObservation` has a similar view in
`RespireTelemetry.Errors.cs`, but the architecture guards cover only the four owners.
`InternalsVisibleTo` stays the boundary. Production coordination uses ordinary owner methods.

The compiler stops the view itself from being boxed, captured, stored on the heap or held across
`await`. References and copies it returns can escape; the tests must apply the rules below to
them, including after the view expires.

## Consumers and boundaries

| View member | Consumers | Required boundary |
| --- | --- | --- |
| `RespireTelemetry.ErrorObservation.TestInspection.StorageIdentity` | `ErrorMetricsTests.LeaseGeneration`, `DispatchResponseObservationTests` | Copy the identity while the observation is live; use it only for identity comparison. Never mutate or dispose the storage. Dispatch reuse tests hold every later rental until the source identity reappears, then exercise a stale generation and drain the native reply. |
| `RespireConnection.TestInspection.Inflight` | `TransactionDeadlineTests`, `HashImportTests`, `ClusterReadyReplyTests`, `NativeLockCommandTests`, `TimeoutDiagnosticsTests`, `DeadlinePublicationTests`, `CommandWriteOutcomeTests` | Pause admission and reply consumption before reading slots (park the writer, suppress replies, or inspect synchronously inside `OnAccepted` before the flush). `Count` is an observation, not a reservation. Peeking takes no source reference and no enqueue/dequeue ownership. Inspect a reused incarnation only while it is rented. Timeout tests disable the timer before completing the source through its timeout transition. |
| `RespireConnection.TestInspection.ResumedScalarCount` (Debug only) | `AggregateProgressWireTests` | Wait for the stream's next read request after the incomplete prefix, then copy the count synchronously before supplying the next segment. Absent from Release. |
| `PendingResponse.TestInspection.RegisteredCancellationToken` | `TransactionDeadlineTests` | Inspect after registration and admission finish, while the source is live. Never keep a pooled source after it can be recycled. |
| `ClientSideCacheCoordinator.TestInspection.SharedReadGate` | `TransactionDeadlineTests` | Hold an `EnterScope` lease to control the barrier. Release it on the owning thread; do not await while holding it or dispose the gate. |
| `ClientSideCacheCoordinator.TestInspection.PendingQueryDependencyCount` | `ClientCacheQueryDependencyTests`, `ClientCacheQueryDependencyWireTests` | Snapshot under the query gate. Use completion/cancellation barriers before asserting retirement. Grants no registration or publication ownership. |
| `ClientSideCacheCoordinator.TestInspection.IdleQueryStorage` | `ClientCacheQueryDependencyTests` | Copy under the query gate. Join completion before checking bounds and clearing. Grants no rent/return ownership. |
| `ClientSideCacheCoordinator.TestInspection.ActiveMutationCount` | `ClientCacheMutationLifetimeTests`, `ClientSideCacheTests`, `CacheAsideTests` | Volatile snapshot. Park admission or native reply retirement before asserting an active fence; wait for zero only after releasing the reply. Caller completion does not prove native retirement. |
| `ClientSideCacheCoordinator.TestInspection.MutationWriterStorage` | `ClientCacheMutationLifetimeTests` | Copy under the query gate. Complete the mutation before checking the idle bound. |
| `RespireTransactionBase.TestInspection.WatchConnection` | `TransactionDeadlineTests`, `TimeoutDiagnosticsTests` | The transaction owns the pinned lease. Do not dispose or return the connection or race transaction disposal. Null without WATCH. |

Never hold a view across an `await`. Preserve the tests' barriers, deadlines, cancellation, import
and FIFO assertions.

## Architecture guards

`TestInspectionArchitectureTests` checks compiled factory and view metadata and rejects the four
legacy names on both frameworks. The analyzer-test host compiles production source once per target
framework, using the `DefineConstants` the SDK evaluates for each, so new frameworks and symbols
are covered automatically. It needs no checkout at runtime. Friend-test source and `bin`/`obj`
paths are excluded from the scanned resources.

The guards enforce three rules:

1. **Owner surface inventory.** `TestInspectionOwnerSurface.txt` lists every source-declared member
   signature of the four owners, their production subclasses (found semantically, including
   indirect, aliased and cross-file ones) and accessible nested types, with a reviewed count per
   type. Any new accessor or overload fails, whatever its name.
2. **Reserved factory name.** Production source must not reference `InspectForTests`: no calls,
   conditional calls or method groups. Declarations and real `nameof` references are allowed;
   escaped `@nameof(...)` and local functions named `nameof` are not exempt.
3. **View construction.** Only the owner's internal, instance, nongeneric, parameterless
   `InspectForTests()` returning that exact view may construct it. Explicit, qualified, aliased
   and target-typed `new` are all caught.

### Inventory keys

| Included | Not included |
| --- | --- |
| Owner, member kind, name, generic arity | Bodies, accessor style, initializers |
| Parameter types and passing modifiers | Parameter names |
| Result or value type | Attributes, generic constraints |
| Explicit interface names, primary constructor parameter types | Private-only helpers |
| Property/indexer accessibility and accessor kinds (`get`, `set`, `init`) | Compiler-synthesized members |
| `readonly` and `ref` on nested types | |

Instance members of the four views are allowed. Static members and accessible nested types of the
views need review. An owner's primary constructor shares the ordinary constructor key. All four
owners must be present as classes in each configuration; other kinds fail explicitly.

### When the inventory check fails

The failure lists the exact unreviewed and removed signatures.

1. Confirm the change is an approved operational change, not a test accessor.
2. Copy each approved unreviewed line into `TestInspectionOwnerSurface.txt` and remove the obsolete
   line. Keep the legacy test-hook comments (they are not precedent for new hooks).
3. Update the type's reviewed count.
4. Rerun the check for both frameworks.

The guard never regenerates the inventory. Never accept an inspection bypass to make it pass.

## Limits

These are architecture checks, not lifetime or ownership analysis. They cannot detect a repurposed
inventoried member, reflection-based access, or code behind symbols outside the core project's
evaluated configurations. The syntax checks use the analyzer host's preview language mode and do
not replace production compilation. Passing a guard does not prove quiescence, source lifetime or
ownership.
