# Orleans provider assessment

The provider packages are pinned to Orleans **10.2.2**, tag `v10.2.2`, commit
`a575888773a3b01b079c9c3720bb1553c3169997` in
[dotnet/orleans](https://github.com/dotnet/orleans/tree/a575888773a3b01b079c9c3720bb1553c3169997).
The upstream code is Copyright .NET Foundation and Contributors, MIT licensed;
see `LICENSE.upstream`.

`ProviderScenarios.cs` adapts these named upstream scenarios to TUnit and the
repository's Redis Testcontainers fixture. The production providers come from
the pinned NuGet packages: no provider implementation is copied or replaced.

The project updates all three central `PackageVersion` items to **10.2.2**.
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

This is selected upstream acceptance with additional adapter regressions, not the
full upstream suite. Parallel membership updates, sustained parallel stress,
large hash-ring distributions, all persistence datasets/serializers, silo
integration, provider-owned multiplexer shutdown, reconnect/failover, and Cluster
routing remain unvalidated.
