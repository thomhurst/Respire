# Error ownership for contributors

This guide is for contributors who change command routing or error reporting. For the user-facing error metrics, see [Observability](../website/docs/integrations/observability.md).

The internal error metric foundation provides failure-only `ErrorObservation` leases.
These leases do not consume responses, own transport references, or wrap deferred results.

Pooled response conversion counts caller-visible converter failures in
`redis.client.errors` after disposing any response that was not transferred to
the caller. This applies to both immediate replies and pending conversions.
Input failures remain the responsibility of the underlying send unless the
conversion receives its final owner's lease, which also preserves retry counts.
Exceptions and cancellation tokens retain their original identity and status.

Native raw, typed, string and byte response sources support explicit error
observation through response inspection, conversion, cleanup and caller reference
release. Immediately admitted ordinary standalone raw, typed, string and byte dispatch
returns the original native response source. Its inspection owns final publication, with
no extra dispatch source, ownership lock or continuation forwarding on successful commands.
This path excludes cache coordination, circuits, Sentinel, Cluster, replica reads,
streaming, scripts and activity tracing; raw operation-duration metrics also retain the
normal route. Capacity waits and retirement recovery retain the normal dispatch owner.
Other core raw sends and typed, string and byte dispatch establish a logical
response owner before disposal checks, route selection and cancellation handling.
Primary, replica and Cluster ready sends borrow that owner. Retired-connection
handoffs, capacity waits, MOVED/ASK retries and Sentinel replacement share its retry
history. The final error is published once after response inspection, conversion
and cleanup. Borrowed native inspection disables independent final publication.
This covers core dispatch boundaries; command-family argument construction remains
the responsibility of each family's entry point.
Successful inspection and conversion keep a default lease and rent no error
observation storage.

`ExecuteAsync` and `ExecuteFireAndForgetAsync` retain a caller response owner before
raw parsing, catalog validation, key-prefix rewriting and Cluster slot validation.
Raw blocking commands, including ASK redirects and the `ASKING` reply, borrow that
owner. SORT, multi-key list moves and list/sorted-set pops retain the same boundary
through construction and typed conversion. MGET, MSETNX and ZINTERCARD also cover
local multi-key validation. Cluster-wide sends share the caller's retry history
across targets and complete cache fences and discovery cleanup before publishing a
final failure. Successful calls rent no error observation storage.

`SendShutdownAsync` starts its caller boundary before option validation and the
`AllowAdmin` check, retaining ownership until its control socket is released. It
counts caller-visible preflight and write failures; completion still confirms only
the local write, and server-side errors remain outside this submission API.

When adding a core public method, update its source-adjacent
`<source-file>.cs.ownership.json` declaration in the same change. There is no shared
inventory file to update. The guard treats every public method on a public core
type as a route, including synchronous methods on new types. Explicit `NonRoutes`
entries exclude local value operations and helpers, with a reason for each source
signature. A new method fails until it is classified. The declarations pin
public source signatures, not the number or location of telemetry recorder calls.
`Surfaces` maps each declared signature to an executable final owner: `OwnerType`
uses the same signature on that implementation type, and `Overrides` names a
different complete source member when delegation changes the signature.
`AdditionalOwnerTypes` pins alternative implementations, such as batch and
transaction implementations of the same queue interface. Generic
arity uses a backtick followed by the parameter count. Parameter names and defaults
are omitted; parameter types, modifiers and return types are retained. Interface
declarations alone cannot be final owners, except executable default interface
methods. Same-signature implementation owners must have a public executable method
with the same static or instance kind, and list the contract in their source base
types (including inherited and partial declarations). A `new` hiding method owns the
contract only when its type re-lists that interface. A partial route's declaration
may sit beside either its defining or implementing source file. Explicit interface implementations retain their interface-qualified
source signature; they cannot masquerade as implicit public implementations.

Declare the owner before argument validation, disposal checks, cancellation checks,
command construction or setup parsing can fail. Explain the complete lifetime in
the surface's `Contract`. Forwarding overloads and facet interfaces retain the
logical caller's ownership when delegating to their declared implementation.
Native pooled inspection and typed converter inspection have separate boundaries;
keep conversion and response cleanup inside the caller's lifetime. Raw, catalog and
interpolated commands use their public caller boundary even when dispatch is shared.
Transfer the same final-owner lease to the appropriate final inspection boundary;
these declarations never authorize two final publications for one caller.

`Boundaries` declares non-public branches by complete source member, ownership role
and lifetime contract. Use `final` for a caller's final inspection, `helper` for
delegated work, `borrower` for shared attempts or retries, and `internal` for an
observation with no caller final failure. Helpers and borrowers name the enclosing
owner; they cannot declare themselves final owners. Internal observations have no
caller owner. Shared helpers may serve many enclosing public routes: the boundary's
owner anchor identifies the relevant dispatch lifetime, while each applicable
surface retains its distinct logical caller. Do not interpret an anchor as a
global lease shared by unrelated callers.

