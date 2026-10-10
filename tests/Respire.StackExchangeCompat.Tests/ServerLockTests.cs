using System.Net;
using StackExchange.Redis;
using TUnit.Core;
using Assert = Xunit.Assert;

namespace Respire.StackExchangeCompat.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class ServerLockTests(RedisTestContainer fixture)
{
    private RespireOptions Options(int protocol) => RespireOptions.Parse(fixture.ConnectionString) with
    {
        Connections = 1,
        Protocol = protocol == 2 ? RespProtocol.Resp2 : RespProtocol.Resp3,
    };

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task PinnedHangfireStorageConstructsDiscoversServerAndAcquiresLock(int protocol)
    {
        await using var connection = RespireConnectionMultiplexer.Create(Options(protocol));
        var storage = new Hangfire.Redis.StackExchange.RedisStorage(connection,
            new Hangfire.Redis.StackExchange.RedisStorageOptions { Db = fixture.Database, UseTransactions = false });
        using var storageConnection = storage.GetConnection();
        Assert.InRange(((Hangfire.Storage.JobStorageConnection)storageConnection).GetUtcDateTime(), DateTime.UtcNow.AddSeconds(-5), DateTime.UtcNow.AddSeconds(5));
        using (storageConnection.AcquireDistributedLock("adapter-lock", TimeSpan.FromSeconds(1)))
        {
            await using var control = await RespireClient.ConnectAsync(Options(protocol));
            using var token = await control.ExecuteAsync(RespireCommands.String.GET, "{hangfire}:adapter-lock");
            Assert.False(token.IsNull);
        }
        await using var after = await RespireClient.ConnectAsync(Options(protocol));
        using var released = await after.ExecuteAsync(RespireCommands.String.GET, "{hangfire}:adapter-lock");
        Assert.True(released.IsNull);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task DiscoveryReportsActualEndpointAndServerTime(int protocol)
    {
        await using var connection = RespireConnectionMultiplexer.Create(Options(protocol));
        var parsed = ConfigurationOptions.Parse(connection.Configuration);
        Assert.Equal(fixture.Database, parsed.DefaultDatabase);
        Assert.Null(parsed.Password);
        var database = connection.GetDatabase();
        var endpoint = Assert.Single(connection.GetEndPoints());
        Assert.Equal(endpoint, database.IdentifyEndpoint());
        Assert.Equal(endpoint, await database.IdentifyEndpointAsync(new byte[] { 0xff, 0 }));
        var server = connection.GetServer(endpoint);
        Assert.Same(server, connection.GetServer(database.IdentifyEndpoint()!));
        Assert.Same(server, Assert.Single(connection.GetServers()));
        Assert.Equal(endpoint, server.EndPoint);
        Assert.Same(connection, server.Multiplexer);
        Assert.True(server.IsConnected);
        Assert.False(server.IsReplica);
        Assert.Contains("redis_version:", server.InfoRaw()!);
        Assert.Contains("role:master", (await server.InfoRawAsync("replication"))!);
        Assert.InRange(server.Time(), DateTime.UtcNow.AddSeconds(-5), DateTime.UtcNow.AddSeconds(5));
        Assert.Equal(DateTimeKind.Utc, (await server.TimeAsync()).Kind);
        Assert.Throws<NotSupportedException>(() => server.Time(CommandFlags.FireAndForget));
        Assert.Throws<NotSupportedException>(() => server.DatabaseSize());
        Assert.Throws<NotSupportedException>(() => database.IdentifyEndpoint(flags: CommandFlags.DemandReplica));
        Assert.Throws<NotSupportedException>(() => { _ = database.CreateBatch().IdentifyEndpointAsync(); });
        await connection.CloseAsync();
        Assert.Throws<ObjectDisposedException>(() => server.InfoRaw());
        Assert.Throws<ObjectDisposedException>(() => connection.GetEndPoints());
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task PhysicalServerHandleNeverFallsBackAndBorrowedClientSurvivesClose(int protocol)
    {
        await using var second = new RedisTestContainer();
        await second.InitializeAsync();
        await using var native = await RespireClient.ConnectAsync(Options(protocol) with { ReplicaEndpoints = [new(second.Host, second.Port)] });
        var connection = RespireConnectionMultiplexer.Wrap(native);
        Assert.Equal(2, connection.GetEndPoints(true).Length);
        Assert.Equal(2, connection.GetEndPoints(false).Length);
        var first = connection.GetServer(connection.GetEndPoints()[0]);
        var target = connection.GetServer(second.Host, second.Port);
        Assert.NotEqual(first.InfoRaw("server"), target.InfoRaw("server"));
        await using var control = await RespireClient.ConnectAsync(RespireOptions.Parse(second.ConnectionString) with { Protocol = RespProtocol.Resp2 });
        using (await control.ExecuteAsync(RespireCommands.Server.REPLICAOF, "127.0.0.1", 1)) { }
        Assert.True(target.IsConnected);
        Assert.True(target.IsReplica);
        Assert.False(first.IsReplica);
        Assert.Contains("role:slave", target.InfoRaw("replication")!);
        await connection.CloseAsync();
        Assert.True(native.IsConnected);
        Assert.Contains("role:master", await native.Server.InfoAsync("replication"));
        Assert.Throws<ObjectDisposedException>(() => target.Time());
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task BinaryLocksPreserveTokenExpiryAndAtomicOwnership(int protocol)
    {
        await using var connection = RespireConnectionMultiplexer.Create(Options(protocol));
        await using var control = await RespireClient.ConnectAsync(Options(protocol));
        var database = connection.GetDatabase();
        RedisKey key = new byte[] { 0xff, 0, (byte)fixture.Database };
        RedisValue token = new byte[] { 0xfe, 0, 0x80 };
        Assert.True(database.LockTake(key, token, TimeSpan.FromMinutes(1)));
        using (var reply = await control.ExecuteAsync(RespireCommands.String.GET, (byte[]?)key!))
            Assert.Equal((byte[]?)token, reply.AsBytes());
        using (var reply = await control.ExecuteAsync(RespireCommands.Key.PTTL, (byte[]?)key!))
            Assert.InRange(reply.AsInteger(), 1, 60_000);
        Assert.False(await database.LockTakeAsync(key, "other", TimeSpan.FromSeconds(1)));
        Assert.False(database.LockExtend(key, "other", TimeSpan.FromMinutes(2)));
        Assert.False(await database.LockReleaseAsync(key, "other"));
        Assert.True(await database.LockExtendAsync(key, token, TimeSpan.FromMinutes(2)));
        using (var reply = await control.ExecuteAsync(RespireCommands.Key.PTTL, (byte[]?)key!))
            Assert.InRange(reply.AsInteger(), 60_001, 120_000);
        // Expire deterministically, then replace ownership. The stale holder must never affect the replacement.
        using (await control.ExecuteAsync(RespireCommands.Key.PEXPIRE, (byte[]?)key!, 0)) { }
        Assert.True(await database.LockTakeAsync(key, "replacement", TimeSpan.FromMinutes(1)));
        Assert.False(await database.LockExtendAsync(key, token, TimeSpan.FromHours(1)));
        Assert.False(database.LockRelease(key, token));
        Assert.True(database.LockRelease(key, "replacement"));
        Assert.False(await database.LockReleaseAsync(key, "replacement"));
        var attempts = Enumerable.Range(0, 16).Select(index => database.LockTakeAsync(key, index, TimeSpan.FromMinutes(1))).ToArray();
        var results = await Task.WhenAll(attempts);
        Assert.Equal(1, results.Count(static result => result));
        Assert.True(await database.LockReleaseAsync(key, Array.IndexOf(results, true)));
        Assert.Throws<ArgumentOutOfRangeException>(() => database.LockTake(key, token, TimeSpan.Zero));
        Assert.Throws<NotSupportedException>(() => database.LockRelease(key, token, CommandFlags.DemandReplica));
        Assert.Throws<NotSupportedException>(() => database.LockTake(key, token, TimeSpan.FromSeconds(1), (CommandFlags)1));
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task LuaErrorsRetainServerMessageAndDoNotBreakFollowingCommands(int protocol)
    {
        await using var connection = RespireConnectionMultiplexer.Create(Options(protocol));
        var database = connection.GetDatabase();
        database.HashSet("wrongtype", "field", "value");
        var error = await Assert.ThrowsAsync<RespireServerException>(() => database.LockExtendAsync("wrongtype", "owner", TimeSpan.FromSeconds(1)));
        Assert.Contains("WRONGTYPE", error.Message);
        Assert.Equal("value", (string?)database.HashGet("wrongtype", "field"));
        Assert.Throws<NotSupportedException>(() => database.ScriptEvaluate("return 1"));
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task LiteralSubscriptionsDeliverBinaryPayloadAndCloseWithoutDisposingBorrowedClient(int protocol)
    {
        await using var native = await RespireClient.ConnectAsync(Options(protocol) with { PubSubPrefix = new RespireKey(new byte[] { 0x80, 0 }) });
        await using var connection = RespireConnectionMultiplexer.Wrap(native);
        var subscriber = connection.GetSubscriber();
        byte[] channelBytes = [0xff, 0, (byte)fixture.Database];
        RedisChannel channel = new(channelBytes, RedisChannel.PatternMode.Literal);
        RedisChannel original = new(channelBytes.ToArray(), RedisChannel.PatternMode.Literal);
        byte[] payload = [0xfe, 0, 0x80];
        var delivered = new TaskCompletionSource<RedisValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(RedisChannel actual, RedisValue value)
        {
            Assert.Equal(original, actual);
            delivered.TrySetResult(value);
        }
        subscriber.Subscribe(channel, Handler);
        await subscriber.SubscribeAsync(channel, Handler);
        channelBytes[0] = 0x11;
        channel = original;
        Assert.Equal(1, await connection.GetDatabase().PublishAsync(channel, payload));
        Assert.Equal(payload, (byte[]?)await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await subscriber.UnsubscribeAsync(channel, Handler);
        Assert.Equal(0, subscriber.Publish(channel, payload));
        Assert.Throws<NotSupportedException>(() => subscriber.Subscribe(channel));
        Assert.Throws<NotSupportedException>(() => subscriber.Subscribe(RedisChannel.Pattern("pattern*"), Handler));
        Assert.Throws<NotSupportedException>(() => subscriber.Subscribe(channel, Handler, CommandFlags.FireAndForget));
        await subscriber.SubscribeAsync(channel, Handler);
        await connection.CloseAsync();
        Assert.Equal(0, await native.PublishAsync(new RespireChannel(((byte[]?)channel)!.AsMemory()), payload));
        Assert.True(native.IsConnected);
        Assert.Throws<ObjectDisposedException>(() => subscriber.Subscribe(channel, Handler));
        Assert.Throws<NotSupportedException>(() => connection.GetSubscriber(new object()));
    }
}
