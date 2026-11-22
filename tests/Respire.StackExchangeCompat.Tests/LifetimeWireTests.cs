using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Respire.Tests.Networking;
using StackExchange.Redis;
using TUnit.Core;
using Assert = Xunit.Assert;

namespace Respire.StackExchangeCompat.Tests;

public class LifetimeWireTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReplicaRedirectsKeepIndividualCommandsInCallerOrder(bool retry)
    {
        var setReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var target = new FakeRespServer(4, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("HGET ", StringComparison.Ordinal)
                ? "$5\r\nvalue\r\n"u8.ToArray() : command.StartsWith("HSET ", StringComparison.Ordinal)
                    ? ":1\r\n"u8.ToArray() : FakeRespServer.OkReply,
        };
        await using var source = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("HGET ", StringComparison.Ordinal)
                ? System.Text.Encoding.UTF8.GetBytes($"-REDIRECT 127.0.0.1:{target.Port}\r\n") : FakeRespServer.OkReply,
            SuppressReply = command =>
            {
                if (!command.StartsWith("HSET ", StringComparison.Ordinal))
                {
                    return false;
                }

                setReceived.TrySetResult();
                return true;
            },
        };
        await using var connection = RespireConnectionMultiplexer.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", source.Port)],
            Protocol = RespireProtocol.Resp2,
            EnableReplicaRedirects = true,
            RetryPolicy = retry ? new() : null,
        });
        var database = connection.GetDatabase();
        var set = database.HashSetAsync("key", "field", "value");
        await setReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var get = database.HashGetAsync("key", "field");
        // A later command must stay behind the unresolved redirect, even though
        // the first command has already been accepted by the source transport.
        await Assert.ThrowsAsync<TimeoutException>(() => get.WaitAsync(TimeSpan.FromMilliseconds(200)));
        Assert.DoesNotContain("HGET key field", source.ReceivedCommands);
        await source.SendRawAsync(System.Text.Encoding.UTF8.GetBytes($"-REDIRECT 127.0.0.1:{target.Port}\r\n"));
        Assert.True(await set.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("value", (string?)await get.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new[] { "HSET key field value", "HGET key field" }, target.ReceivedCommands
            .Where(command => command.StartsWith("HSET ", StringComparison.Ordinal) || command.StartsWith("HGET ", StringComparison.Ordinal)));
    }

    [Test]
    [Arguments("HSET", false)]
    [Arguments("HDEL", false)]
    [Arguments("HMSET", false)]
    [Arguments("EXPIRE", false)]
    [Arguments("PEXPIRE", false)]
    [Arguments("PERSIST", false)]
    [Arguments("HSET", true)]
    [Arguments("HDEL", true)]
    [Arguments("HMSET", true)]
    [Arguments("EXPIRE", true)]
    [Arguments("PEXPIRE", true)]
    [Arguments("PERSIST", true)]
    public async Task AdmissionMutationsInvalidateAndFenceCachedReads(string operation, bool cancel)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdReads = false;
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
                "HGET key field" => "$5\r\nvalue\r\n"u8.ToArray(),
                _ => null,
            },
            SuppressReply = command =>
            {
                if (holdReads && command == "HGET key field")
                {
                    readReceived.TrySetResult();
                    return true;
                }

                if (!command.StartsWith(operation + " ", StringComparison.Ordinal))
                {
                    return false;
                }

                received.TrySetResult();
                return true;
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)],
            Protocol = RespireProtocol.Resp3,
            ClientSideCache = new(),
            Connections = 1,
        });
        await using var connection = RespireConnectionMultiplexer.Wrap(client);
        var database = connection.GetDatabase();
        Assert.Equal("value", (string?)await database.HashGetAsync("key", "field"));
        Assert.Equal("value", (string?)await database.HashGetAsync("key", "field"));
        Assert.Equal(1, client.ClientSideCache!.GetStatistics().Hits);
        Assert.Equal(1, client.ClientSideCache.Count);
        var mutation = operation switch
        {
            "HSET" => database.HashSetAsync("key", "field", "new"),
            "HDEL" => database.HashSetAsync("key", "field", RedisValue.Null),
            "HMSET" => database.HashSetAsync("key", [new HashEntry("field", "new")]),
            "EXPIRE" => database.KeyExpireAsync("key", TimeSpan.FromMinutes(1)),
            "PEXPIRE" => database.KeyExpireAsync("key", TimeSpan.FromMilliseconds(60001)),
            "PERSIST" => database.KeyExpireAsync("key", (TimeSpan?)null),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, client.ClientSideCache.Count);

        if (cancel)
        {
            await connection.CloseAsync(allowCommandsToComplete: false);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mutation);
        }

        // A read submitted while the mutation still owns its FIFO slot cannot publish a cache entry,
        // even after the adapter's caller has cancelled and the read's reply follows the write's reply.
        holdReads = true;
        var read = client.Hashes.GetStringAsync("key", "field").AsTask();
        await readReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        holdReads = false;
        await server.SendRawAsync(":1\r\n$5\r\nvalue\r\n"u8.ToArray());

        if (!cancel)
        {
            if (mutation is Task<bool> result)
            {
                Assert.True(await result);
            }
            else
            {
                await mutation;
            }
        }

        Assert.Equal("value", await read.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, client.ClientSideCache.Count);
        Assert.Equal("value", await client.Hashes.GetStringAsync("key", "field"));
        Assert.Equal(1, client.ClientSideCache.Count);
    }

    [Test]
    public async Task ExpiryCommandsUseThePrecisionOfTheEncodedMilliseconds()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray());
        await using var connection = RespireConnectionMultiplexer.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)],
            Protocol = RespireProtocol.Resp2,
        });
        var database = connection.GetDatabase();

        Assert.True(await database.KeyExpireAsync("key", TimeSpan.FromMilliseconds(1501), ExpireWhen.HasExpiry));
        Assert.True(await database.KeyExpireAsync("key", TimeSpan.FromTicks(-TimeSpan.TicksPerSecond - 1)));
        Assert.True(await database.KeyExpireAsync("key", DateTime.UnixEpoch.AddMilliseconds(1501), ExpireWhen.GreaterThanCurrentExpiry));
        Assert.True(await database.KeyExpireAsync("key", DateTime.UnixEpoch.AddSeconds(-1), ExpireWhen.LessThanCurrentExpiry));
        Assert.True(await database.KeyExpireAsync("key", (TimeSpan?)null));
        Assert.True(await database.KeyExpireAsync("key", DateTime.MaxValue));
        Assert.Equal(new[]
        {
            "PEXPIRE key 1501 XX",
            "EXPIRE key -1",
            "PEXPIREAT key 1501 GT",
            "EXPIREAT key -1 LT",
            "PERSIST key",
            "PERSIST key",
        }, server.ReceivedCommands);
    }

    [Test]
    [Arguments(30)]
    [Arguments(60)]
    public async Task LongTimeoutsPreserveSynchronousCommandsAndWaitHelpers(int days)
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray());
        await using var connection = RespireConnectionMultiplexer.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)],
            Protocol = RespireProtocol.Resp2,
            CommandTimeout = TimeSpan.FromDays(days),
        });
        Assert.Equal(int.MaxValue, connection.TimeoutMilliseconds);
        Assert.True(connection.GetDatabase().HashSet("key", "field", "value"));
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = Task.Run(() => completion.SetResult(7));
#pragma warning disable SER308 // These assertions exercise the required synchronous interface.
        Assert.Equal(7, connection.Wait(completion.Task));
        connection.WaitAll(completion.Task, complete);
