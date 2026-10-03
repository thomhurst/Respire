# Sentinel notification state and invariants

`SentinelMonitoring` supervises subscriptions. `SentinelRouter.Notifications` runs one
notification discovery worker. `SentinelBackgroundWork` registers both components' tasks
under the router gate. `SentinelNotificationCoalescer` applies the pure
`SentinelNotificationState.Transition` function under that gate. Network queries and waits
for DNS completion happen outside it. Switch-source DNS startup captures the resolver task
under the gate so starting a lookup is atomic with stopping monitor ownership; the internal
resolver seam must return its task promptly.

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

## Endpoint identity and address evidence

Configured Sentinel seeds are never aged out. Learned endpoints are removed after
three completed discovery rounds omit them and their last recorded connection attempt
failed. A round needs at least one complete `SENTINEL SENTINELS` reply to count;
permission errors, malformed lists, and caller-cancelled rounds do not supply omission
evidence. Valid rows in a partially malformed list can still add or refresh peers.
Any reporter listing a peer resets its missing count, as does the peer reporting its
own list. Successful connections clear failure evidence. No periodic discovery is
introduced: aging progresses when primary discovery runs.

Reports from overlapping discovery rounds protect peers from older omission evidence.
Membership versions prevent retired monitors from changing the health of a re-added
endpoint. Removal signals the monitor supervisor, which cancels that endpoint's linked
token and joins its cleanup. Configured seeds, the 64 learned-endpoint cap, and accepted
primary/epoch evidence remain intact.

`SentinelEndpointIdentity` defines textual endpoint equality and hashing for discovery,
monitor registration, hint keys, evidence unions, and retained validated owners. Hostname
case is ignored; numeric addresses use their canonical spelling, including IPv4-mapped
IPv6 equivalence. Ports remain distinct. Textual identity never performs DNS resolution.
Literal parsing preserves the runtime's existing IPv4 shorthand syntax (`127.1`, for
example). IPv6 scope IDs remain part of identity: identical link-local addresses on
different interfaces are distinct peers. Interface-name syntax follows the runtime parser;
identity comparison performs no additional interface lookup.

`SentinelAddressEvidence` normalizes and retains immutable candidates from one observation or
lookup lifetime. It copies caller-owned arrays and reuses already canonical immutable
snapshots. Repeated matching compares retained canonical strings without parsing
IPs in the address-pair loop. Switch sources retain this evidence across matching calls.
Evidence provides typed value equality so comparing or hashing switch sources does not
box its fields. Equality retains array-snapshot identity; ownership and alias matching
still use the explicit operations below. Default evidence never matches an observation.
Its conservative `CouldMatch` operation can fence a possible demoted source, but ownership
confirmation uses `ConfirmsPeer` and requires one unambiguous address. Overlapping sets of
several addresses cannot confirm an owner or consume its demotion fence. Duplicate evidence
keeps existing immutable storage; unions produce a new snapshot without assigning chronology.

Hints retain immutable source, target, reporter, and down-report collections.
`SentinelNotificationState` returns a new value for each coalescing operation. Lookup
records use an immutable dictionary keyed by lookup lifetime, so later offers and lookup
completion cannot change an earlier state snapshot.

Each hint also carries a `SentinelReporterLedger`. It retains the original switch,
down, or gap observations with their reporter, source/target, validated owner at receipt,
observed and accepted epoch facts, and completed source/target DNS evidence. Context is captured
once under the router gate. A later publication or DNS-triggered requeue cannot replace
an observation's owner or fill in an owner that was absent at receipt.

The ledger's cached down-report projection supplies reporter-specific outage reconciliation.
Mixing an independent gap or switch suppresses that projection on the effective hint,
while retaining the original observations in the ledger. Consuming a source fence after
validation likewise leaves its original observation intact. Unique owner, epoch, or DNS
evidence from an existing reporter remains pending; identical evidence reuses immutable
storage. Neither ledger order nor DNS completion order establishes failover chronology.

`SentinelValidatedPrimary` keeps the advertised endpoint separate from the physical peer
accepted by ROLE. Discovery retains provisional DNS evidence separately from its accepted
peer. At an observed epoch, a numeric alias can confirm that peer even when DNS is unavailable;
an unchanged hostname cannot replace it without a newer epoch. Retained down reports keep
their observation-time owner when later DNS or publication changes the current owner.

## Background work ownership

| Work | Owner and shutdown contract |
| --- | --- |
| Monitor supervisor, endpoint monitors, and removed-monitor cleanup | `SentinelBackgroundWork` registers every task under the router gate. `SentinelMonitoring` owns subscription resources, reconnect episodes, parsing, transport/clock/resolver seams, and publication rearm signals. Removed endpoints receive individual cancellation; their cleanup remains registered until completion. |
| Notification rediscovery and switch-source DNS tasks | The same background owner registers these tasks while the router retains generation-sensitive evidence. Disposal sets `_disposed` and stops registration atomically, then cancels `_lifetime`. All background tasks and cancellation callbacks share one ten-second shutdown bound. A straggler must recheck disposal and its endpoint cancellation before publication or retirement. Successful tasks are released; the last eight completed failures per work kind remain available for aggregate shutdown reporting, with a count of omitted earlier failures. Active tasks are never evicted by this history limit. |
| Generation retirement and correction-fence drainage | Each owned generation retains its retirement task. Disposal starts cleanup for every owned connection/pool, then joins retirement tasks and propagates aggregated failures. Failed cleanup stays owned until disposal. |
| State/health observer callbacks | Serialized in `_notifications`, outside publication locks. Pending application callbacks are suppressed after disposal; explicitly retained telemetry callbacks may still run. This chain is not joined because an active observer can synchronously dispose the client itself. |

