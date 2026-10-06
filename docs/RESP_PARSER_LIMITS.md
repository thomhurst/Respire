# RESP aggregate storage

Aggregate counts describe framing, not permission to allocate the declared number
of elements. The restartable parser shares one element budget derived from the
buffered byte count across all nested aggregates. Every RESP element needs at
least three wire bytes, so a declaration that exceeds the remaining budget cannot
be complete and returns `NeedMoreData` before renting storage. Fully buffered
valid replies keep their single rent per aggregate. The resumable parser starts
with no element storage and grows it geometrically as complete children arrive.
A header declaring a billion elements therefore does not rent a billion-element
array. Maps and attributes validate the pair count before doubling it; array, set, and push counts
must also fit the parser's signed 32-bit element count.

There is no configurable aggregate-count ceiling. Valid large replies retain their
existing count range. An aggregate can contain at most `int.MaxValue` elements;
maps and attributes can contain at most `int.MaxValue / 2` pairs. Null aggregates
retain their existing `-1` representation. Completed replies retain their normal
pooled ownership and must still be disposed.

Nesting is limited to 512 aggregate frames, including maps and attributes. This
bound applies to both parsers, including fully buffered replies, and protects
recursive parsing, disposal, and owned copies. Replies beyond this depth fail with
a protocol error. Empty aggregates count as a level too; null replies are scalars
for depth accounting. This is a fixed protocol
safety limit, not a per-command or Search-specific setting. Earlier versions had
no explicit nesting cap. The 512-level bound preserves the existing 257-level
Sentinel reply regression; tests exercise both parsers at 512 levels and reject
level 513. It still introduces a compatibility limit for deeper replies.

## Memory and wire limits

The connection's existing 512 MiB response-byte limit remains independent of the
element count and depth checks. It is not a 512 MiB managed-memory budget. Element
arrays store `RespValue` structures, which can cost more than compact RESP tokens;
payload buffers, receive buffers, object headers, pool rounding, and owned result
copies also consume memory.

The restartable parser's total requested element slots cannot exceed one third
of the buffered bytes, across the entire tree, before pool rounding. The budget is
not reset for nested arrays or discarded attributes. This bounds speculative
rents even when a buffered frame is incomplete.

The restartable parser uses a stack-only context whose depth is copied per branch.
Each context retains a reference to the root's stack-local remaining element budget,
so siblings and discarded attributes consume the same budget. Child contexts
increment depth without copying or resetting that budget. Scalar replies bypass
the context entirely.

The resumable parser's retained element capacity depends on children actually
parsed across the entire tree, not the sum of declared counts. An incomplete frame
with no complete child holds no element array. The first completed child uses the pool's minimum bucket
(16 slots); subsequent growth doubles capacity below 256 slots, then quadruples
capacity once at least 256 children have completed, capped by the declared count,
with pool rounding. A growth operation temporarily holds the old and replacement
arrays, transfers ownership of completed children, then clears and returns the old
array. This bounds speculative storage while preserving large replies; it does
not impose an absolute aggregate heap budget or guarantee that every legal reply
fits available memory.

Malformed input and connection shutdown release retained frames and children.
Cancellation does not remove a response from the FIFO: the connection continues
parsing/draining its frame before delivering the next response. Aggregate growth
does not change that rule or the large-bulk direct-fill contract.

## Performance validation

Parser changes require a pinned baseline/candidate/baseline CI comparison on both
supported frameworks. Include small and large complete arrays, nested replies,
and fragmented resumable arrays, with scalar batches as a control; report
allocation and latency uncertainty. Run the comparison by adding the
`run-aggregate-benchmarks` label to the PR. Reapply it after a source change to
measure the new head. The workflow validates both builds before measuring and
retains its pinned revisions, reports, and logs as artifacts.
Storage growth trades extra copies for bounded speculative allocation. Do not
infer latency equivalence from correctness tests or pooled-allocation counts.
