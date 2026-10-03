# Sentinel notification state and invariants

`SentinelRouter.Notifications` supervises subscriptions and runs one notification
discovery worker. `SentinelNotificationCoalescer` is synchronous state owned by the
router gate. Network queries and DNS resolution happen outside that gate.

## Review boundaries

Review the implementation in these dependency groups, keeping each group's regression
tests beside its runtime changes:

1. **Wire and subscription lifecycle:** `SentinelEvent`, the connection close observer,
   `SubscriptionHub` recovery, and `SentinelMonitoring`.
   Check channel/service filtering, separate Sentinel credentials, reconnect episode capture,
   subscription gaps, and disposal. The parsing and Pub/Sub reconnect tests cover this boundary.
2. **Discovery and publication:** `SentinelNotificationCoalescer`, `SentinelDiscoveryState`,
   `SentinelResolver`, and the router's notification worker and generation publication.
   Check the state table and safety boundaries below against `SentinelNotificationTests`,
   `SentinelFenceTransitionTests`, `SentinelConfigurationTests`, and `SentinelRoutingTests`.
3. **Public contract and integration:** the Sentinel container fixture, real failover tests,
   connection/reconnect documentation, and observability documentation. Real Redis and Valkey
   failover must exercise the same publication path as the deterministic wire regressions.

The monitor and reducer extraction in #727 separates the first two runtime boundaries.
Fixtures and documentation accompany the behavior they verify and describe, so intermediate
changes retain executable coverage and an accurate public contract.

The notification router's `SafeLog` and `LogSentinelEvent` wrappers and the resolver's
`LogOptionalDiscoveryFailure` wrapper for peer/configuration failures isolate non-fatal
logger callback failures and increment the process-wide `respire.sentinel.guarded_logging.failures`
counter instead of logging through the same failing sink. These wrappers also isolate
non-fatal counter-listener exceptions; fatal exceptions still propagate. The counter covers
these wrappers only. Other Sentinel logging paths are outside its coverage,
and this is not a general guarantee that throwing loggers cannot interrupt discovery.

Recovery, retry, and guarded diagnostics use `SentinelExceptionPolicy.IsRecoverable`.
Resource cleanup and task joining still collect all failures before propagating the result;
those ownership boundaries intentionally do not discard errors through a recovery filter.

## Background work ownership

| Work | Owner and shutdown contract |
| --- | --- |
| Monitor supervisor and endpoint monitors | `SentinelMonitoring` owns subscriptions, reconnect episodes, parsing, the clock/resolver seams, and publication rearm signals. It shares the router gate for registration and shutdown. `Stop` rejects new registrations and returns the owned tasks for the router's bounded join. |
| Notification rediscovery and switch-source DNS tasks | The router retains generation-sensitive evidence and task registration under its gate. Disposal sets `_disposed`, cancels `_lifetime`, then joins these tasks and the monitor tasks with one shared ten-second notification shutdown bound. A straggler must recheck the disposed gate before publication or retirement. |
| Generation retirement and correction-fence drainage | Each owned generation retains its retirement task. Disposal starts cleanup for every owned connection/pool, then joins retirement tasks and propagates aggregated failures. Failed cleanup stays owned until disposal. |
| State/health observer callbacks | Serialized in `_notifications`, outside publication locks. Pending application callbacks are suppressed after disposal; explicitly retained telemetry callbacks may still run. This chain is not joined because an active observer can synchronously dispose the client itself. |

First-subscription acknowledgements advance a monitor version. Discovery captures that version
before its network lookup and marks it validated only after accepting the primary. An initial
gap already covered by that validation can reuse the healthy generation without another ROLE
pass. A subscription that attaches during the lookup still requires a later pass. Merging an
initial gap with any real switch, down event, reconnect, or overflow gap clears this shortcut;
those events keep their independent rediscovery requirements.

The dedicated background-work owner and pure reducer are the next architectural change in
#727. That extraction must preserve these different joining and reentrancy contracts.

## State transitions

| State/event | Transition |
| --- | --- |
| Idle, relevant notification | Make the hint active and start one worker. |
| Active, notification arrives | Retain new evidence in the pending hint. Arrival order does not establish failover order. |
| Active+pending, duplicate of active switch | Retain the duplicate's reporter without adding the active switch's source to the independent pending switch. |
| Discovery succeeds | Reconcile unqueried reporters, retaining demotion evidence for other primaries. Consume source evidence for the validated endpoint or its actual ROLE-validated socket peer only in reconciliation of that completed evidence; independent pending switches keep their fences. Source DNS aliases must be unambiguous. |
| ROLE accepts one peer from a multi-address hostname | Narrow the epoch owner's DNS candidates to the physical peer that answered ROLE. A numeric fallback may confirm that peer at the same or missing epoch after the hostname socket closes. Another peer under the same hostname requires a newer epoch; rejected connected candidates are released before fallback. Deployments that have never exposed an epoch retain metadata-free recovery. |
| Discovery succeeds without remaining source evidence | Bind reporter-only reconciliation to the validated owner and socket peer. A different owner requires a strictly newer configuration epoch. |
| Discovery succeeds with another report of the same master-down outage pending | Bind reconciliation to the validated owner; the affected endpoint identifies the outage independently of reporter, channel and quorum count. Normalize numeric addresses and fold hostname case while retaining the port. |
| A parsed down report arrives after its outage worker completed | Retain each affected endpoint together with its reporter through coalescing. Capture the current validated owner after acquiring discovery ownership. Each reporting Sentinel uses only its own down evidence: reports about superseded owners and unreported fallback Sentinels reconcile the current owner unless a newer epoch authorizes movement. Current-owner reports remain independent, including after failback. Check numeric evidence first, then resolve hostname reports concurrently within one separate alias deadline and require unambiguous validated-peer identity. Cancel and join losing lookups. Unknown aliases retain the fence without consuming the candidate's DNS/connection/ROLE deadline; caller cancellation still wins. |
| DNS moves before or after a hostname down report is received | Retain the validated endpoint and peer from notification receipt in that reporter's evidence. Every hostname report, including a known hostname received after publication, can authorize recovery only while the observed owner is still current and fresh DNS unambiguously matches its validated peer. Textual equality alone cannot identify the outage. Never rebind old evidence to a later owner through fresh DNS. Include the observed peer in hostname outage identity so the next owner's outage is not mistaken for the completed one. Numeric reports continue to identify their endpoint directly, including a next outage queued during publication. |
| Discovery succeeds with an independent down/gap hint pending | Discover again without binding that hint to the completed primary. A down report for a different endpoint or a delivery gap may describe a subsequent promotion, including on deployments without epoch metadata. |
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
  belongs to that reconciliation pass and duplicate reports of the same outage; an independent
  down/gap hint can discover a later primary, and an independent switch retains its own evidence.