#pragma warning restore SER308
    }

    [Test]
    public async Task AbandonedBatchTasksCanBeCollectedWhileConnectionRemainsOpen()
    {
        await using var connection = RespireConnectionMultiplexer.Create(RespireOptions.Parse("127.0.0.1:1"));
        var abandoned = AbandonBatch(connection.GetDatabase());
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        Assert.False(abandoned.IsAlive);
        GC.KeepAlive(connection);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AbandonBatch(IDatabase database)
    {
        var batch = database.CreateBatch();
        return new WeakReference(batch.HashSetAsync("abandoned", [new HashEntry("field", new byte[1024 * 1024])]));
    }

    [Test]
    public async Task CloseCancelsRetainedTaskAfterBatchIsCollected()
    {
        await using var connection = RespireConnectionMultiplexer.Create(RespireOptions.Parse("127.0.0.1:1"));
        var queued = QueueWithoutRetainingBatch(connection.GetDatabase());
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        await connection.CloseAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task QueueWithoutRetainingBatch(IDatabase database)
        => database.CreateBatch().HashSetAsync("retained", [new HashEntry("field", "value")]);

    [Test]
    [Arguments(CommandFlags.None)]
    [Arguments(CommandFlags.FireAndForget)]
    public async Task CapacityBlockedCommandsRemainInCallerOrder(CommandFlags flags)
    {
        var received = Channel.CreateUnbounded<string>();
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray())
        {
            SuppressReply = command =>
            {
                received.Writer.TryWrite(command);
                return true;
            },
        };
        await using var connection = RespireConnectionMultiplexer.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)],
            Protocol = RespireProtocol.Resp2,
            MaxInflightCommands = 1,
        });

        try
        {
            var database = connection.GetDatabase();
            var tasks = new List<Task>();
            var expected = new List<string>();
            tasks.Add(database.HashSetAsync("hold", [new HashEntry("field", "value")]));
            Assert.Equal("HMSET hold field value", await received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));

            for (var index = 0; index < 16; index++)
            {
                tasks.Add(database.HashSetAsync($"key{index}", [new HashEntry("field", "value")], flags));
                tasks.Add(database.KeyExpireAsync($"key{index}", TimeSpan.FromSeconds(30), flags));
                expected.Add($"HMSET key{index} field value");
                expected.Add($"EXPIRE key{index} 30");
            }

            await server.SendRawAsync(FakeRespServer.OkReply);

            foreach (var command in expected)
            {
                Assert.Equal(command, await received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
                await server.SendRawAsync(command.StartsWith("HMSET ", StringComparison.Ordinal)
                    ? FakeRespServer.OkReply : ":1\r\n"u8.ToArray());
            }

            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { await connection.CloseAsync(allowCommandsToComplete: false); }
    }

    [Test]
    public async Task BatchUsesOneOrderedPipelineEvenWithMultipleNativeConnections()
    {
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply, ":1\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)],
            Protocol = RespireProtocol.Resp2,
            Connections = 2,
        });
        await using var connection = RespireConnectionMultiplexer.Wrap(client);
        var database = connection.GetDatabase();
        Assert.Throws<NotSupportedException>(() => database.HashGet("key", "field"));
        server.MinimumCommandsBeforeReply = 2;
        var batch = database.CreateBatch();
        var before = server.CommandsSeen;
        var set = batch.HashSetAsync("key", [new HashEntry("field", "value")]);
        var expiry = batch.KeyExpireAsync("key", TimeSpan.FromSeconds(30));
        Assert.Equal(before, server.CommandsSeen);
        Assert.False(set.IsCompleted);
        Assert.False(expiry.IsCompleted);
        batch.Execute();
        await Task.WhenAll(set, expiry).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await expiry);
        var commands = server.ReceivedCommands.Skip(before).ToArray();
        Assert.Equal(new[] { "HMSET key field value", "EXPIRE key 30" }, commands);
        Assert.Single(server.ReceivedConnectionIds.Skip(before).Distinct());
    }

    [Test]
    public async Task ColdIndividualCommandsPipelineInCallerOrder()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply, ":1\r\n"u8.ToArray()) { MinimumCommandsBeforeReply = 2 };
        await using var connection = RespireConnectionMultiplexer.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)],
            Protocol = RespireProtocol.Resp2,
        });
        var first = connection.GetDatabase().HashSetAsync("key", [new HashEntry("field", "value")]);
        var second = connection.GetDatabase().KeyExpireAsync("key", TimeSpan.FromSeconds(30));
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new[] { "HMSET key field value", "EXPIRE key 30" }, server.ReceivedCommands);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CloseSettlesEveryInFlightBatchTask(bool drain)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (command.StartsWith("EXPIRE ", StringComparison.Ordinal))
                {
                    received.TrySetResult();
                }

                return command.StartsWith("HMSET ", StringComparison.Ordinal) || command.StartsWith("EXPIRE ", StringComparison.Ordinal);
            },
        };
        var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var connection = RespireConnectionMultiplexer.Wrap(client, ownsClient: true);
        var batch = connection.GetDatabase().CreateBatch();
        var set = batch.HashSetAsync("key", [new HashEntry("field", "value")]);
        var expire = batch.KeyExpireAsync("key", TimeSpan.FromSeconds(30));
        batch.Execute();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var close = connection.CloseAsync(drain);

        if (drain)
        {
            Assert.False(close.IsCompleted);
            await server.SendRawAsync("+OK\r\n:1\r\n"u8.ToArray());
            await set;
            Assert.True(await expire);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => set);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => expire);
        }

        await close.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(set.IsCompleted);
        Assert.True(expire.IsCompleted);
        Assert.False(client.IsConnected);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CloseSettlesInFlightCommandAndOwnsOnlyItsClient(bool drain)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (!command.StartsWith("HMSET ", StringComparison.Ordinal))
                {
                    return false;
                }

                received.TrySetResult();
                return true;
            },
        };
        var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var connection = RespireConnectionMultiplexer.Wrap(client, ownsClient: true);
        var set = connection.GetDatabase().HashSetAsync("key", [new HashEntry("field", "value")]);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var close = connection.CloseAsync(drain);

        if (drain)
        {
            Assert.False(close.IsCompleted);
            await server.SendRawAsync(FakeRespServer.OkReply);
            await set;
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => set);
        }

        await close.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(client.IsConnected);
        await server.PeerClosed.WaitAsync(TimeSpan.FromSeconds(10));
        await connection.DisposeAsync();
    }

    [Test]
    public async Task NoRedirectAndServerOutcomesRemainObservable()
    {
        await using var server = new FakeRespServer("-MOVED 1 127.0.0.1:1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var connection = RespireConnectionMultiplexer.Wrap(client);
        var error = await Assert.ThrowsAsync<RespireServerException>(() => connection.GetDatabase().HashGetAsync("key", "field", CommandFlags.NoRedirect));
        Assert.Equal("MOVED", error.Code);
        Assert.Single(server.ReceivedCommands.Where(static command => command.StartsWith("HGET ", StringComparison.Ordinal)));
    }
}