Keep explicit declarations for cache producers and each waiter, upload fills and
later download reads, fan-out targets, deferred execution and later pending-result
inspection, and cleanup. A shared producer cannot publish every waiter's final
failure. A returned stream's later read has its own caller boundary. Queue-time
validation, batch execution and pending inspection have distinct lifetimes.
Returned per-node failures must not also become duplicate parent failures.

Run `CommandRouteOwnershipTests` on net8.0 and net10.0. The guard scans the core
library source, including catalog dispatch, plus the distributed-cache implementation,
and validates each target framework
independently. A route's executable owner and inherited interface contract must
exist on the same target; a body in another framework branch cannot supply them.
Declarations for target-specific routes apply only where those routes are public.
New signatures, missing declarations and removed executable owners fail;
negative controls exercise those failures. The source-local declarations specify
required ownership. They do not prove that runtime instrumentation is present or
that a delegate passes its lease correctly. Route-family integration must also test final
publication, retries and original exception/cancellation behavior. Extension package
integration requires its own route declarations when its scope is added.

Keep this guard test-only. Do not add production reflection, a successful-path
observation rental, or a universal response wrapper to satisfy it. Preserve native
transport references, deferred result contracts and existing allocation tests.

Keep a default `ErrorObservation.FinalOwner` on a successful path. Call `StartFailure`
only after the first failure or handled retry, and complete the lease in `finally`.
There is one final owner per logical caller, even when independent callers receive the
same exception instance. `PublishFinal` records at most one final failure; repeated
inspection of that generation returns `false`. It observes the exception without consuming
or rethrowing it. Preserve the original exception with `throw;` and retain its
cancellation token.

Helpers borrow a separate lease with `Borrow`. Borrowers can create nested borrowers,
record handled retries with `RecordHandled`, and complete their own lease. They cannot
publish a final caller failure. Every handled retry increments the shared count once;
the final failure captures the total count. Exporters run outside the ownership gate,
so concurrent retry events may arrive out of order while retaining their exact counts.

Borrow before final publication or completion of that lease. `Borrow` on a closed lease
returns an empty borrower, whose `RecordHandled` returns `false`; it cannot recover the
completed operation's retry history. Core dispatch acquires a shared failure-only
lease at its first handled failure and retains it until final caller inspection.
Native response sources also retain a copied retry
count independently, so replies discarded after caller completion still record internal
errors without borrowing a closed or reused final owner.

Copying an owner or borrower value shares its existing completion right; it does not
create another reference. Repeated completion is harmless, including after the pooled
storage has been reused, so cleanup in `finally` cannot replace the original failure.
Storage returns to the bounded pool only after the owner and every distinct borrower
complete. Owner completion does not complete a still-live borrower. Final publication
closes retry reporting and new borrowing. `RecordHandled` and `PublishFinal` return
`false` for default, completed, closed, or stale leases without emitting a metric.
`Borrow` returns a default, inert borrower in those states. These checks apply in every
build, so a final-inspection race or stale asynchronous callback cannot replace the
original failure or affect a new caller's observation. Do not retain completed leases
for later asynchronous work. Storage returns outside the ownership gate after the last
completion. Retry counts saturate at `int.MaxValue`.

Failure-only owners and legacy route observations use the same retry and final-publication
bookkeeping. Their lifetime gates, generations, references and completion rights remain
separate. A handled event captures the count before advancing it; counts saturate at
`int.MaxValue`. Final publication freezes that logical observation's count and rejects
later handled events or count updates, including reentrant exporter callbacks. This does
not suppress late native replies: discarded replies retain their own copied attempt count
and publish internal errors independently after the caller observation has closed.

Native raw, string, byte and typed response inspection shares final-error publication
after response cleanup and caller-reference release. Conversion still runs when the
caller consumes the result. If cleanup also fails, the original conversion or cancellation
error wins, including its cancellation token. Native transport cancellation keeps its
canceled task status. A cleanup failure
without an earlier error remains the caller's final failure. Response ownership is cleared
before disposal so a failed disposal cannot make pool reset dispose the same response again.
Successful inspection does not rent error observation storage.

Physical setup borrows the connection caller's boundary. A physical disconnect records one
independent internal error, with zero logical retries. It does not invent a final failure
for an idle socket, a successful caller, or an already-canceled caller. Pending callers
retain their own final failure after cleanup. Cancellation releases the caller reference
but keeps the native receive reference until the original FIFO reply drains or the socket
closes. A late server error remains an internal event and cannot answer the next command
or publish a second final caller failure. Raw, catalog and interpolated dispatch follow
these same lifetime rules.

