# Orleans provider assessment

The provider packages are pinned to Orleans **10.2.2**, tag `v10.2.2`, commit
`a575888773a3b01b079c9c3720bb1553c3169997` in
[dotnet/orleans](https://github.com/dotnet/orleans/tree/a575888773a3b01b079c9c3720bb1553c3169997).
The upstream code is Copyright .NET Foundation and Contributors, MIT licensed;
see `LICENSE.upstream`.

`ProviderScenarios.cs` adapts these named upstream scenarios to TUnit and the
repository's Redis Testcontainers fixture. The production providers come from
the pinned NuGet packages: no provider implementation is copied or replaced.

The project updates all three central provider `PackageVersion` items to **10.2.2**
and pins `Microsoft.Orleans.Server` to **10.2.2**, including its runtime, SDK and grain code generator.
Repository defaults are **10.4.0**, but those defaults do not change this
assessment's provider or runtime versions. Renovate excludes these project pins;
changing them requires a new source inventory and compatibility assessment.

| Local scenario | Upstream scenario/source under the pinned commit |
| --- | --- |
| `Membership_ReadRow_Insert_Read` | `MembershipTable_ReadRow_Insert_Read` in `test/Orleans.Runtime.Internal.Tests/MembershipTests/MembershipTableTestsBase.cs` |
| `Membership_UpdateRow_UpdateIAmAlive_Cleanup` | Selected contracts from `MembershipTable_UpdateRow`, `MembershipTable_UpdateIAmAlive`, and `MembershipTable_CleanupDefunctSiloEntries` in that base |
| `Membership_GetGateways` | `MembershipTable_GetGateways` in that base |
| `Storage_WriteRead` | `CommonStorageTests.Store_WriteRead` in `test/Orleans.Runtime.Internal.Tests/StorageTests/CommonStorageTests.cs`, called by `RedisStorageTests.StorageDataSetGeneric_WriteRead` |
| `Storage_Clear` | `CommonStorageTests.Store_WriteClearRead` and the delete-state-on-clear variant in `test/Extensions/Orleans.Redis.Tests/Persistence`; selects the clear operation and its subsequent read |
| `Storage_StaleETagRejectsWriteAndClear` | The duplicate/inconsistent ETag contracts in `CommonStorageTests.PersistenceStorage_WriteDuplicateFailsWithInconsistentStateException` and `PersistenceStorage_WriteInconsistentFailsWithInconsistentStateException`, extended to stale clear and rewrite after both clear modes |
| `Storage_ConcurrentWritesHaveOneWinner` | Additional adapter regression: 16 writes sharing one ETag must leave exactly one winner |
| `Storage_ExpiryIsAppliedAtomically` | Additional adapter regression: configured TTL and binary payload survive the provider write script |
| `Storage_BinaryStateInteroperatesWithOfficialClient` | `CommonStorageTests.Store_WriteRead`/`Store_WriteClearRead` contracts extended to binary byte-list payloads and bidirectional client interoperability |
| `Reminders_ReminderSimple` | `ReminderSimple` in `test/Orleans.Runtime.Internal.Tests/RemindersTest/ReminderTableTestsBase.cs`, called by `RedisRemindersTableTests.RemindersTable_Redis_ReminderSimple` |
| `Reminders_ReadRows` | Selected grain/range read contracts from `RemindersRange` in that base |
| `Reminders_RemoveRow` | The stale/current/repeated removal contracts in `ReminderSimple` |
| `Reminders_InteroperateWithOfficialClient` | `ReminderSimple` contracts extended to bidirectional client interoperability |
| `Reminders_RangePartitionsAndGrainFilters` | `RemindersRange`/`TestRemindersHashInterval` contracts with 32 deterministic grain IDs, multiple reminder names, and explicit inclusive/exclusive ring boundaries |
| `Reminders_ConcurrentUpsertsLeaveOneReplacement` | `RemindersParallelUpsert` contract adapted to 16 concurrent replacements of one reminder, with stale ETag removal assertions |

Adaptations: deterministic grain IDs and membership entries, unique service and
cluster IDs per client, integer and binary persistence states, three combined membership
operations, and no Orleans silo or grain activation. Storage initialization uses
the upstream `CommonFixture.CreateRedisGrainStorage` lifecycle pattern. The
assembly name grants the same internal-provider access as the upstream suite.
The storage fixture uses Orleans' binary serializer; JSON serialization is not
assessed. Both clients borrow their multiplexer (`IsShared = true`).

Each scenario first succeeds with StackExchange.Redis, then requires the same
behavior from the shim against the same real Redis server and a separate provider
namespace. No scenario expects unsupported-member failures, and no production
calls fall back to the control client. Explicit interoperability cases read/update
one namespace with both clients to verify the unchanged storage format.

Eighteen cases run per framework/protocol combination: three clustering, nine
persistence, and six reminder cases. The expiry case inspects TTL and payload
without waiting for expiration. Byte-array state exercises binary write/read;
byte-list state exercises both clear modes because the upstream activator cannot
create an empty byte array after clear.

`SiloScenarios.cs`, `TopologyScenarios.cs` and `LifecycleGrain.cs` add nine original adapter-only integration
cases (not ports of upstream tests). They use real Orleans silo/client hosts and
the existing `RespireContainerFixture` with Redis 7.0.15:

- Standalone shared and provider-owned connections: Redis membership, persisted
  integer state, actual reminder callbacks, graceful shutdown, restart under the
  same service/cluster IDs, and reminder-driven reactivation. Provider-owned shutdown
  checks that both adapter and native client reject further use; the client host's
  gateway provider remains usable after the silo has been disposed.
- Three-primary Cluster, database 0: the same shared lifecycle, with native routing.
- Standalone reconnect: kill actual Redis client sockets, reuse the same adapter,
  and verify state writes, reminder callbacks, a second silo joining, and persisted
  state in a new activation.
- Sentinel: fence replication, stop the primary, observe connection to the promoted
  replica, continue provider operations, join a second silo, return the old primary
  as a replica, and read persisted state from a new activation.
- Cluster migration (two cases): transfer the membership key, publish its new slot owner,
  and join a silo through the existing adapter. The ASK variant inserts and updates a row,
  rejects duplicate inserts and stale versions without partial writes, and updates its
  heartbeat while migration remains open.
- Six-node Cluster: coordinated promotion of all three replicas while a silo runs,
  followed by state/reminder operations, another silo joining and reactivation there.
  This uses the test-only `ClusterDeployment`, also pinned to Redis 7.0.15.
- Abrupt silo death: `Program.cs` starts a child silo without entering the test runner;
  kill that process and require the surviving silo to mark it Dead within 60 seconds.
  Log elapsed detection time and verify the survivor can serve a grain. Test-only
  membership options use one-second probes/refreshes/heartbeats, two missed probes,
  and one death vote. These are not production latency guarantees.

Every deployment is owned by Testcontainers and disposed even on failure. The
administrative fault/fence connection does not execute any provider operations.
The suite now has **27 cases per framework/protocol combination**: 18 provider
cases and nine real-host cases. CI runs all cases on net8.0/net10.0 and RESP2/RESP3.

This is selected upstream acceptance with additional adapter regressions, not the
full upstream suite. Sustained parallel stress, large hash-ring distributions,
all persistence datasets/serializers, multi-host partitions, unplanned Cluster primary
crashes, failover racing provider transactions, and provider-owned topology recovery remain
unvalidated. Nonzero Cluster databases and combined Cluster/Sentinel configuration
are unsupported. ASK membership coverage does not promise uninterrupted availability
when topology changes race an already established watched transaction.
Multi-host partitions cannot be represented by this CI fixture: Redis processes share
one container and silos advertise loopback. Independent addresses and selective network
fault injection are prerequisites; killing a process or its TCP connections is not a
substitute. See the website guide for the exact topology and recovery limits.
