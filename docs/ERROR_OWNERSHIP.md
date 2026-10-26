# Error ownership for contributors

Every caller-visible failure is counted in `redis.client.errors` exactly once, by one final
owner per logical caller. When you add or change a public route, declare its owner in the
source-adjacent `<source-file>.cs.ownership.json` file in the same change, and run
`CommandRouteOwnershipTests` on net8.0 and net10.0.

For the user-facing metrics, see [Observability](../website/docs/integrations/observability.md).

## Rules

1. **One final publication per caller.** Helpers, borrowers, transport events and cleanup
   observations stay separate from the caller's final publication. Returned per-node failures
   must not also become a duplicate parent failure.
2. **Start the boundary first.** Declare the owner before argument validation, disposal checks,
   cancellation checks, key mapping, serialization, command construction or setup parsing can
   fail.
3. **Publish after cleanup.** The final error is published once, after response inspection,
   conversion, cleanup and caller-reference release. Fan-out and Cluster-wide routes join every
   target (and finish cache fences, discovery and connection cleanup) before publishing.
4. **Retries borrow.** Transparent redirects (MOVED/ASK, `ASKING`), connection retries, retired
   connection handoffs, capacity waits, Sentinel replacement, capability probes and fallbacks
   borrow the caller's owner. A recovered failure is internal. A failure returned to the caller
   counts once with its full retry count.
5. **Preserve the exception.** Keep the original exception identity, its cancellation token and
   canceled task status. Rethrow with `throw;`. If cleanup also fails, the original error wins;
   a cleanup failure with no earlier error is the caller's final failure.
6. **Successful paths stay free.** Keep a default `ErrorObservation.FinalOwner` on success. Do
   not rent error observation storage, add a response wrapper or add production reflection.
   Preserve native transport references, deferred-result contracts and allocation tests.
7. **Keep the guard test-only.** The declarations specify required ownership. They do not prove
   runtime instrumentation or correct lease passing, so route-family tests must also cover final
   publication, retries and original exception/cancellation behavior.

## Ownership declarations

The guard treats every public method on a public core type as a route, including synchronous
methods on new types. A new method fails until it is classified. Declarations pin public source
signatures, not where telemetry calls sit.

| Key | Meaning |
| --- | --- |
| `Surfaces` | Maps each public signature to an executable final owner. Explain the full lifetime in `Contract`. |
| `OwnerType` | Implementation type that has the same signature. |
| `Overrides` | A different complete source member, when delegation changes the signature. |
| `AdditionalOwnerTypes` | Alternative implementations, such as batch and transaction implementations of one queue interface. |
| `NonRoutes` | Local value operations and helpers, with a reason for each signature. |
| `Boundaries` | Non-public branches, by complete source member, `Role`, `Owner` and `Contract`. |

Boundary roles:

| Role | Use for | Owner |
| --- | --- | --- |
| `final` | A caller's final inspection | Itself |
| `helper` | Delegated work | Names the enclosing owner |
| `borrower` | Shared attempts or retries | Names the enclosing owner |
| `internal` | An observation with no caller final failure | None |

A shared helper's owner anchor identifies the relevant dispatch lifetime. It is not a lease
shared by unrelated callers; each surface keeps its own logical caller.

Signature format:

- Generic arity is a backtick plus the parameter count.
- Omit parameter names and defaults. Keep parameter types, modifiers and return types.
- Explicit interface implementations keep their interface-qualified signature.

Owner validity:

- An interface declaration cannot be a final owner, except an executable default interface method.
- A same-signature owner needs a public executable method of the same static or instance kind,
  and must list the contract in its source base types (inherited and partial declarations count).
- A `new` hiding method owns the contract only if its type re-lists that interface.
- A partial route's declaration can sit beside its defining or implementing file.

The guard scans the core library (including catalog dispatch) and the distributed-cache
implementation, per target framework. An owner and its interface contract must exist on the same
target; a body in another framework branch does not count. Target-specific declarations apply
only where the route is public. An extension package needs its own declarations when it comes
into scope.

## Lease API

