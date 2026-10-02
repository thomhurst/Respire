# Sentinel notification state and invariants

`SentinelRouter.Notifications` supervises subscriptions and runs one notification
discovery worker. `SentinelNotificationCoalescer` is synchronous state owned by the
router gate. Network queries and DNS resolution happen outside that gate.

## State transitions

| State/event | Transition |
| --- | --- |
| Idle, relevant notification | Make the hint active and start one worker. |
| Active, notification arrives | Retain new evidence in the pending hint. Arrival order does not establish failover order. |
| Active+pending, duplicate of active switch | Retain the duplicate's reporter without adding the active switch's source to the independent pending switch. |
| Discovery succeeds | Reconcile unqueried reporters, retaining demotion evidence for other primaries. Consume source evidence for the validated endpoint or its actual ROLE-validated socket peer only in reconciliation of that completed evidence; independent pending switches keep their fences. Source DNS aliases must be unambiguous. |
| Discovery succeeds without remaining source evidence | Bind reporter-only reconciliation to the validated owner and socket peer. A different owner requires a strictly newer configuration epoch. |
| Discovery succeeds with a newly delivered down/gap hint pending | Discover again without binding that hint to the completed primary. It may describe a subsequent promotion, including on deployments without epoch metadata. |
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
  Conflicting targets do not disable source fences. A completed A-to-B switch cannot
  invert an independent pending B-to-A switch's fence when another A-to-B report arrives.
  Reconciliation of a completed conflicting cycle consumes only the validated target's
  source fence; a source demoted toward one distinct pending target remains fenced.
- A gap or master-down report contains no demotion evidence. After it successfully
  recovers a primary, unqueried reporters can confirm that primary or its unambiguous
  validated peer alias. They cannot replace it without a newer epoch. This restriction
  belongs to that reconciliation pass; a newly delivered down/gap hint can discover a later
  primary, and an independent switch retains its own evidence.
- DNS answer sets do not prove which peer answered `ROLE`. Reconciliation keeps the actual
  validated socket peer, including its port. Ambiguous DNS overlaps cannot consume another
  primary's source fence. Demotion matching may conservatively match any source address;
  consuming that fence requires the stronger identity proof.
  Fresh DNS evidence must match the validated peer even when the hostname text is unchanged.
  Textual hostname identity is a fallback only when DNS evidence is unavailable.
- When a switch names the current primary's hostname, its actual validated peer is captured
  before queuing discovery. A metadata-denied numeric alias cannot republish the demoted
  server while DNS resolution is unavailable. In a conflicting cycle, source address
  evidence also protects a target with the same hostname and port from premature retirement.
- Forced discovery can reuse a healthy generation when the announced endpoint is its
  canonical endpoint without available DNS evidence, or resolves unambiguously to its
  connected peer. A stable hostname with changed DNS evidence requires a fresh connection.
  Publication cannot discard a validated replacement merely because its hostname is unchanged:
  reusing the old generation also requires the same validated peer and port. Reuse still checks
  `ROLE`. IPv6 spelling differences use the same normalized endpoint comparer as epoch state.
- Late source resolution never combines a source's addresses with the arrival generation's
  port. A newer generation is protected by the arrival generation's own identity or later
  announced failback evidence; a pending B-to-C switch must still demote an intervening B.
- A switch confirming the current primary must remain pending while discovery is active:
  the in-flight query can still publish another primary before that confirmation runs.
- Retirement preserves accepted commands and correction fences. Notification coalescing
  does not replay accepted work or force disposal of a draining generation.
- A monitor captures its publication signal at the start of a reconnect episode. Failed
  replacement sockets remain in that episode and cannot overwrite an already granted budget.
  A close after successful recovery captures the signal for a new episode.

## Executable coverage

`SentinelFenceTransitionTests` enumerates fifteen idle, active and active+pending
transitions, including success and failure for switches, gaps and master-down reports.
Every row checks retained source fences and candidate acceptance with no metadata,
equal epochs and strictly newer epochs (90 discovery cases). Wire tests cover a delayed
duplicate during failback, metadata-denied reporter rollback, connected hostname sources,
and preservation of a hostname target when conflicting-cycle discovery fails.

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
