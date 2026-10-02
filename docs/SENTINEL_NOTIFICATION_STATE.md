# Sentinel notification state and invariants

`SentinelRouter.Notifications` supervises subscriptions and runs one notification
discovery worker. `SentinelNotificationCoalescer` is synchronous state owned by the
router gate. Network queries and DNS resolution happen outside that gate.

## State transitions

| State/event | Transition |
| --- | --- |
| Idle, relevant notification | Make the hint active and start one worker. |
| Active, notification arrives | Retain new evidence in the pending hint. Arrival order does not establish failover order. |
| Discovery succeeds | Reconcile unqueried reporters, retaining demotion evidence for other primaries. Consume source evidence for the validated primary, including DNS aliases captured by that successful discovery. |
| Discovery fails | Retain source evidence and prioritize unqueried reporters before retrying with bounded backoff. |
| Another discovery publishes a different generation | Discard superseded active evidence, preserve pending notifications, and continue from the published generation. |
| No pending evidence or reporter | Complete the worker; a later relevant event starts another. |
| Disposal | Cancel work and prevent subsequent publication under the router gate. |

A source-resolution record retains its original hint and evidence offered after that
lookup began. It does not import earlier pending evidence: doing so could let an older
switch target incorrectly protect a primary from a later demotion. Completion removes
the record. DNS evidence remains paired with its endpoint and port.

## Safety boundaries

- Notifications are advisory. Publication still requires Sentinel discovery and a
  successful `ROLE` check.
- Observed configuration epochs never move backward. Equal or missing epochs cannot
  change an observed owner through an ambiguous DNS overlap.
- When `SENTINEL MASTER` is unavailable, source/target evidence still fences a stale
  reporter whose old primary continues to answer `ROLE master`. A wake-up-only event
  model loses that evidence and cannot preserve this supported fallback contract.
- Resolved addresses used to consume switch-source evidence belong to a successfully
  validated generation. They do not establish owner identity or create a global DNS cache.
- A switch confirming the current primary must remain pending while discovery is active:
  the in-flight query can still publish another primary before that confirmation runs.
- Retirement preserves accepted commands and correction fences. Notification coalescing
  does not replay accepted work or force disposal of a draining generation.

## Executable coverage

`OfferedEvidenceIsAlwaysQueuedOrDiscoveredAcrossWorkerTransitions` runs 64 seeded
sequences of 64 transitions against an independent offered/discovered evidence model.
`MergeUnionsAreCommutativeIdempotentAndKeepTheFaultFlag` checks union properties across
256 seeded pairs. Focused tests cover delayed DNS, intervening publication, current-target
confirmation, reporter ordering, metadata denial, and hostname/numeric failback aliases.
The wire failback alias regression passes for numeric endpoints and fails for hostname
aliases before validated address evidence is carried into reporter reconciliation.

The monitor/coalescer extraction remains tracked by [#727](https://github.com/thomhurst/Respire/issues/727).
That refactor must preserve these contracts and the deterministic tests while reducing
shared mutable state. Endpoint identity consolidation must keep epoch-owner equivalence
separate from conservative switch-source matching.
