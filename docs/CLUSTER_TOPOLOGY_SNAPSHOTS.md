# Cluster routing snapshots

Reader-visible Cluster routing state lives only in `ClusterRouter.RoutingSnapshot`. Readers capture
the volatile reference once and read related fields from that capture. Writers change it only through
`PublishTopologyLocked` while holding the router node gate.

The snapshot holds primary owners, per-slot replica membership, primary and replica node summaries,
primary slot counts and completeness. The slot index returns the primary and replica set from the
same publication. A captured snapshot keeps its membership after a newer publication replaces it.

## Writer rules

- `_slots`, `_replicasBySlot` and the node summary arrays are writer staging, never read by readers.
- Discovery resolves identities, applies per-slot redirect and snapshot-batch fences, keeps uncovered
  slots when requested, and prepares both roles before publishing. MOVED, slot clearing and a whole
  SMIGRATED batch use the same publication boundary.
- Direct-route versions, owner mutation tokens, discovery generations and snapshot-batch markers stay
  separate ordering mechanisms. Publishing does not erase an ABA fence.
- Same-owner MOVED still advances the discovery fence and invalidates replica coverage. A stale
  discovery cannot replace either half of a redirected slot. SMIGRATED keeps its receive-time
  owner-change fence and queued dependency processing.
- Replace node summary arrays instead of mutating them; publication wraps them without copying.
  Primary slot counts are copied, because the writer updates them in place. Debug builds assert that
  publication holds the node gate.

## Copy-on-write layout

- 64 pages of 256 slots. A writer copies each changed page once; other pages are shared.
- When pages, node summary references, slot counts (compared by value) and completeness are all
  unchanged, publication reuses the snapshot itself.
- One point update copies one page, not the whole table, and allocates no large object.
- Readers allocate nothing to capture a snapshot or select a slot.
- Pages and membership arrays are immutable after publication. Node summaries and slot counts are
  exposed as `ImmutableArray`.

## State outside the snapshot

These stay live and are not frozen by an old snapshot:

- Transport connection state, identity maps, correction fences, dedicated leases and retirement tasks.
  A reference in an old snapshot does not admit new work to a retired transport. Accepted work still
  drains before disposal.
- `ClusterReplicaSet` coordination: its round-robin cursor, revalidation deadline and refresh
  throttle. `Nodes` is immutable membership. An unchanged range keeps its set, so publishing does not
  reset selection or refresh budgets.
- The unknown-slot coordinator. It joins refresh work without merging different slots' coverage
  attempts, forgets covered slots only after the new snapshot is visible, defers empty replies until
  competing probes cannot give nonempty coverage, and on promotion removes only the promoted sibling.

## Retirement and cursors

- A replica leaves routing only when it is absent from the resulting membership. Membership is kept
  across uncovered slots and shared ranges.
- MOVED clears the moved slot's coverage and removes the last unrouted replica transport. Removing one
  of several covered slots does not retire a shared transport.
- Retirement uses the existing drain and correction-fence machinery.
- Primary and replica roles keep distinct transports, even when one endpoint changes role or serves
  both roles for different slots.
- Pinned reads check primary and replica membership from one slot record. A cursor keeps its exact
  transport while it stays in the slot's topology; a removed transport cannot continue through a
  replacement owner.

## Verification

- `ClusterRoutingSnapshotTests`: retained snapshots, stale discovery after MOVED, concurrent readers,
  final-slot replica retirement, and allocation-free selection with a positive control. A test-only
  oracle rebuilds all pages from writer staging and compares every slot after discovery, MOVED and
  clearing at page boundaries, in Debug and Release.
- Existing Cluster tests: same-endpoint promotion, aliases, partial and deferred empty replies,
  unknown-slot coverage, SMIGRATED ordering, cursor affinity and transport retirement.
- Real Redis Cluster replica tests run RESP2 and RESP3 on both frameworks.

### Local performance measurements

BenchmarkDotNet 0.15.8, .NET 10.0.12, Windows 11, Intel Core i7-12700K, in-process emit toolchain,
`MemoryDiagnoser`. Diagnostic only, not a cross-machine guarantee.

| Operation | Mean | Allocated per operation |
| --- | ---: | ---: |
| Separate primary/replica array lookup | 0.337 ns | 0 B |
| Snapshot slot lookup | 0.792 ns | 0 B |
| Direct cached connection lookup | 1.595 ns | 0 B |
| Healthy Cluster connection dispatch | 29.921 ns | 0 B |
| Reuse with no changed pages or metadata | 2.909 ns | 0 B |
| Publish with one changed page | 783.406 ns | 4,744 B |
| Publish with all 64 pages changed | 45.00 us | 264,304 B |

- Publication rows use empty summaries and exclude identity reconciliation, summary construction and
  transport retirement.
- Dispatch uses a connected transport and excludes serialization and network latency.
- Subnanosecond lookups are near the measurement floor. The useful result is allocation-free lookup at
  small absolute cost, not a precise ratio.
