# Orleans provider assessment

The provider packages are pinned to Orleans **10.2.2**, tag `v10.2.2`, commit
`a575888773a3b01b079c9c3720bb1553c3169997` in
[dotnet/orleans](https://github.com/dotnet/orleans/tree/a575888773a3b01b079c9c3720bb1553c3169997).
The upstream code is Copyright .NET Foundation and Contributors, MIT licensed;
see `LICENSE.upstream`.

`ProviderScenarios.cs` adapts these named upstream scenarios to TUnit and the
repository's Redis Testcontainers fixture. The production providers come from
the pinned NuGet packages: no provider implementation is copied or replaced.

| Local scenario | Upstream scenario/source under the pinned commit |
| --- | --- |
| `Membership_ReadRow_Insert_Read` | `MembershipTable_ReadRow_Insert_Read` in `test/Orleans.Runtime.Internal.Tests/MembershipTests/MembershipTableTestsBase.cs` |
| `Membership_UpdateRow_UpdateIAmAlive_Cleanup` | Selected contracts from `MembershipTable_UpdateRow`, `MembershipTable_UpdateIAmAlive`, and `MembershipTable_CleanupDefunctSiloEntries` in that base |
| `Membership_GetGateways` | `MembershipTable_GetGateways` in that base |
| `Storage_WriteRead` | `CommonStorageTests.Store_WriteRead` in `test/Orleans.Runtime.Internal.Tests/StorageTests/CommonStorageTests.cs`, called by `RedisStorageTests.StorageDataSetGeneric_WriteRead` |
| `Storage_Clear` | `CommonStorageTests.Store_WriteClearRead` and the delete-state-on-clear variant in `test/Extensions/Orleans.Redis.Tests/Persistence`; selects the clear operation and its subsequent read |
| `Reminders_ReminderSimple` | `ReminderSimple` in `test/Orleans.Runtime.Internal.Tests/RemindersTest/ReminderTableTestsBase.cs`, called by `RedisRemindersTableTests.RemindersTable_Redis_ReminderSimple` |
| `Reminders_ReadRows` | Selected grain/range read contracts from `RemindersRange` in that base |
| `Reminders_RemoveRow` | The stale/current/repeated removal contracts in `ReminderSimple` |

Adaptations: deterministic grain IDs and membership entries, unique service and
cluster IDs per client, an integer persistence state, three combined membership
operations, and no Orleans silo or grain activation. Storage initialization uses
the upstream `CommonFixture.CreateRedisGrainStorage` lifecycle pattern. The
assembly name grants the same internal-provider access as the upstream suite.
The storage fixture uses Orleans' binary serializer; JSON serialization is not
assessed. Both clients borrow their multiplexer (`IsShared = true`).

Each scenario first succeeds with StackExchange.Redis, then runs with the shim
against the same real Redis server and a separate provider namespace. Six cases
require the provider's exact exception type and the specific unsupported member
in its message. These are **confirmed incompatibilities, not passing provider
conformance**. Independent control seeding reaches persistence clear and reminder
read/removal paths despite the earlier shim write failure. No production calls
fall back to the control client.

Nine cases run per framework/protocol combination: three successful clustering
scenarios and six gap reproductions. This is a selected assessment, not the full
upstream suite. Parallel membership updates, reminder parallel upserts and large
hash-ring distributions, all persistence datasets/serializers, silo integration,
provider-owned multiplexer shutdown, reconnect/failover, and Cluster routing are
not validated. The three unsupported methods prevent full persistence/reminder
acceptance; adding support must replace the gap expectations with successful
upstream assertions and expand coverage before claiming compatibility.
