# Analyzer control-flow limits

RESP001 disposal checks and RESP002 pending-read checks share a control-flow search.
The search follows catches, filters, finally blocks, local jumps, and iterator disposal
within the current scope. Opaque operations before a proof barrier can enter local
catch handlers. The search does not infer exception types across method calls or
model failures of the disposal/flush operation itself. Uncaught implicit exceptions
remain outside the proof.

Passing an owned result or batch to another method or property setter transfers
responsibility at callee entry. The search checks failures before entry, including
argument evaluation, receiver checks, and type initialization. It does not inspect
whether the callee subsequently throws before disposing the result or flushing the
batch. A static method or accessor before a proof barrier still has its own opaque
exception paths, even when its declaring type has a static constructor; those paths
are not restricted to `TypeInitializationException`.

Returning an owned value transfers responsibility only after enclosing finalizers
complete. A finalizer that throws into a local handler can abandon the return value;
that handler must still release or transfer it.

Branch evidence is invalidated when a traversed assignment changes its local or
parameter. Writes before selection or after the proof interval do not invalidate
that interval. Captured writes, by-reference escapes, and address-taken locals remain
conservative; array-index calculations do not expose their locals' storage.

Each query processes fewer than 16,384 states from its work stack (`ReachabilityWalker.MaxProcessedStates`),
including duplicate states discarded by the earliest-entry check,
and tracks at most 64 path facts (`FlowConditions.MaxPredicates`, the width
of the `ulong` masks). These facts include branch predicates, non-null receivers, successful type initialization,
and whether an owning value was selected inside a wrapped call argument.
`FlowConditions.UnknownPathFlag` represents exhausted fact capacity. Such a flag
cannot prove a receiver non-null or a type initialized; an unproven transfer barrier
is omitted, which leaves more paths reachable rather than hiding a bypass.
If the state budget is exhausted, the search conservatively treats the
unproven path as reachable. A warning in a very large or branch-heavy method can
therefore reflect an analysis limit rather than a confirmed unsafe execution. The
diagnostic retains its usual message; reaching the limit never suppresses a warning
by claiming disposal or a flush is guaranteed.

Moving acquisition, use, and disposal or flush into a smaller scope can make the
relationship easier for both readers and the analyzer to verify. Queries honor
compilation cancellation. Exception continuations and dispatch decisions are cached
within each query; they are not shared across queries with different origins and barriers.
