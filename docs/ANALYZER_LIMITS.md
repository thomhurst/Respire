# Analyzer control-flow limits

RESP001 disposal checks and RESP002 pending-read checks share a control-flow search.
The search follows catches, filters, finally blocks, local jumps, and iterator disposal
within the current scope. It does not infer arbitrary exceptions across method calls.

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
