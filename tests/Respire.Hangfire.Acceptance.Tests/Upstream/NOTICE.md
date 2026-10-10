# Pinned Hangfire upstream tests

These files adapt `Hangfire.Redis.Tests` from
[Hangfire.Redis.StackExchange commit da8e39a33df204900afc30aeb65110f76f081c55](https://github.com/marcoCasamento/Hangfire.Redis.StackExchange/tree/da8e39a33df204900afc30aeb65110f76f081c55/Hangfire.Redis.Tests).
This is the repository commit in the NuGet metadata for `Hangfire.Redis.StackExchange` 1.12.0.
The tests execute that NuGet package, not a rebuilt or modified copy of its production source.

Copyright © 2015 Marco Casamento. The original product is by Sergey Odinokov.
The upstream package declares `LGPL-3.0-or-later`; the copied upstream notice is
in `License.md`, with the GNU LGPLv3 and its GPLv3 companion in `COPYING.LESSER` and `COPYING`.
These upstream-derived files retain those terms; the repository's MIT license does not replace them.

## Adaptations

- Replace xUnit discovery with TUnit, retaining xUnit assertions and the original method names.
- Run all classes sequentially with an isolated Testcontainers Redis database per test.
  This replaces `CleanRedisAttribute` and its global database flushes.
- Inject the Respire shim into the pinned production `RedisStorage`, `RedisConnection`,
  `RedisFetchedJob`, `RedisWriteOnlyTransaction`, `RedisLock`, and `RedisSubscription` paths.
  `RedisUtils.Connection`, `ShimDatabase`, `GetFirstServer`, and `CreateSubscriber` always use the shim.
- Use an independent StackExchange.Redis connection only for upstream arrangement and state assertions
  (`RedisUtils.CreateClient`). This preserves test-only calls such as `ListRightPop`, `HashExists`,
  `SortedSetRank`, `SetContains`, and newer `StringSet` overloads without advertising shim support for them.
  The smoke test and all lock operations explicitly use `ShimDatabase`.
- Retain the upstream mocks for argument checks and state-handler callback verification.
  They are not used in place of production Redis behavior.
- Dispose both clients and the watcher/subscription cancellation sources after each test.
- Preserve the subscription timeout's 99 ms lower bound, removing its 120 ms scheduling
  ceiling so a loaded host cannot fail a correct wait merely by resuming the thread late.
- Capture exceptions in lock-test thread callbacks, release waiters in `finally`, and
  rethrow on the test thread after joining. Dispose the synchronization events after joining.
  The upstream lock assertions and thread-based ownership checks remain intact.
- Use assembly name `Hangfire.Redis.Tests` to consume the package's existing `InternalsVisibleTo` grant.
  No production assembly, reflection workaround, or successful default replaces an unsupported call.

## Exclusions and upstream limitations

The upstream revision has 96 active facts. This port executes 93 of them.
The three excluded `RedisStorageFacts` methods are `DbFromConnectionStringIsUsed`,
`PasswordFromToStringIsNotShown`, and `PasswordFromWriteOptionsToLogIsNotShown`.
They construct storage from a string, which creates StackExchange.Redis internally and cannot test an injected shim.
These constructors remain upstream StackExchange.Redis behavior, outside the injected shim contract.

`RedisLockFacts.AcquireFromNestedTask` is already commented out upstream because its thread-local
ownership model does not support that scenario. It remains commented out here; it is not one of the 96 active facts.
The commented `NestedTask` helper also remains unchanged.
The upstream expired-job deletion test writes `succeded`, while it asserts `succeeded`;
that original assertion is preserved and does not establish cleanup of the succeeded list.
The additional real worker lifecycle test does verify the succeeded job and monitoring entry.

The exact exercised methods are listed below. The two cases in `ServerLifecycleTests` are additional
Respire tests, not upstream facts. Condition aborts, WATCH races, and queue-time versus EXEC errors are
covered independently by `Respire.StackExchangeCompat.Tests.TransactionTests` against real Redis.

## Exercised upstream methods

### DeletedStateHandlerFacts (3)

- `StateName_ShouldBeEqualToSucceededState`
- `Apply_ShouldInsertTheJob_ToTheBeginningOfTheSucceededList_AndTrimIt`
- `Unapply_ShouldRemoveTheJob_FromTheSucceededList`

### ExpiredJobsWatcherFacts (5)

- `Ctor_ThrowsAnException_WhenStorageIsNull`
- `Ctor_ThrowsAnException_WhenCheckIntervalIsZero`
- `Ctor_ThrowsAnException_WhenCheckIntervalIsNegative`
- `Execute_DeletesNonExistingJobs`
- `Execute_DoesNotDeleteExistingJobs`

### FailedStateHandlerFacts (3)

- `StateName_ShouldBeEqualToFailedState`
- `Apply_ShouldAddTheJob_ToTheFailedSet`
- `Unapply_ShouldRemoveTheJob_FromTheFailedSet`

### FetchedJobsWatcherFacts (7)

- `Ctor_ThrowsAnException_WhenStorageIsNull`
- `Ctor_ThrowsAnException_WhenInvisibilityTimeoutIsZero`
- `Ctor_ThrowsAnException_WhenInvisibilityTimeoutIsNegative`
- `Execute_EnqueuesTimedOutJobs_AndDeletesThemFromFetchedList`
- `Execute_MarksDequeuedJobAsChecked_IfItHasNoFetchedFlagSet`
- `Execute_EnqueuesCheckedAndTimedOutJob_IfNoFetchedFlagSet`
- `Execute_DoesNotEnqueueTimedOutByCheckedFlagJob_IfFetchedFlagSet`

### ProcessingStateHandlerFacts (3)

- `StateName_ShouldBeEqualToProcessingState`
- `Apply_ShouldAddTheJob_ToTheProcessingSet`
- `Unapply_ShouldRemoveTheJob_FromTheProcessingSet`

### RedisConnectionFacts (18)

- `GetStateData_ThrowsAnException_WhenJobIdIsNull`
- `GetStateData_ReturnsNull_WhenJobDoesNotExist`
- `GetStateData_ReturnsCorrectResult`
- `GetStateData_ReturnsNullReason_IfThereIsNoSuchKey`
- `GetAllItemsFromSet_ThrowsAnException_WhenKeyIsNull`
- `GetAllItemsFromSet_ReturnsEmptyCollection_WhenSetDoesNotExist`
- `GetAllItemsFromSet_ReturnsAllItems`
- `SetRangeInHash_ThrowsAnException_WhenKeyIsNull`
- `SetRangeInHash_ThrowsAnException_WhenKeyValuePairsArgumentIsNull`
- `SetRangeInHash_SetsAllGivenKeyPairs`
- `GetAllEntriesFromHash_ThrowsAnException_WhenKeyIsNull`
- `GetAllEntriesFromHash_ReturnsNullValue_WhenHashDoesNotExist`
- `GetAllEntriesFromHash_ReturnsAllEntries`
- `GetUtcDateTime_ReturnsValidDateTime`
- `SetCount_ReturnZeroIfSetDoesNotExists`
- `SetCount_ReturnNumberOfItems`
- `SetContains_ReturnTrueIfContained`
- `SetContains_ReturnFalseIfNotContained`

### RedisFetchedJobFacts (16)

- `Ctor_ThrowsAnException_WhenStorageIsNull`
- `Ctor_ThrowsAnException_WhenRedisIsNull`
- `Ctor_ThrowsAnException_WhenJobIdIsNull`
- `Ctor_ThrowsAnException_WhenQueueIsNull`
- `RemoveFromQueue_RemovesJobFromTheFetchedList`
- `RemoveFromQueue_RemovesOnlyJobWithTheSpecifiedId`
- `RemoveFromQueue_DoesNotRemoveIfFetchedDoesntMatch`
- `RemoveFromQueue_RemovesTheFetchedFlag`
- `RemoveFromQueue_RemovesTheCheckedFlag`
- `Requeue_PushesAJobBackToQueue`
- `Requeue_PushesAJobToTheRightSide`
- `Requeue_RemovesAJobFromFetchedList`
- `Requeue_RemovesTheFetchedFlag`
- `Requeue_RemovesTheCheckedFlag`
- `Dispose_WithNoComplete_RequeuesAJob`
- `Dispose_AfterRemoveFromQueue_DoesNotRequeueAJob`

### RedisLockFacts (4)

- `AcquireInSequence`
- `AcquireNested`
- `AcquireFromMultipleThreads`
- `SlidingExpirationTest`

### RedisStorageFacts (1)

- `GetStateHandlers_ReturnsAllHandlers`

### RedisStorageOptionsFacts (1)

- `InvisibilityTimeout_HasDefaultValue`

### RedisSubscriptionFacts (3)

- `Ctor_ThrowAnException_WhenStorageIsNull`
- `Ctor_ThrowAnException_WhenSubscriberIsNull`
- `WaitForJob_WaitForTheTimeout`

### RedisTest (1)

- `RedisSampleTest`

### RedisWriteOnlyTransactionFacts (25)

- `Ctor_ThrowsAnException_WhenStorageIsNull`
- `Ctor_ThrowsAnException_WhenTransactionIsNull`
- `ExpireJob_SetsExpirationDateForAllRelatedKeys`
- `SetJobState_ModifiesJobEntry`
- `SetJobState_RewritesStateEntry`
- `SetJobState_AppendsJobHistoryList`
- `PersistJob_RemovesExpirationDatesForAllRelatedKeys`
- `AddJobState_AddsJobHistoryEntry_AsJsonObject`
- `AddToQueue_AddsSpecifiedJobToTheQueue`
- `AddToQueue_PrependsListWithJob`
- `IncrementCounter_IncrementValueEntry`
- `IncrementCounter_WithExpiry_IncrementsValueAndSetsExpirationDate`
- `DecrementCounter_DecrementsTheValueEntry`
- `DecrementCounter_WithExpiry_DecrementsTheValueAndSetsExpirationDate`
- `AddToSet_AddsItemToSortedSet`
- `AddToSet_WithScore_AddsItemToSortedSetWithScore`
- `RemoveFromSet_RemoveSpecifiedItemFromSortedSet`
- `InsertToList_PrependsListWithSpecifiedValue`
- `RemoveFromList_RemovesAllGivenValuesFromList`
- `TrimList_TrimsListToASpecifiedRange`
- `SetRangeInHash_ThrowsAnException_WhenKeyIsNull`
- `SetRangeInHash_ThrowsAnException_WhenKeyValuePairsArgumentIsNull`
- `SetRangeInHash_SetsAllGivenKeyPairs`
- `RemoveHash_ThrowsAnException_WhenKeyIsNull`
- `RemoveHash_RemovesTheCorrespondingEntry`

### SucceededStateHandlerFacts (3)

- `StateName_ShouldBeEqualToSucceededState`
- `Apply_ShouldInsertTheJob_ToTheBeginningOfTheSucceededList_AndTrimIt`
- `Unapply_ShouldRemoveTheJob_FromTheSucceededList`
