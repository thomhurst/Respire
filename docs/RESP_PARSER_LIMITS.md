# RESP aggregate storage

An aggregate count describes framing, not permission to allocate that many elements. When you
change the parser, keep these rules: never rent storage for a declared count the buffered bytes
cannot satisfy, keep children readable only while the root lives, and keep the fixed limits below.

## Limits

| Limit | Value | Notes |
| --- | --- | --- |
| Aggregate elements | `int.MaxValue` | Array, set and push counts must fit a signed 32-bit count. |
| Map and attribute pairs | `int.MaxValue / 2` | Pair count is validated before doubling. |
| Nesting depth | 512 aggregate frames | Includes maps, attributes and empty aggregates; null replies are scalars. Applies to both parsers. Level 513 fails with a protocol error. Fixed safety limit, not configurable. |
| Response bytes per connection | 512 MiB | Independent of element and depth checks. Not a managed-memory budget. |
| Restartable element budget | One third of buffered bytes | Shared across the whole tree, before pool rounding. Not reset for nested arrays or discarded attributes. |
| Payload pool buffer | 64 MiB | Larger frames copy each retained string child separately. |
| Aggregate-count ceiling | None | Valid large replies keep their full count range. |

The 512-level depth limit protects recursive parsing, disposal and owned copies, and still accepts
the 257-level Sentinel reply regression. It is a compatibility limit for deeper replies. Null
aggregates keep their `-1` representation.

## Parsing paths

1. **Fully buffered.** The connection tries this first. Every RESP element needs at least three wire
   bytes, so a declaration that exceeds the remaining element budget returns `NeedMoreData` before
   renting. Valid complete replies keep one rent per aggregate.
2. **Resumable.** If an aggregate is incomplete, the connection moves completed children and all
   unfinished parent frames into the resumable parser; completed children are not parsed again.
   Deferred string payloads become owned copies before input can be compacted. Discarded attribute
   trees transfer nothing.

The public restartable parser retains nothing and leaves its position unchanged on incomplete input.
It uses a stack-only context whose depth is copied per branch, while every branch shares a reference
to the root's remaining element budget. Scalar replies bypass the context.

### Resumable storage growth

Capacity follows children actually parsed, not declared counts:

- An incomplete frame with no complete child holds no element array.
- The first child takes the pool's minimum bucket (16 slots).
- Capacity doubles below 256 slots, then quadruples once at least 256 children complete, capped by
  the declared count, with pool rounding.
- Growth briefly holds both arrays, transfers completed children, then clears and returns the old one.

This bounds speculative storage. It is not an absolute heap budget and does not guarantee every legal
reply fits in memory. Element arrays clear only their used slots before returning to the pool.

## Payload ownership

Dense fully buffered aggregates up to 64 MiB copy their consumed wire frame once (excluding leading
discarded attributes) into a payload buffer owned by the root. Nonempty string children, including
nested ones, borrow slices of that copy.

Children are copied separately instead when:

- the frame exceeds the 64 MiB pool limit (a single child above 64 MiB still needs an unpooled
  allocation), or
- the frame is at least 4 KiB and retained string bytes are under one eighth of it. Discarded
  attributes do not count as retained bytes.

Integer-only aggregates copy no payload, common simple replies stay interned, and fragmented resumable
aggregates keep per-child payload ownership. The sparse-frame policy bounds copying; it does not
promise lower latency for every shape.

Lifetime rules, on both parse paths:

- Keep the root alive while reading children. Use `ToOwned()` to retain a root or child.
- Public `RespireResult` child views check the root's disposed state before reading.
- Debug builds poison a returned shared frame to expose invalid internal reads; Release adds no work.
- Completed replies keep pooled ownership and must be disposed.

Materialize before disposing the reply:

- Deserialization materializes strings, byte arrays, primitives and serializer results while the root lives.
- Batch and transaction converters finish typed results first. Deferred raw, script, function and
  protocol-shaped results use `RespireResult.CreateOwned()`, which deep-copies with `ToOwned()`.
- Pub/sub dispatch copies payload bytes before buffering messages.
- Scan pages and synchronous parsers materialize output before disposal; escaping protocol-shaped
  fields use owned copies.

Memory use exceeds wire size: element arrays store `RespValue` structs, and payload and receive
buffers, object headers, pool rounding and owned copies all add cost.

## Failure and cancellation

Malformed input and connection shutdown release retained frames and children. Cancellation does not
remove a response from the FIFO: the connection keeps parsing and draining its frame before delivering
the next response. Aggregate growth does not change this or the large-bulk direct-fill contract.

## Performance validation

Parser changes need a pinned baseline/candidate/baseline comparison on net10.0. Start it by adding the
`run-aggregate-benchmarks` label; reapply the label to measure a newer head. The workflow validates both
builds before measuring and keeps revisions, reports and logs as artifacts. Correctness stays covered
on net8.0 and net10.0.

Cover these shapes and report allocation and latency uncertainty:

- Small and large complete arrays, nested replies, and fragmented flat and nested arrays whose first
  slice fits the budget, with scalar batches as a control.
- Typed MGET (1,906-byte frames) and HGETALL (3,806-byte frames) decoding, singly and in batches
  retaining 50 replies, plus rows decoding two 1 MiB values each.
- Sparse mixed replies of 4,096 elements with two three-byte strings, singly and retaining 50 replies,
  to show the fallback's pool pressure.

Storage growth trades extra copies for bounded speculative allocation. Do not infer latency
equivalence from correctness tests or pooled-allocation counts.
