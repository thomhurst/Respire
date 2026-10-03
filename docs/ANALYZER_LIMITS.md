# Analyzer control-flow limits

RESP001 disposal checks and RESP002 pending-read checks share a control-flow search.
The search follows catches, filters, finally blocks, local jumps, and iterator disposal
within the current scope. Opaque operations before a proof barrier can enter local
catch handlers. The search does not infer exception types across method calls or
model failures of the disposal/flush operation itself. Uncaught implicit exceptions
remain outside the proof.

Branch evidence is invalidated when a traversed assignment changes its local or
parameter. Writes before selection or after the proof interval do not invalidate
that interval. Captured writes, by-reference escapes, and address-taken locals remain
conservative; array-index calculations do not expose their locals' storage.

Each query examines fewer than 16,384 queued states and tracks at most 64 branch
predicates. If the state budget is exhausted, the search conservatively treats the
unproven path as reachable. A warning in a very large or branch-heavy method can
therefore reflect an analysis limit rather than a confirmed unsafe execution. The
diagnostic retains its usual message; reaching the limit never suppresses a warning
by claiming disposal or a flush is guaranteed.

Moving acquisition, use, and disposal or flush into a smaller scope can make the
relationship easier for both readers and the analyzer to verify. Queries honor
compilation cancellation. Exception continuations and dispatch decisions are cached
within each query; they are not shared across queries with different origins and barriers.
