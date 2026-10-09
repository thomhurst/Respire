using System.Diagnostics;
using System.Runtime.CompilerServices;
using Respire.Tests.Networking;
using Respire.StackExchangeCompat;
using StackExchange.Redis;
using TUnit.Core;
using Assert = Xunit.Assert;

namespace Respire.StackExchangeCompat.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
[NotInParallel]
public class TransactionTests(RedisTestContainer redis)
{
    private RespireOptions Options(int protocol) => RespireOptions.Parse(redis.ConnectionString) with
    {
        Connections = 1,
        Protocol = protocol == 2 ? RespProtocol.Resp2 : RespProtocol.Resp3,
    };

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task TransactionPublicationResolvesPrefixAndSnapshotsBinaryArguments(int protocol)
    {
        byte[] prefix = [0x80, 0];
        await using var connection = RespireConnectionMultiplexer.Create(Options(protocol) with { PubSubPrefix = new RespireKey(prefix) });
        var subscriber = connection.GetSubscriber();
        byte[] channelBytes = [0xff, 0, (byte)redis.Database];
        RedisChannel channel = new(channelBytes.ToArray(), RedisChannel.PatternMode.Literal);
        byte[] payload = [0xfe, 0, 0x81];
        var delivered = new TaskCompletionSource<RedisValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        await subscriber.SubscribeAsync(channel, (actual, value) =>
        {
            Assert.Equal(channel, actual);
            delivered.TrySetResult(value);
        });
        var transaction = connection.GetDatabase().CreateTransaction();
        var published = transaction.PublishAsync(new RedisChannel(channelBytes, RedisChannel.PatternMode.Literal), payload);
        Assert.False(published.IsCompleted);
        Assert.False(delivered.Task.IsCompleted);
        channelBytes.AsSpan().Fill(1);
        payload.AsSpan().Fill(1);
        Assert.True(await transaction.ExecuteAsync());
        Assert.Equal(1, await published);
        Assert.Equal(new byte[] { 0xfe, 0, 0x81 }, (byte[]?)await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await subscriber.UnsubscribeAsync(channel);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task PrefixedConditionsReadEverySupportedCommandAndAbortOnMismatch(int protocol)
    {
        await using var connection = RespireConnectionMultiplexer.Create(Options(protocol) with { KeyPrefix = "tenant:" });
        using var control = await ConnectionMultiplexer.ConnectAsync(redis.StackExchangeConnectionString);
        var expected = control.GetDatabase();
        await expected.StringSetAsync("tenant:string", "abc");
        await expected.StringSetAsync("string", "wrong namespace");
        await expected.HashSetAsync("tenant:hash", "field", "value");
        await expected.ListRightPushAsync("tenant:list", "entry");
        await expected.SetAddAsync("tenant:set", "member");
        await expected.SortedSetAddAsync("tenant:sorted", "member", 1.5);
        var transaction = connection.GetDatabase().CreateTransaction();
        var conditions = new[]
        {
            Condition.KeyExists("string"), Condition.KeyNotExists("missing"),
            Condition.StringEqual("string", "abc"), Condition.HashExists("hash", "field"),
            Condition.HashEqual("hash", "field", "value"), Condition.SetContains("set", "member"),
            Condition.SortedSetEqual("sorted", "member", 1.5), Condition.ListIndexEqual("list", 0, "entry"),
            Condition.StringLengthEqual("string", 3), Condition.HashLengthEqual("hash", 1),
            Condition.ListLengthEqual("list", 1), Condition.SetLengthEqual("set", 1),
            Condition.SortedSetLengthEqual("sorted", 1), Condition.SortedSetLengthEqual("sorted", 1, 1, 2),
            Condition.StreamLengthEqual("stream", 0),
        }.Select(transaction.AddCondition).ToArray();
        var write = transaction.StringIncrementAsync("counter");
        Assert.True(await transaction.ExecuteAsync());
        Assert.All(conditions, static condition => Assert.True(condition.WasSatisfied));
        Assert.Equal(1, await write);
        Assert.Equal("1", (string?)await expected.StringGetAsync("tenant:counter"));
        Assert.False(await expected.KeyExistsAsync("counter"));

        var mismatch = transaction.AddCondition(Condition.StringEqual("string", "wrong namespace"));
        var wrongType = transaction.AddCondition(Condition.StringEqual("hash", "value"));
        var canceled = transaction.StringIncrementAsync("counter");
        Assert.False(await transaction.ExecuteAsync());
        Assert.False(mismatch.WasSatisfied);
        Assert.False(wrongType.WasSatisfied);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        Assert.Equal("1", (string?)await expected.StringGetAsync("tenant:counter"));
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task PinnedHangfireWritesAndFetchedJobsUseTransactions(int protocol)
    {
        await using var connection = RespireConnectionMultiplexer.Create(Options(protocol));
        var storage = new Hangfire.Redis.StackExchange.RedisStorage(connection,
            new Hangfire.Redis.StackExchange.RedisStorageOptions { Db = redis.Database, UseTransactions = true });
        using var storageConnection = storage.GetConnection();
        var concrete = (Hangfire.Storage.JobStorageConnection)storageConnection;
        using (var transaction = storageConnection.CreateWriteTransaction())
        {
            transaction.SetRangeInHash("metadata", [new KeyValuePair<string, string>("field", "value")]);
            transaction.InsertToList("history", "entry");
            transaction.AddToSet("jobs", "job", 2);
            transaction.IncrementCounter("counter");
            transaction.AddToQueue("default", "job");
            Assert.False(await connection.GetDatabase().KeyExistsAsync("{hangfire}:metadata"));
            transaction.Commit();
        }
        Assert.Equal("value", storageConnection.GetAllEntriesFromHash("metadata")["field"]);
        Assert.Equal(new[] { "entry" }, concrete.GetAllItemsFromList("history"));
        Assert.Contains("job", storageConnection.GetAllItemsFromSet("jobs"));
        Assert.Equal(1, concrete.GetCounter("counter"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using (var fetched = storageConnection.FetchNextJob(["default"], timeout.Token))
        {
            Assert.Equal("job", fetched.JobId);
            fetched.Requeue();
        }
        Assert.Equal("job", (string?)await connection.GetDatabase().ListGetByIndexAsync("{hangfire}:queue:default", 0));
        using (var fetched = storageConnection.FetchNextJob(["default"], timeout.Token)) fetched.RemoveFromQueue();
        Assert.Equal(0, await connection.GetDatabase().ListLengthAsync("{hangfire}:queue:default:dequeued"));
        Assert.Equal(RedisValue.Null, await connection.GetDatabase().HashGetAsync("{hangfire}:job:job", "Fetched"));
        storageConnection.AnnounceServer("worker", new Hangfire.Server.ServerContext { Queues = ["default"], WorkerCount = 1 });
        Assert.Contains((RedisValue)"worker", await connection.GetDatabase().SetMembersAsync("{hangfire}:servers"));
        storageConnection.RemoveServer("worker");
        Assert.Empty(await connection.GetDatabase().SetMembersAsync("{hangfire}:servers"));
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task AllHangfireFacetsAreDeferredAndCompleteInOrder(int protocol)
    {
        await using var connection = RespireConnectionMultiplexer.Create(Options(protocol));
        var database = connection.GetDatabase();
        var transaction = database.CreateTransaction();
        byte[] key = [0xff, 0, 0x81];
        byte[] field = [0xfe, 0, 0x82];
        byte[] value = [0xfd, 0, 0x83];
        var hash = transaction.HashSetAsync(key, [new HashEntry(field, value)]);
        var hashRead = transaction.HashGetAsync(key, field);
        var list = transaction.ListRightPushAsync("list", ["first", "last"]);
        var listRead = transaction.ListRangeAsync("list");
        var move = transaction.ListRightPopLeftPushAsync("list", "moved");
        var trim = transaction.ListTrimAsync("list", 0, 0);
        var remove = transaction.ListRemoveAsync("moved", "last");
        var set = transaction.SetAddAsync("set", ["one", "two"]);
        var setRead = transaction.SetMembersAsync("set");
        var sorted = transaction.SortedSetAddAsync("sorted", [new("high", 2), new("low", 1)]);
        var sortedRead = transaction.SortedSetRangeByRankWithScoresAsync("sorted");
        var increment = transaction.StringIncrementAsync("counter");
        var decrement = transaction.StringDecrementAsync("counter");
        var get = transaction.StringGetAsync(["counter", "missing", "counter"]);
        var expires = transaction.KeyExpireAsync(key, TimeSpan.FromMinutes(1));
        var persists = transaction.KeyPersistAsync(key);
        var publish = transaction.PublishAsync(RedisChannel.Literal("jobs"), "job");
        var delete = transaction.KeyDeleteAsync("set");
        var afterDelete = transaction.KeyExistsAsync("set");
        Assert.False(hash.IsCompleted);
        Assert.False(publish.IsCompleted);
        Assert.False(await database.KeyExistsAsync(key));
        key.AsSpan().Fill(1);
        field.AsSpan().Fill(1);
        value.AsSpan().Fill(1);
        Assert.True(transaction.Execute());
        await hash;
        Assert.Equal(new byte[] { 0xfd, 0, 0x83 }, (byte[]?)await hashRead);
        Assert.Equal(2, await list);
        Assert.Equal(new RedisValue[] { "first", "last" }, await listRead);
        Assert.Equal("last", (string?)await move);
        await trim;
        Assert.Equal(1, await remove);
        Assert.Equal(2, await set);
        Assert.Equal(new[] { "one", "two" }, (await setRead).Select(static item => (string?)item).Order().ToArray());
        Assert.Equal(2, await sorted);
        Assert.Equal(new StackExchange.Redis.SortedSetEntry[] { new("low", 1), new("high", 2) }, await sortedRead);
        Assert.Equal(1, await increment);
        Assert.Equal(0, await decrement);
        Assert.Equal(new RedisValue[] { "0", RedisValue.Null, "0" }, await get);
        Assert.True(await expires);
        Assert.True(await persists);
        Assert.Equal(0, await publish);
        Assert.True(await delete);
        Assert.False(await afterDelete);
        Assert.True(transaction.Execute());
        var next = transaction.StringIncrementAsync("counter");
        Assert.True(await transaction.ExecuteAsync());
        Assert.Equal(1, await next);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task MultiExecNeverExposesPartialUpdates(int protocol)
    {
        await using var connection = RespireConnectionMultiplexer.Create(Options(protocol));
        using var control = await ConnectionMultiplexer.ConnectAsync(redis.StackExchangeConnectionString);
        var database = connection.GetDatabase();
        var expected = control.GetDatabase();
        await expected.StringSetAsync([new KeyValuePair<RedisKey, RedisValue>("left", 0), new("right", 0)]);
        var observed = 0;
        async Task ObserveAsync()
        {
            for (var index = 0; index < 100; index++)
            {
                var pair = await expected.StringGetAsync(["left", "right"]);
                Assert.Equal(0, (long)pair[0] + (long)pair[1]);
                observed++;
            }
        }
        var observing = ObserveAsync();
        for (var index = 0; index < 30; index++)
        {
            var transaction = database.CreateTransaction();
            var left = transaction.StringIncrementAsync("left");
            var right = transaction.StringDecrementAsync("right");
            Assert.True(await transaction.ExecuteAsync());
            Assert.Equal(index + 1, await left);
            Assert.Equal(-index - 1, await right);
        }
        await observing;
        Assert.Equal(100, observed);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ConditionsSnapshotBinaryValuesAndReportEachOutcome(int protocol)
    {
        await using var connection = RespireConnectionMultiplexer.Create(Options(protocol));
        using var control = await ConnectionMultiplexer.ConnectAsync(redis.StackExchangeConnectionString);
        var expected = control.GetDatabase();
        byte[] key = [0xff, 0, 0x81];
        byte[] value = [0xfe, 0, 0x82];
        await expected.StringSetAsync(key, value);
        var transaction = connection.GetDatabase().CreateTransaction();
        var exists = transaction.AddCondition(Condition.KeyExists(key));
        var equals = transaction.AddCondition(Condition.StringEqual(key, value));
        var missing = transaction.AddCondition(Condition.KeyExists("missing"));
        var notExists = transaction.AddCondition(Condition.KeyNotExists("missing"));
        var command = transaction.StringIncrementAsync("counter");
        Assert.False(exists.WasSatisfied);
        key.AsSpan().Fill(1);
        value.AsSpan().Fill(1);
        Assert.False(transaction.Execute());
        Assert.True(exists.WasSatisfied);
        Assert.True(equals.WasSatisfied);
        Assert.False(missing.WasSatisfied);
        Assert.True(notExists.WasSatisfied);
        Assert.False(transaction.WasWatchConflict);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command);
        Assert.False(await expected.KeyExistsAsync("counter"));
        // Neither commands nor conditions are replayed by a repeat Execute.
        Assert.True(transaction.Execute());
        Assert.False(await expected.KeyExistsAsync("counter"));
        var next = transaction.StringIncrementAsync("counter");
        Assert.True(await transaction.ExecuteAsync());
        Assert.Equal(1, await next);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task SupportedConditionFamiliesMatchUpstream(int protocol)
    {
        await using var connection = RespireConnectionMultiplexer.Create(Options(protocol));
        using var control = await ConnectionMultiplexer.ConnectAsync(redis.StackExchangeConnectionString);
        var expected = control.GetDatabase();
        await expected.HashSetAsync("hash", "field", "value");
        await expected.ListRightPushAsync("list", ["first", "last"]);
        await expected.SetAddAsync("set", "member");
        await expected.SortedSetAddAsync("sorted", "member", 1.5);
        await expected.StringSetAsync("string", "abc");
        var conditions = new[]
        {
            Condition.HashExists("hash", "field"), Condition.HashNotExists("hash", "missing"),
            Condition.HashEqual("hash", "field", "value"), Condition.HashNotEqual("hash", "field", "other"),
            Condition.HashEqual("hash", "missing", RedisValue.Null), Condition.HashNotEqual("hash", "field", RedisValue.Null),
            Condition.StringEqual("missing", RedisValue.Null), Condition.StringNotEqual("string", RedisValue.Null),
            Condition.StringNotEqual("string", "other"), Condition.SetContains("set", "member"),
            Condition.SetNotContains("set", "missing"), Condition.SortedSetContains("sorted", "member"),
            Condition.SortedSetNotContains("sorted", "missing"), Condition.SortedSetEqual("sorted", "member", 1.5),
            Condition.SortedSetNotEqual("sorted", "member", 2), Condition.ListIndexEqual("list", -1, "last"),
            Condition.ListIndexNotEqual("list", 0, "other"), Condition.ListIndexExists("list", 0),
            Condition.ListIndexNotExists("list", 3), Condition.ListIndexEqual("list", 3, RedisValue.Null),
            Condition.HashLengthEqual("hash", 1), Condition.HashLengthLessThan("hash", 2), Condition.HashLengthGreaterThan("hash", 0),
            Condition.StringLengthEqual("string", 3), Condition.StringLengthLessThan("string", 4), Condition.StringLengthGreaterThan("string", 2),
            Condition.ListLengthEqual("list", 2), Condition.ListLengthLessThan("list", 3), Condition.ListLengthGreaterThan("list", 1),
            Condition.SetLengthEqual("set", 1), Condition.SetLengthLessThan("set", 2), Condition.SetLengthGreaterThan("set", 0),
            Condition.SortedSetLengthEqual("sorted", 1), Condition.SortedSetLengthLessThan("sorted", 2), Condition.SortedSetLengthGreaterThan("sorted", 0),
            Condition.SortedSetLengthEqual("sorted", 1, 1, 2), Condition.SortedSetLengthLessThan("sorted", 2, 1, 2),
            Condition.SortedSetLengthGreaterThan("sorted", 0, 1, 2), Condition.SortedSetLengthEqual("sorted", 1, double.NegativeInfinity, double.PositiveInfinity),
            Condition.StreamLengthEqual("stream", 0), Condition.StreamLengthLessThan("stream", 1),
            Condition.HashEqual("hash", "field", "wrong"), Condition.ListLengthEqual("list", 0),
        };
        foreach (var condition in conditions)
        {
            var native = connection.GetDatabase().CreateTransaction();
            var upstream = expected.CreateTransaction();
            var actual = native.AddCondition(condition);
            var reference = upstream.AddCondition(condition);
            Assert.Equal(await upstream.ExecuteAsync(), await native.ExecuteAsync());
            Assert.Equal(reference.WasSatisfied, actual.WasSatisfied);
        }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task WatchedMutationAfterConditionReadAbortsAllCommands(int protocol)
    {
        await using var connection = RespireConnectionMultiplexer.Create(Options(protocol) with { KeyPrefix = "tenant:" });
        using var control = await ConnectionMultiplexer.ConnectAsync(redis.StackExchangeConnectionString);
        var expected = control.GetDatabase();
        await expected.StringSetAsync("tenant:guard", "old");
        var transaction = connection.GetDatabase().CreateTransaction();
        var condition = transaction.AddCondition(Condition.StringEqual("guard", "old"));
        var pending = transaction.StringIncrementAsync("counter");
        var changed = 0;
        // Mutate through another real Redis connection after GET's response, before the adapter
        // returns from condition evaluation and sends EXEC. No timing delay or retry loop needed.
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "Respire",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if ((string?)activity.GetTagItem("db.operation.name") == "GET" && Interlocked.Exchange(ref changed, 1) == 0)
                    expected.StringSet("tenant:guard", "new");
            },
        };
        ActivitySource.AddActivityListener(listener);
        Assert.False(await transaction.ExecuteAsync());
        Assert.Equal(1, changed);
        Assert.True(condition.WasSatisfied);
        Assert.True(transaction.WasWatchConflict);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(await expected.KeyExistsAsync("tenant:counter"));
        Assert.True(transaction.Execute());
        Assert.False(transaction.WasWatchConflict);
        // The watched lease is released; subsequent conditional transactions remain usable.
        var nextCondition = transaction.AddCondition(Condition.StringEqual("guard", "new"));
        var next = transaction.StringIncrementAsync("counter");
        Assert.True(transaction.Execute());
        Assert.True(nextCondition.WasSatisfied);
        Assert.Equal(1, await next);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task RuntimeErrorsFaultOnlyTheirCommandAndConditionErrorsAbort(int protocol)
    {
        await using var connection = RespireConnectionMultiplexer.Create(Options(protocol));
        var database = connection.GetDatabase();
        await database.HashSetAsync("wrong", "field", "value");
        var transaction = database.CreateTransaction();
        var before = transaction.StringIncrementAsync("counter");
        var error = transaction.ListRightPushAsync("wrong", "value");
        var after = transaction.StringIncrementAsync("counter");
        Assert.True(transaction.Execute());
        Assert.Equal(1, await before);
        Assert.Equal(2, await after);
        Assert.Contains("WRONGTYPE", (await Assert.ThrowsAsync<RespireServerException>(() => error)).Message);
        var condition = transaction.AddCondition(Condition.StringEqual("wrong", "value"));
        var canceled = transaction.StringIncrementAsync("counter");
        Assert.False(await transaction.ExecuteAsync());
        Assert.False(condition.WasSatisfied);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        Assert.Equal("2", (string?)await database.StringGetAsync("counter"));
    }

    [Test]
    public async Task UnsupportedMembersRejectBeforeConsumingPendingQueue()
    {
        await using var connection = RespireConnectionMultiplexer.Create(Options(2));
        var database = connection.GetDatabase();
        Assert.Throws<NotSupportedException>(() => database.CreateTransaction(new object()));
        var transaction = database.CreateTransaction();
        Assert.Throws<ArgumentNullException>(() => transaction.AddCondition(null!));
        Assert.Contains("native WATCH", Assert.Throws<NotSupportedException>(() =>
            transaction.AddCondition(Condition.SortedSetScoreExists("sorted", 1))).Message);
        foreach (var flags in new[] { CommandFlags.FireAndForget, CommandFlags.NoRedirect, CommandFlags.PreferReplica, CommandFlags.DemandReplica })
        {
            Assert.Throws<NotSupportedException>(() => { _ = transaction.StringIncrementAsync("counter", flags: flags); });
            Assert.Throws<NotSupportedException>(() => transaction.Execute(flags));
        }
        Assert.Throws<NotSupportedException>(() => { _ = transaction.HashSetAsync("hash", "field", "value", When.Exists); });
        Assert.Throws<NotSupportedException>(() => transaction.SortedSetScanAsync("sorted"));
        Assert.Throws<NotSupportedException>(() => { _ = transaction.LockTakeAsync("lock", "owner", TimeSpan.FromSeconds(1)); });
        var pending = transaction.StringIncrementAsync("counter", flags: CommandFlags.DemandMaster);
        Assert.Throws<NotSupportedException>(() => transaction.Execute(CommandFlags.FireAndForget));
        Assert.False(pending.IsCompleted);
        Assert.True(transaction.Execute(CommandFlags.DemandMaster));
        Assert.Equal(1, await pending);
        ITransactionAsync asynchronous = ((IDatabaseAsync)database).CreateTransaction();
        var next = asynchronous.StringIncrementAsync("counter");
        Assert.True(await asynchronous.ExecuteAsync());
        Assert.Equal(2, await next);
        var batch = (IBatch)database.CreateTransaction();
        var fromBatch = batch.StringIncrementAsync("counter");
        batch.Execute();
        Assert.Equal(3, await fromBatch);
    }

    [Test]
    public async Task QueuedShutdownCancelsTasksAndPreservesWrappedClient()
    {
        await using var client = await RespireClient.ConnectAsync(Options(2));
        var connection = RespireConnectionMultiplexer.Wrap(client);
        var transaction = connection.GetDatabase().CreateTransaction();
        var pending = transaction.HashGetAsync("hash", "field");
        await connection.CloseAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Throws<ObjectDisposedException>(() => transaction.Execute());
        await client.SetAsync("owned", "still usable");
        Assert.Equal("still usable", await client.GetStringAsync("owned"));
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task RedisQueueErrorsDiscardEveryCommandAndDoNotPoisonRepeatExecution(int protocol)
    {
        using var control = await ConnectionMultiplexer.ConnectAsync(redis.StackExchangeConnectionString);
        var server = control.GetServer(redis.Host, redis.Port);
        var username = "transaction-" + Guid.NewGuid().ToString("N");
        await server.ExecuteAsync("ACL", "SETUSER", username, "on", ">password", "~*", "+@all", "-incr");
        try
        {
            await using var connection = RespireConnectionMultiplexer.Create(Options(protocol) with
            {
                Username = username,
                Password = "password",
            });
            var transaction = connection.GetDatabase().CreateTransaction();
            var hash = transaction.HashSetAsync("hash", "field", "never");
            var denied = transaction.StringIncrementAsync("counter");
            var list = transaction.ListRightPushAsync("list", "never");
            var error = await Assert.ThrowsAsync<RespireServerException>(() => transaction.ExecuteAsync());
            Assert.Contains("NOPERM", error.Message);
            await Assert.ThrowsAsync<RespireServerException>(() => hash);
            await Assert.ThrowsAsync<RespireServerException>(() => denied);
            await Assert.ThrowsAsync<RespireServerException>(() => list);
            Assert.False(await control.GetDatabase().KeyExistsAsync("hash"));
            Assert.False(await control.GetDatabase().KeyExistsAsync("list"));
            Assert.False(await control.GetDatabase().KeyExistsAsync("counter"));
            await server.ExecuteAsync("ACL", "SETUSER", username, "+incr");
            Assert.True(transaction.Execute());
            var next = transaction.StringIncrementAsync("counter");
            Assert.True(transaction.Execute());
            Assert.Equal(1, await next);
        }
        finally { await server.ExecuteAsync("ACL", "DELUSER", username); }
    }

    [Test]
    public async Task ShutdownCancelsExecutingTransactionAndEveryQueuedTask()
    {
        var reachedExec = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("INCR ", StringComparison.Ordinal) ? "+QUEUED\r\n"u8.ToArray() : null,
            SuppressReply = command =>
            {
                if (command != "EXEC") return false;
                reachedExec.TrySetResult();
                return true;
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var connection = RespireConnectionMultiplexer.Wrap(client);
        var transaction = connection.GetDatabase().CreateTransaction();
        var one = transaction.StringIncrementAsync("one");
        var two = transaction.StringIncrementAsync("two");
        var executing = transaction.ExecuteAsync();
        await reachedExec.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await connection.CloseAsync(allowCommandsToComplete: false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executing);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => one);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => two);
    }

    [Test]
    public async Task EmptyExecuteRequiresRealServerReply()
    {
        await using var server = new FakeRespServer("-ERR empty transaction ping failed\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var connection = RespireConnectionMultiplexer.Wrap(client);
        var transaction = connection.GetDatabase().CreateTransaction();
        Assert.Contains("empty transaction ping failed", (await Assert.ThrowsAsync<RespireServerException>(
            () => transaction.ExecuteAsync())).Message);
        Assert.Equal(new[] { "PING" }, server.ReceivedCommands);
    }

    [Test]
    public async Task AbandonedTransactionsDoNotRootTasksThroughShutdownRegistration()
    {
        await using var connection = RespireConnectionMultiplexer.Create(Options(2));
        var abandoned = CreateAbandonedTask(connection.GetDatabase());
        for (var attempt = 0; attempt < 5 && abandoned.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(abandoned.IsAlive);
        var retained = connection.GetDatabase().CreateTransaction().HashGetAsync("hash", "field");
        await connection.CloseAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retained);
    }

    [Test]
    public async Task DefaultCloseDrainsTransactionAlreadyEvaluatingConditions()
    {
        await using var client = await RespireClient.ConnectAsync(Options(2));
        await using var connection = RespireConnectionMultiplexer.Wrap(client);
        var transaction = connection.GetDatabase().CreateTransaction();
        transaction.AddCondition(Condition.KeyNotExists("guard"));
        var pending = transaction.StringIncrementAsync("counter");
        var reachedRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseRead = new ManualResetEventSlim();
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "Respire",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if ((string?)activity.GetTagItem("db.operation.name") != "EXISTS") return;
                reachedRead.TrySetResult();
                Assert.True(releaseRead.Wait(TimeSpan.FromSeconds(10)));
            },
        };
        ActivitySource.AddActivityListener(listener);
        var executing = transaction.ExecuteAsync();
        try
        {
            await reachedRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var closing = connection.CloseAsync();
            Assert.False(pending.IsCompleted);
            Assert.False(closing.IsCompleted);
            releaseRead.Set();
            Assert.True(await executing);
            Assert.Equal(1, await pending);
            await closing;
        }
        finally { releaseRead.Set(); }
        Assert.Equal("1", await client.GetStringAsync("counter"));
    }

    [Test]
    public async Task OverlappingExecutionsKeepQueuesAndLatestConflictStatusSeparate()
    {
        await using var connection = RespireConnectionMultiplexer.Create(Options(2));
        using var control = await ConnectionMultiplexer.ConnectAsync(redis.StackExchangeConnectionString);
        var transaction = connection.GetDatabase().CreateTransaction();
        transaction.AddCondition(Condition.KeyNotExists("guard"));
        var aborted = transaction.StringIncrementAsync("counter");
        var reachedRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseRead = new ManualResetEventSlim();
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "Respire",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if ((string?)activity.GetTagItem("db.operation.name") != "EXISTS") return;
                reachedRead.TrySetResult();
                Assert.True(releaseRead.Wait(TimeSpan.FromSeconds(10)));
            },
        };
        ActivitySource.AddActivityListener(listener);
        var first = transaction.ExecuteAsync();
        try
        {
            await reachedRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var next = transaction.StringIncrementAsync("counter");
            var second = transaction.ExecuteAsync();
            Assert.False(second.IsCompleted);
            await control.GetDatabase().StringSetAsync("guard", "changed");
            releaseRead.Set();
            Assert.False(await first);
            Assert.True(await second);
            Assert.Equal(1, await next);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => aborted);
            Assert.False(transaction.WasWatchConflict);
        }
        finally { releaseRead.Set(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateAbandonedTask(IDatabase database)
        => new(database.CreateTransaction().HashSetAsync("hash", "field", new byte[128 * 1024]));
}
