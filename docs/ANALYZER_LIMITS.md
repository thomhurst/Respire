# Analyzer control-flow limits

RESP001 (disposal) and RESP002 (pending reads) share one control-flow search. When it hits a limit it
treats the path as reachable, so it may warn but never hides a bypass. A warning in a very large or
branch-heavy method can be an analysis limit, not a confirmed bug: move acquisition, use and
disposal or flush into a smaller scope.

## What the search models

- Catches, filters, finally blocks, local jumps and iterator disposal within the current scope.
  Opaque operations before a proof barrier can enter local catch handlers.
- Awaited flush barriers include receiver and argument evaluation, including arguments to completion
  adapters such as `ConfigureAwait` and `Task.WhenAll`.
- Awaiting a directly default-initialized `ValueTask` or `ValueTask<T>` has no exception edge. Unknown
  awaitables stay conservative.

## What it does not model

- Exception types across method calls, and uncaught implicit exceptions.
- Failures of the disposal, flush or completion operation itself.
- Whether a callee throws after receiving an owned value (see transfers below).

## Ownership transfers

- **Passing** an owned result or batch to a method or property setter transfers it at callee entry.
  Failures before entry count: argument evaluation, receiver checks and type initialization. A static
  method or accessor before a proof barrier has its own opaque exception paths, not only
  `TypeInitializationException`.
- **Returning** an owned value transfers it only after enclosing `finally` blocks complete. If a
  `finally` throws into a local handler, that handler must still release or transfer the value.

## Branch evidence

- A traversed assignment to a local or parameter invalidates its branch evidence. Writes before
  selection or after the proof interval do not.
- Captured writes, by-reference escapes and address-taken locals stay conservative. Array-index
  calculations do not expose their locals' storage.

## Budgets

| Budget | Limit | When exhausted |
| --- | --- | --- |
| Processed states per query (`ReachabilityWalker.MaxProcessedStates`) | Fewer than 16,384, including duplicates discarded by the earliest-entry check | The unproven path is treated as reachable. |
| Path facts (`FlowConditions.MaxPredicates`, the width of the `ulong` masks) | 64: branch predicates, non-null receivers, successful type initialization, owning value selected inside a wrapped call argument | `FlowConditions.UnknownPathFlag` is set. It proves nothing, so the transfer barrier is omitted and more paths stay reachable. |

The diagnostic keeps its usual message at a limit. Queries honor compilation cancellation. Exception
continuations and dispatch decisions are cached per query, not shared across queries with different
origins and barriers.
