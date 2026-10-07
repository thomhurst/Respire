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

Fully buffered aggregates up to the payload pool's limit copy their consumed wire
frame once, excluding leading discarded attributes, into a payload buffer owned
by the root. Nonempty string
children borrow slices of that copy,
including children of nested arrays and maps. Receive-buffer reuse therefore
cannot change a completed reply. Keep the root alive while reading its children;
use `ToOwned()` for a retained root or child. Integer-only aggregates need no
payload copy, and common simple replies remain interned. Fragmented resumable
aggregates retain their existing per-child payload ownership. Element arrays
clear only their used slots before returning to the pool.
The root lifetime contract applies to children on both parse paths. Public
`RespireResult` child views check the root's disposed state before reading.
Debug builds poison a returned shared frame to expose invalid internal child
reads; release builds add no poisoning work. Deserialization materializes strings,
byte arrays, primitives, and serializer results while the root remains alive.

Batch and transaction converters finish materializing typed results before disposing
the reply; deferred raw, script, and function results explicitly use `ToOwned()`.
Pub/sub dispatch copies payload bytes before placing messages in subscription buffers.
Scan pages and synchronous response parsers materialize their output before disposal;
protocol-shaped additional fields use owned copies when they escape that parse.

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

The response payload pool retains buffers up to 64 MiB. Larger complete frames
copy each retained string child separately instead of allocating an unpooled
backing frame on every reply. Discarded nested attributes and integer tokens are
not copied in this fallback. An individual child larger than 64 MiB still needs
an unpooled allocation. The root lifetime contract applies to either storage mode.
Shared-frame copies also include integer tokens and framing, so a mostly
integer aggregate with a few strings can copy more bytes than separate payload
copies. Integer-only aggregates still copy no payload. These are storage tradeoffs,
not a guarantee of lower allocation or latency for every aggregate shape.
The 100-item payload benchmark uses 1,906-byte MGET and 3,806-byte HGETALL frames,
both well within the pool's limit; its retained cases hold 50 such replies.
Two additional MGET/HGETALL rows decode two 1 MiB values each, exposing larger
copies and pool rounding alongside typed string materialization.

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
allocation and latency uncertainty. Also measure typed MGET and HGETALL decoding
and batches retaining 50 replies before draining, to expose payload pool pressure.
Add the `run-aggregate-benchmarks` label to the pull request to start these
comparisons. The workflow validates both builds before measuring and
retains its pinned revisions, reports, and logs as artifacts.
Storage growth trades extra copies for bounded speculative allocation. Do not
infer latency equivalence from correctness tests or pooled-allocation counts.
