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
host separates inventory, factory-use and resource-validation tests, sharing
`TestInspectionSource` helpers. At build time the harness queries the core production
project's declared target frameworks and the SDK's evaluated `DefineConstants` for
each framework. It embeds that configuration alongside the source, so new frameworks
and conditional compilation symbols enter both production guards automatically.
The test assembly does not require a checkout at runtime. Each production guard
builds one compilation per configuration and reuses its semantic models across files.
Runtime dependency references support binding; designated views must resolve from
the embedded source compilation itself, so missing views cannot pass vacuously.
The resource check rejects friend-test source and generated `bin`/`obj` paths:

- `TestInspectionOwnerSurface.txt` explicitly reviews the source-declared member
  signatures of the four owners, their production subclasses and accessible nested
  operational types. Signatures are grouped by declaring type with a reviewed count
  checked for each group, so large surface additions are visible in review.
  It includes public, internal, protected, and explicit interface members, recursively
  following accessible nested types. Semantic inheritance discovery also follows
  indirect, aliased and cross-file subclasses, which can expose protected owner state.
  A new accessor or overload fails comparison regardless of its name. Instance
  members of the four designated borrowed views
  remain permitted; their static members and accessible nested types require review.
  Existing operational members remain permitted. When adding or changing an
  operational declaration, review and update its inventory entry deliberately;
  update the corresponding reviewed type count as well. Do not accept a new test-only
  accessor into that inventory. Put inspection state in the designated nested view
  instead. Keys contain the owner, member kind, name,
  generic arity, parameter types and passing modifiers, and result or value type.
  Explicit interface names and primary constructor parameter types are included.
  Properties and indexers include accessibility and accessor kind/accessibility
  (`get`, `set` or `init`). Expression-bodied getters and block getters share a key;
  adding a setter or changing its accessibility changes the key.
  An owner's primary constructor uses the same key as an ordinary constructor
  overload; nested-type primary constructors remain part of their type key.
  Nested-type keys preserve `readonly` and `ref`, so weakening a borrowed view's
  mutability or lifetime restriction requires review. All four qualified owners
  must be present as classes in each framework configuration; unsupported owner
  kinds fail explicitly rather than disappearing from the scan.
  Bodies, accessor style, initializers, parameter names, attributes,
  generic constraints, and private-only helpers are not inventoried. Known legacy
  test hooks are marked explicitly; their presence is not precedent for new hooks.
  Inventory failures list exact unreviewed and removed signatures and point here.
  For an approved operational change, copy each approved unreviewed line into the
  inventory and remove its obsolete line, retaining the test-hook comments. Rerun
  both framework-symbol checks. Do not accept an inspection bypass merely to make
  the test pass; the guard never overwrites or regenerates the inventory automatically.
- `InspectForTests` is a reserved factory name throughout production source. Its
  identifier references, including direct calls, conditional calls, and method
  groups, are forbidden there. Declarations and deliberate `nameof` metadata
  references remain permitted. Roslyn's `INameOfOperation` distinguishes metadata
  from ordinary methods or local functions named `nameof`, including helpers in
  another partial source file. Escaped `@nameof(...)` calls receive no exemption.
  Friend-test source is outside the production resource set and may call the
  factories. The rule uses this exact reserved name, not guesses about names that
  sound like testing or inspection.
- Direct construction of the four designated nested views is forbidden outside
  their own owner's internal, instance, nongeneric, parameterless `InspectForTests()`
  method returning that exact view. Same-name overloads and nested methods receive
  no exemption. Semantic type binding covers explicit,
  fully qualified, aliased and target-typed `new` expressions. Unrelated types with
  the same simple name are permitted; the factory's own construction remains valid.

Positive controls cover a renamed accessor, a new operational overload, owner
primary constructors (including abstract owners and default internal visibility),
explicit interface methods/properties/indexers/events, production calls, a method group,
and escaped, unescaped, local-function, and cross-file `nameof` helpers. Additional
controls cover direct view construction, removed `readonly`/`ref` modifiers,
missing qualified owners, unsupported owner declaration kinds, writable
properties/indexers, property accessibility, nested helper/static-view bypasses,
same-name factory overloads, cross-file construction, missing views and privileged
direct/indirect derived accessors.
Negative controls cover permitted borrowed instance members, reviewed operations,
body and accessor style changes, parameter renames, attributes, constraints, private
implementation changes, another type with the same simple owner name, metadata
references, comments, and string literals. A non-empty resource-set check verifies
that friend-test source is excluded; missing resources report their names.

These are architecture checks, not a lifetime or ownership analysis. They cannot
detect repurposing an existing inventoried member, reflection-based state access,
or code hidden behind configurations outside the core project's evaluated framework
and build-configuration symbols. The Roslyn syntax checks use the pinned analyzer
host's preview language mode; they are not a replacement for production compilation.
Implementation bodies and compiler-synthesized members are not part of the
source-declared inventory.
Returned references and copies still require the boundaries in the consumer table above. Update that table
when adding consumers or inspection members; passing a guard does not establish
quiescence, source lifetime, or ownership.