Streaming uploads retain the caller boundary through dedicated lease acquisition, source
reads, transparent retries, replay reset, reply inspection and mutation/lease cleanup.
A source failure preserves its original exception and independent cancellation token.
An incomplete frame is closed before caller failure is published; a failed first fill
reclaims the unpublished response. Cleanup never disposes the caller's source stream.
Replay reset runs after releasing the source lease; a reset failure preserves the
original server rejection. Caller cancellation can finish while a detached source fill
still runs. That fill retains its buffer until the fill settles and cannot publish a
second caller failure.

Streaming downloads have independent header, transport-frame and payload-read lifetimes.
The GET header boundary ends when the stream is returned. Copy its completed retry
count through circuit wrappers to the payload before returning observation storage.
Later observation reuse cannot change payload retry tags. The receive loop owns frame
validation and FIFO draining independently of caller inspection. A producer failure is
not a caller failure until a read observes it. Repeated reads of that terminal failure
publish once across all read overloads. Canceling an individual read preserves that
read's token and does not close payload ownership: later reads can succeed, cancel
independently or observe a producer failure. Invalid stream arguments and early disposal
do not count as Redis payload failures. Exporter exceptions cannot replace any failure.

When adding a command path, declare its public boundary and delegated final owner in
the independent route inventory. Helper, borrower, transport, and cleanup observations
must remain distinct from the caller's final publication. Preserve checked native
response lifetimes and deferred-result inspection; do not introduce a universal wrapper.

Single-command string, hash, key, list, and sorted-set query facets start their caller
boundary before argument validation, serialization, key mapping, and command construction.
The same boundary covers typed reply conversion and command cleanup. Transparent
redirects and connection retries borrow that boundary: a recovered failure is internal,
and a failure returned to the caller is counted once with its complete retry count.
Preflight failures keep their original exception and cancellation behavior. Successful
calls keep an empty failure-only lease, including synchronous client-cache hits.

Lua script execution starts its caller boundary before script validation and command
construction. EVALSHA transport retries, redirects and NOSCRIPT fallback share that
boundary through typed conversion and cleanup. SCRIPT LOAD, EXISTS and FLUSH keep one
caller boundary across all Cluster primaries and join every target before publishing
one final failure. Successful script calls rent no error observation storage.

Collection scans (`HSCAN`, `SSCAN`, `ZSCAN`, including `HSCAN NOVALUES`) and standalone
`SCAN` count validation, cancellation and malformed pages once. Replica-policy pages
retain their server affinity and final owner through cursor and item parsing and reply
cleanup. Each page owns its retry history; cancellation between yielded items is a
separate enumeration failure. Ending an enumeration early does not count as an error.

Resumable Cluster pages and direct Valkey `CLUSTERSCAN` pages retain a failure-only
owner from checkpoint or argument validation through page construction, response
cleanup and discovery completion. Capability probes, unsupported-command fallbacks,
retired connections and redirects borrow that owner. Recovered failures are internal;
caller-visible failures count once with the full page retry count. Exception identity
and cancellation token/status are preserved. Successful pages do not rent error lease
storage from either observation pool.

Batch execution counts setup rejections before command ownership starts. Each queued
command then retains a failure-only owner through transparent retries, typed conversion,
and cleanup. Durability batches release their dedicated connection and client-cache fence
before publishing a final error, including WAIT/WAITAOF argument and reply failures.
Transactions retain shared retry history through commit preflight, EXEC replies and
connection cleanup. WATCH setup and hash-import session calls start ownership before
key routing, argument validation or command construction. Successful deferred execution
rents no error observation lease.

The shared `DeferredOperation` helper contains only operation metadata and observation
forwarding. It adds no pending storage, lease, response wrapper or successful-path rental.
Batch, durability and transaction routes call its final publication helper only after
their own cleanup. Transaction and group-selection retry counts are captured before
the lending pending closes; independent batch command retries remain independent.

Deferred command failures are reported once after execution cleanup. Inspecting a pending
before execution, after a WATCH abort, or after its queue is discarded counts that lifecycle
failure once; repeated `Result` or awaiter inspection does not count it again. A later
execution failure remains a separate boundary from an earlier not-ready inspection.
Deferred results use the shared bounded retry transitions both before and after they
acquire failure-only storage. Added retries saturate at `int.MaxValue`; zero or negative
additions do not reduce completed retry history. Final execution reporting freezes that
count. Copied observations and later `Result` or awaiter inspection retain the original
exception and cancellation token without publishing a second terminal failure.

