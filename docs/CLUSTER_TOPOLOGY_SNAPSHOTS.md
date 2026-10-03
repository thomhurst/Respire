# Cluster routing snapshots

`ClusterRouter.RoutingSnapshot` publishes primary owners, per-slot replica membership,
primary and replica node summaries, primary slot counts, and completeness through one
volatile reference. A reader captures that reference once when it needs related fields.
The slot index returns the primary and replica set from the same publication. An older
captured snapshot keeps its membership even after another publication replaces it.

## Publication and ordering

The router node gate serializes writers. `_slots`, `_replicasBySlot`, and the node summary
arrays are writer staging, not reader-visible routing state. Discovery resolves identities,
applies per-slot redirect and snapshot-batch fences, retains uncovered slots when requested,
and prepares both roles before `PublishTopologyLocked` changes the reader reference.
MOVED, slot clearing, and an entire SMIGRATED batch use that same publication boundary.

Direct-route versions, owner mutation tokens, discovery generations, and snapshot-batch
markers remain separate ordering mechanisms. A snapshot publication does not turn discovery
into a direct-route mutation or erase an ABA fence. Same-owner MOVED still advances the
discovery fence and invalidates replica coverage. A stale discovery cannot replace either
half of a redirected slot. SMIGRATED retains its receive-time owner-change fence and queued
dependency processing.

The snapshot uses 64 pages of 256 slots. A writer copies each changed page once; all other
pages remain shared with the previous snapshot. Unchanged publications share every page.
One point update therefore does not copy the entire slot table or allocate a large object.
Readers allocate nothing to capture a snapshot or select a slot.

## Membership versus coordination

Snapshot pages and their membership arrays are immutable after publication. Internal node
summary arrays are publication-owned and must only be enumerated. Primary slot counts are
copied because the writer updates those counts in place.

Transport connection state, identity maps, correction fences, dedicated leases, and retirement
tasks remain outside the snapshot. A reference in a historical snapshot does not grant new
admission to a retired transport. Accepted work still drains before disposal.

`ClusterReplicaSet.Nodes` is immutable membership. Its shared round-robin cursor, revalidation
deadline, and refresh throttle remain live coordination state, not historical topology. An
unchanged range retains its set so publishing topology does not reset selection or refresh
budgets. Snapshot retention neither owns a connection nor freezes that coordination state.

The unknown-slot coordinator from #731 also remains outside the snapshot. It joins refresh
work without combining different slots' coverage attempts. Covered slots are forgotten only
after the new snapshot is visible. Empty replies remain deferred until competing probes
cannot provide nonempty coverage; promotion removes only the promoted sibling replica.

## Retirement and cursors

Replica membership is retained across uncovered slots and shared ranges. A replica leaves
routing only when it is absent from the resulting membership. MOVED now removes the final
unrouted replica transport as well as clearing the moved slot's coverage; removing one of
several covered slots does not retire the shared transport. Retirement uses the existing
drain and correction-fence machinery. Primary and replica roles keep distinct transports,
including when one endpoint changes role or serves both roles for different slots.

Pinned reads check the primary and replica membership from one slot record. A cursor retains
its exact transport while that transport remains in the slot's topology; a removed transport
cannot silently continue through a replacement owner. Existing in-flight work keeps its drain
guarantees.

## Verification

`ClusterRoutingSnapshotTests` covers retained snapshots, stale discovery after MOVED,
concurrent readers during publication, final-slot replica retirement, and allocation-free
selection with a positive allocation control. Existing Cluster tests cover same-endpoint
promotion, aliases, partial and deferred empty replies, independent unknown-slot coverage,
SMIGRATED ordering, cursor affinity, and transport retirement. Real Redis Cluster replica
tests exercise RESP2 and RESP3 on both target frameworks.

### Local performance measurements

BenchmarkDotNet 0.15.8, .NET 10.0.12, Windows 11, and an Intel Core i7-12700K
produced the following diagnostic measurements using the in-process emit toolchain.
The temporary benchmarks used normal measurement iterations and `MemoryDiagnoser`.

| Operation | Mean | Allocated per operation |
| --- | ---: | ---: |
| Separate primary/replica array lookup | 0.337 ns | 0 B |
| Snapshot slot lookup | 0.792 ns | 0 B |
| Direct cached connection lookup | 1.595 ns | 0 B |
| Healthy Cluster connection dispatch | 29.921 ns | 0 B |
| Publish with no changed pages | 32.45 ns | 88 B |
| Publish with one changed page | 863.30 ns | 4,744 B |
| Publish with all 64 pages changed | 49.30 us | 264,304 B |

Publication measurements isolate the snapshot copy mechanism with empty node summaries;
they exclude identity reconciliation, summary construction, and transport retirement.
Dispatch uses an already connected transport and excludes command serialization and network
latency. Subnanosecond lookups are close to the measurement overhead floor; the useful result
is allocation-free lookup with a small absolute cost, not a precise ratio. These local
measurements are not cross-machine performance guarantees. The in-process toolchain avoids
generated-project ambiguity with the repository's existing benchmark assembly name.