| Call | Rule |
| --- | --- |
| `StartFailure` | Call only after the first failure or handled retry. Complete the lease in `finally`. |
| `PublishFinal` | Records at most one final failure per generation; later calls return `false`. Observes the exception without consuming or rethrowing it. Closes retry reporting and new borrowing. |
| `Borrow` | Gives a helper a separate lease. Borrowers can borrow again and complete their own lease, but cannot publish a final failure. Borrow before final publication or completion. |
| `RecordHandled` | Adds one handled retry to the shared count. The final failure captures the total. |

- Default, completed, closed or stale leases are inert in every build: `RecordHandled` and
  `PublishFinal` return `false` without a metric, and `Borrow` returns an inert borrower that
  cannot recover earlier retry history.
- Copying a lease value shares its completion right. Repeated completion is harmless, so cleanup
  in `finally` cannot replace the original failure.
- Storage returns to the pool only after the owner and every distinct borrower complete. Owner
  completion does not complete a live borrower.
- Do not keep completed leases for later asynchronous work.
- Retry counts saturate at `int.MaxValue`. Zero or negative additions do not reduce them.
- Exporters run outside the ownership gate, so concurrent retry events can arrive out of order
  with exact counts. Exporter exceptions cannot replace a failure.

## Route-specific lifetimes

| Route | Boundary rules |
| --- | --- |
| Native raw, typed, string and byte dispatch | Immediately admitted ordinary standalone dispatch returns the native response source, whose inspection owns final publication. Cache coordination, circuits, Sentinel, Cluster, replica reads, streaming, scripts, activity tracing, raw duration metrics, capacity waits and retirement recovery use the normal dispatch owner. Borrowed native inspection disables independent final publication. Clear response ownership before disposal so pool reset cannot dispose twice. |
| Pooled response conversion | Counts converter failures after disposing any response not transferred to the caller. Input failures belong to the underlying send unless the conversion receives the final owner's lease. |
| `ExecuteAsync`, `ExecuteFireAndForgetAsync` | Owner starts before raw parsing, catalog validation, key-prefix rewriting and Cluster slot validation. Raw, catalog and interpolated commands use their public caller boundary even when dispatch is shared. |
| Single-command facets (string, hash, key, list, sorted-set) | Boundary covers validation, serialization, key mapping, construction, typed conversion and cleanup. Synchronous client-cache hits keep an empty lease. Command-family argument construction belongs to each family's entry point. |
| `SendShutdownAsync` | Starts before option validation and the `AllowAdmin` check; holds until the control socket is released. Counts preflight and write failures only; server-side errors are outside this API. |
| Physical connections | Setup borrows the connecting caller's boundary. A disconnect records one internal error with zero retries and never invents a final failure. Cancellation releases the caller reference but keeps the receive reference until the FIFO reply drains or the socket closes; a late server error stays internal. |
| Lua scripts | Starts before script validation. EVALSHA retries, redirects and NOSCRIPT fallback share the boundary. `SCRIPT LOAD`/`EXISTS`/`FLUSH` use one boundary across all Cluster primaries. |
| Scans (`SCAN`, `HSCAN`, `SSCAN`, `ZSCAN`, Cluster pages, Valkey `CLUSTERSCAN`) | Each page owns its retry history. Cancellation between yielded items is a separate failure. Ending early is not an error. |
| Streaming uploads | Boundary covers lease acquisition, source reads, retries, replay reset, reply inspection and cleanup. Close an incomplete frame before publishing. Never dispose the caller's source stream. A reset failure preserves the original server rejection. A detached fill cannot publish a second failure. |
| Streaming downloads | Header, frame and payload reads have separate lifetimes. Copy the header's retry count to the payload before returning storage. A producer failure counts only when a read observes it, once across all read overloads. Canceling one read does not close the payload. Invalid arguments and early disposal are not payload failures. |
| Batches | Setup rejections count before command ownership. Each queued command has its own owner. Durability batches release their connection and client-cache fence first, including `WAIT`/`WAITAOF` failures. |
| Transactions and WATCH | Shared retry history through commit preflight, EXEC replies and cleanup. WATCH setup and hash-import sessions start before key routing. |
| Deferred results (`DeferredOperation`) | Metadata and forwarding only; no extra storage or lease. Publish after the route's cleanup. Inspecting a pending before execution, after a WATCH abort or after discard counts once; repeated `Result` inspection does not. Final reporting freezes the retry count. |
| Cache producers and waiters | A shared producer cannot publish every waiter's failure; each waiter keeps its own declaration. |