- DNS answer sets do not prove which peer answered `ROLE`. Reconciliation keeps the actual
  validated socket peer, including its port. Ambiguous DNS overlaps cannot consume another
  primary's source fence. Demotion matching may conservatively match any source address;
  consuming that fence requires the stronger identity proof.
  Fresh DNS evidence must match the validated peer even when the hostname text is unchanged.
  Textual hostname identity is a fallback only when DNS evidence is unavailable.
- An unchanged target hostname cannot suppress a switch notification: DNS may now resolve
  to a different server. The target-is-current shortcut requires numeric peer identity.
  Every command slot must match that peer before skipping rediscovery or reusing an entire
  generation. One matching socket cannot authorize reuse while another still reaches an old
  DNS peer. Source fences continue to recognize any known peer.
  Conflicting-cycle source evidence still protects an explicitly announced failback target.
- A target hostname whose entire DNS answer set identifies demoted sources is rejected
  before connecting, even with a newer configuration epoch. Mixed answers proceed to
  socket validation; connecting to a demoted source is still rejected. Epochs order Sentinel's announced owner;
  they do not prove that the client's DNS or connected socket reaches that owner. The router
  checks every registered ROLE-validated socket peer before accepting the configuration or
  publishing it. The last validated socket cannot hide another socket reaching a demoted source.
- A source hostname may already resolve to the promoted peer. Fresh source addresses that
  also identify an unambiguous announced hostname target are not retained as demotion
  evidence, before or after target publication, unless they identify the peer validated when
  the event arrived. A literal source, the connected source hostname, and that known peer
  still retire the generation; target DNS cannot erase this source identity.
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
  Temporary clients that close before all subscription acknowledgements arrive also share
  the outer retry episode; only a successful subscription resets that retry state.
  A close after successful recovery captures the signal for a new episode.

## Executable coverage

Unknown or ambiguous advisory DNS does not authorize a different owner or erase a demotion
fence. Failed notification discovery retries with backoff (unlimited by default); a configured
retry budget can stop that worker, and commands can still trigger discovery on demand.
This is rediscovery, not a separate periodic polling loop. Availability requires a Sentinel
and reachable primary to provide sufficient fresh evidence. Permanently ambiguous or stale
reports cannot guarantee failover without weakening the split-brain safety contract.

Switch payloads contain endpoints, not a configuration epoch or event sequence. After a
completed A-to-B, B-to-A cycle, a delayed A-to-B report can be indistinguishable from a genuine
third transition when epoch metadata is unavailable and both servers answer ROLE master.
Completed edge history cannot establish that report's age. The metadata-free fallback permits
genuine recurrence and therefore cannot guarantee rejection of every delayed cyclic report.
Grant `SENTINEL MASTER` permission for configuration-epoch ordering; ROLE alone does not prove
global ownership. `CompletedSwitchCycleAllowsGenuineRecurrenceWithoutEpochs` pins the recurrence
contract that a permanent completed-edge fence would break.

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

`RandomNotificationSequencesRequireRoleAndMonotonicEpochs` replays 96 discovery attempts
for each of three fixed seeds through the real router and fake RESP sockets. It interleaves
switch/down/gap hints, active/pending coalescing, configuration epochs, successful ROLE
replies, and replica ROLE replies. Every successful selection requires a fresh successful
ROLE response and a nondecreasing epoch. Failed attempts preserve the published generation.
Positive coverage checks require successful selections, stale-report rejection, and actual
replica ROLE replies, so an implementation that rejects everything cannot pass.

`RandomInterleavingsNeverLetAStaleReporterReleaseValidatedOwnership` checks the ownership
fence across three seeded sequences of 64 offer, success/failure, and supersession steps.
It mixes duplicate switches, stale down reports, and another Sentinel's current-owner
outage reports. Every active state rejects the stale reporter's old owner without epoch
metadata and accepts the validated owner as a positive control.

The monitor/coalescer extraction remains tracked by [#727](https://github.com/thomhurst/Respire/issues/727).
That refactor must preserve these contracts and the deterministic tests while reducing
shared mutable state. Endpoint identity consolidation must keep epoch-owner equivalence
separate from conservative switch-source matching.
Complete the explicit Idle/Active/ActivePending reducer in that issue before adding further
notification behavior. The randomized publication tests are a regression gate for extraction.

Evidence arrays are immutable after publication. Duplicate reporter unions reuse existing
arrays, empty source unions reuse their populated operand, and unchanged DNS evidence does
not clone source arrays. Larger collection and reducer changes belong to that extraction.