Shutdown initiates both monitor-client and subscription cleanup even when one fails or
does not complete. Cleanup collects every failure before recovery policy is applied;
shutdown failures propagate after generation cleanup. A timed-out background join still
observes late faults. The internal monitor transport seam and shutdown clock allow tests
to exercise cancellation-ignoring clients without private task-collection reflection.

Discovery signals both additions and explicit removal of learned endpoints. Configured
endpoints cannot be removed, and the limit remains 64 learned endpoints. Removing membership
does not erase primary/epoch evidence. No age-based pruning occurs here. A removed monitor
cannot submit late messages or readiness, and re-adding its endpoint creates a new monitor
with an independent delivery gap. Membership versions preserve this restart even when removal
and re-addition occur between supervisor snapshots. Subscription history and reporter validation versions
remain available across that restart.

Address resolution checks cancellation and monitor ownership before starting and after its
resolver returns. DNS startup shares the disposal gate, while awaiting completion releases it.
A cancellation-ignoring source lookup cannot initiate target lookups after shutdown, even
before asynchronous linked-token callbacks run. Generation
invalidation checks disposal before changing retirement state; late responses cannot retire a
generation while background shutdown is still joining its tasks.

First-subscription acknowledgements advance a monitor version. Discovery captures that version
before its network lookup and marks it validated only for the reporter whose primary was
accepted. An initial gap from that reporter can reuse the healthy generation without another
ROLE pass. Other reporters remain unvalidated even if they attached before the lookup began;
their views may disagree. A subscription that attaches during the lookup still requires a later pass. Merging an
initial gap with any real switch, down event, reconnect, or overflow gap clears this shortcut;
those events keep their independent rediscovery requirements.

A restarted monitor task does not turn its endpoint's next subscription into a first
subscription: that endpoint has already been observed, so the restart reports an independent
delivery gap. Gap callbacks run outside the shared gate with the startup version captured
under it; user logging cannot hold the publication/disposal gate. The router rechecks disposal
when consuming a late callback before queuing discovery or changing a generation.

`WaitForSubscriptionsAsync` waits for the requested number of ready endpoints through a
membership/readiness signal. It supports caller cancellation and wakes on shutdown. Readiness
is published only after the delivery-gap callback successfully queues validation. Tests then
join startup rediscovery separately; subscription readiness alone does not prove validation
has completed. The shared test helper no longer polls a subscription count.

The pure reducer remains tracked by #727 and must preserve these joining and reentrancy contracts.

## State transitions

Transition inputs cover notification offers, attempt preparation and success/failure,
source-lookup registration/completion, and disposal. Time, retry policy, jitter samples,
the exact returned generation, and validated owner/peer facts are explicit inputs.
The reducer returns `Stop`, `RunNext`, or `RetryAfter(delay)` for worker decisions;
evidence-only changes return `None`. The router replaces pending signals atomically with
state changes, then executes discovery, waits, and retirement effects.

`SentinelGenerationEvidence` captures the generation identity, endpoint, validated peer,
retirement state, and accepting command peers. Source and cycle matching may use any
captured peer; skipping discovery requires one confirmed peer for every command slot.
Each generation owns one `SentinelGenerationIdentity` token with no transport or mutable
state. Reducer inputs and retirement effects accept only this token type.
The reducer returns the exact generation identity to retire. Under the same gate, the
router verifies that identity is still current and disposal has not begun, then performs
retirement. No live transport is read by the reducer.

Source DNS completion includes the collected target DNS answers and snapshots of the
current and lookup generations. The reducer filters unambiguous target overlap while
preserving a known demoted peer, checks intervening-generation protection, and decides
whether to queue rediscovery. The ledger retains the actual DNS answers separately from
filtered source fences. An empty filtered result does not manufacture demotion evidence;
an ambiguous target answer cannot prove that the source is the promoted owner.

Retry attempts and consecutive failures are separate state fields. Success resets both;
pending hints spend the current retry budget. Exhaustion completes active and pending
work. Without a policy, retries remain unlimited and backoff caps at 30 seconds. The
100 ms minimum-discovery deadline survives worker completion and restart. Notifications
can interrupt policy backoff but cannot bypass that shared deadline.
The router obtains monotonic timestamps and schedules spacing delays through its injected
`TimeProvider`, the same provider used for policy backoff. A timer wake-up rechecks the
deadline before starting discovery, including when a test timer fires early.

| Attempt outcome | Retry attempts | Consecutive failures | Next action |
| --- | --- | --- | --- |
| No active hint | Unchanged | Unchanged | Stop. |
| Success superseded by another generation | Reset to zero | Reset to zero; report the prior failure count | Run genuine pending work, otherwise stop. |
| Success for the current generation | Reset to zero | Reset to zero; report the prior failure count | Reconcile pending work, then run or stop. |
| Failure with exhausted policy | Unchanged; exhaustion is checked before increment | Increment, saturating at `int.MaxValue` | Clear active/pending work and stop. |
| Failure with retry budget and no pending hint | Increment, saturating at `int.MaxValue` | Increment, saturating at `int.MaxValue` | Wait for interruptible policy backoff. |
| Failure with retry budget and pending hint | Increment, saturating at `int.MaxValue` | Increment, saturating at `int.MaxValue` | Run pending work, or stop if its target is already confirmed and it does not require rediscovery. |

Completing a worker clears its hints, not its counters or spacing deadline. Starting a
new idle worker resets both counters. Taking pending work during backoff preserves them.

Recovery logging remains outside the gate and precedes reconciliation. Since callbacks
can permit a competing publication, the router captures the current generation after
logging returns. Supersession compares that identity with the exact discovery result;
matching endpoint text alone is insufficient.

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
