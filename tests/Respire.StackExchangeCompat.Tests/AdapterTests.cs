using System.Buffers;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.StackExchangeRedis;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TUnit.Core;
using Assert = Xunit.Assert;

namespace Respire.StackExchangeCompat.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class AdapterTests(RedisTestContainer fixture)
{
    private RespireOptions Options(int protocol) => RespireOptions.Parse(fixture.ConnectionString) with
    {
        Connections = 1,
        Protocol = protocol == 2 ? RespProtocol.Resp2 : RespProtocol.Resp3,
    };

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task DatabaseSelectionAndBinaryValuesMatchWireControls(int protocol)
    {
        await using var connection = RespireConnectionMultiplexer.Create(Options(protocol));
        var database = connection.GetDatabase();
        var other = connection.GetDatabase(0);
        Assert.Equal(fixture.Database, database.Database);
        Assert.Equal(0, other.Database);
        Assert.Same(connection, database.Multiplexer);
        RedisKey key = new byte[] { 0xff, 0, 0x80, (byte)fixture.Database };
        RedisValue field = new byte[] { 0xfe, 0, 0x81 };
        byte[] value = [0x80, 0, 0xff];
        Assert.True(await database.HashSetAsync(key, field, value));
        Assert.False(database.HashSet(key, field, value));
        Assert.True(await database.HashSetAsync(key, "delete-field", "value"));
        Assert.True(await database.HashSetAsync(key, "delete-field", RedisValue.Null));
        Assert.False(database.HashSet(key, "delete-field", RedisValue.Null));
        Assert.True(other.HashGet(key, field).IsNull);
        await using var control = await RespireClient.ConnectAsync(Options(protocol));
        using var reply = await control.ExecuteAsync(RespireCommands.Hash.HGET, (byte[]?)key!, (byte[]?)field!);
        Assert.Equal(value, reply.AsBytes());
        using var lease = database.HashGetLease(key, field);
        Assert.Equal(value, lease!.Memory.ToArray());
        using var emptyLease = await database.HashGetLeaseAsync(key, "missing");
        Assert.Null(emptyLease);
        Assert.True(database.KeyDelete(key));
        Assert.False(await database.KeyDeleteAsync(key));
        Assert.False(await database.KeyExpireAsync(key, TimeSpan.FromMinutes(1)));
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task BatchDefersSnapshotsAndSequencesExpiry(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync(Options(protocol));
        await using var connection = RespireConnectionMultiplexer.Wrap(client);
        var database = connection.GetDatabase();
        var batch = database.CreateBatch();
        byte[] value = [0xff, 0, 0x81];
        var set = batch.HashSetAsync("batch", [new HashEntry("field", value)]);
        var expire = batch.KeyExpireAsync("batch", TimeSpan.FromSeconds(30));
        value[0] = 0;
        Assert.False(set.IsCompleted);
        Assert.False(expire.IsCompleted);
        using (var before = await client.ExecuteAsync("EXISTS", "batch")) Assert.Equal(0, before.AsInteger());
        batch.Execute();
        await Task.WhenAll(set, expire);
        Assert.True(await expire);
        Assert.Equal(new byte[] { 0xff, 0, 0x81 }, (byte[]?)await database.HashGetAsync("batch", "field"));
        using (var ttl = await client.ExecuteAsync("PTTL", "batch")) Assert.InRange(ttl.AsInteger(), 1, 30000);
        batch.Execute(); // An empty second execution must not replay writes.
        Assert.True(await database.KeyExpireAsync("batch", (TimeSpan?)null));
        using (var ttl = await client.ExecuteAsync("PTTL", "batch")) Assert.Equal(-1, ttl.AsInteger());
        Assert.True(await database.KeyExpireAsync("batch", TimeSpan.Zero));
        Assert.True(database.HashGet("batch", "field").IsNull);
    }

    [Test]
    public async Task ExpirySentinelsAndEmptyListPushPreserveServerOutcomes()
    {
        await using var connection = RespireConnectionMultiplexer.Create(Options(3));
        var database = connection.GetDatabase();
        Assert.Equal(1, database.ListRightPush("list", "value"));
        Assert.Equal(1, await database.ListRightPushAsync("list", Array.Empty<RedisValue>()));
        Assert.Equal(0, database.ListRightPush("missing", "value", When.Exists));
        Assert.True(await database.KeyExpireAsync("list", TimeSpan.FromSeconds(20)));
        Assert.False(await database.KeyExpireAsync("list", TimeSpan.FromSeconds(10), StackExchange.Redis.ExpireWhen.HasNoExpiry));
        Assert.True(await database.KeyExpireAsync("list", TimeSpan.MaxValue));
        Assert.True(await database.KeyExpireAsync("list", DateTime.UtcNow.AddMinutes(1)));
        Assert.True(await database.KeyExpireAsync("list", DateTime.MaxValue));
        Assert.False(await database.KeyExpireAsync("list", (TimeSpan?)null));
    }

    [Test]
    public async Task BatchCompletesEveryTaskWhenOneCommandFails()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        await client.SetAsync("wrongtype", "string");
        await using var connection = RespireConnectionMultiplexer.Wrap(client);
        var batch = connection.GetDatabase().CreateBatch();
        var set = batch.HashSetAsync("wrongtype", [new HashEntry("field", "value")]);
        var expire = batch.KeyExpireAsync("wrongtype", TimeSpan.FromMinutes(1));
        batch.Execute();
        var error = await Assert.ThrowsAsync<RespireServerException>(() => set);
        Assert.Equal("WRONGTYPE", error.Code);
        Assert.True(await expire);
        Assert.True(set.IsFaulted);
    }

    [Test]
    public async Task ClosingCancelsUnexecutedBatchAndPreservesBorrowedClient()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var connection = RespireConnectionMultiplexer.Wrap(client);
        var database = connection.GetDatabase();
        var batch = database.CreateBatch();
        var set = batch.HashSetAsync("unexecuted", [new HashEntry("field", "value")]);
        var expire = batch.KeyExpireAsync("unexecuted", TimeSpan.FromMinutes(1));
        await connection.CloseAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => set);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => expire);
        batch.Execute();
        using var exists = await client.ExecuteAsync("EXISTS", "unexecuted");
        Assert.Equal(0, exists.AsInteger());
        Assert.Throws<ObjectDisposedException>(() => database.HashGet("key", "field"));
        Assert.Throws<ObjectDisposedException>(() => connection.GetDatabase());
        Assert.False(connection.IsConnected);
        Assert.True(client.IsConnected);
        await client.PingAsync();
        await connection.DisposeAsync();
    }

    [Test]
    public async Task OwnedWrappedClientIsDisposedAndUnsupportedMembersFailClearly()
    {
        var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        await using var connection = RespireConnectionMultiplexer.Wrap(client, ownsClient: true);
        var database = connection.GetDatabase();
        Assert.Throws<NotSupportedException>(() => connection.GetDatabase(0));
        Assert.Throws<NotSupportedException>(() => connection.GetDatabase(asyncState: new object()));
        Assert.Contains("native Respire APIs", Assert.Throws<NotSupportedException>(() => database.StringGet("key")).Message);
        Assert.Throws<NotSupportedException>(() => database.HashGet("key", "field", (CommandFlags)1));
        Assert.Throws<NotSupportedException>(() => { _ = database.CreateBatch().StringGetAsync("key"); });
        await connection.CloseAsync(false);
        Assert.False(client.IsConnected);
    }

    [Test]
    public async Task FireAndForgetPreservesDefaultResultAndDatabaseConfiguration()
    {
        await using var client = await RespireClient.ConnectAsync(Options(2) with
        {
            ClientName = "compat-owned",
            CommandTimeout = TimeSpan.FromSeconds(12),
        });
        await using var connection = RespireConnectionMultiplexer.Wrap(client);
        Assert.Equal("compat-owned", connection.ClientName);
        Assert.Equal(12000, connection.TimeoutMilliseconds);
        var database = connection.GetDatabase();
        Assert.False(await database.HashSetAsync("forget", "field", "value", flags: CommandFlags.FireAndForget));
        Assert.Equal("value", (string?)database.HashGet("forget", "field"));
        Assert.Empty(await database.HashGetAsync("forget", new RedisValue[] { "field" }, CommandFlags.FireAndForget));
        Assert.Throws<NotSupportedException>(() => database.HashGet("forget", "field", CommandFlags.FireAndForget | CommandFlags.NoRedirect));
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task OfficialBufferCacheCoversLeaseAndCancellation(int protocol)
    {
        await using var connection = RespireConnectionMultiplexer.Create(Options(protocol));
        using var cache = new RedisCache(Microsoft.Extensions.Options.Options.Create(new RedisCacheOptions
        {
            ConnectionMultiplexerFactory = () => Task.FromResult<IConnectionMultiplexer>(connection),
            InstanceName = "buffer:",
        }));
        var bufferCache = (IBufferDistributedCache)cache;
        byte[] bytes = [0xff, 0, 0x80, 0xfe];
        await bufferCache.SetAsync("key", new ReadOnlySequence<byte>(bytes), new DistributedCacheEntryOptions(), default);
        var writer = new ArrayBufferWriter<byte>();
        Assert.True(bufferCache.TryGet("key", writer));
        Assert.Equal(bytes, writer.WrittenSpan.ToArray());
        writer.Clear();
        Assert.True(await bufferCache.TryGetAsync("key", writer, default));
        Assert.Equal(bytes, writer.WrittenSpan.ToArray());
        cache.Set("empty", []);
        writer.Clear();
        Assert.True(bufferCache.TryGet("empty", writer));
        Assert.Empty(writer.WrittenSpan.ToArray());
        Assert.False(await bufferCache.TryGetAsync("missing", writer, default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetAsync("key", cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.SetAsync("canceled", bytes, cancellation.Token));
        Assert.Null(cache.Get("canceled"));
    }

    [Test]
    public void OfficialDataProtectionServicesSharePersistedKeyRing()
    {
        using var connection = RespireConnectionMultiplexer.Create(Options(3));
        using var first = new ServiceCollection().AddDataProtection().PersistKeysToStackExchangeRedis(connection, "keyring").Services.BuildServiceProvider();
        using var second = new ServiceCollection().AddDataProtection().PersistKeysToStackExchangeRedis(connection, "keyring").Services.BuildServiceProvider();
        var encrypted = first.GetRequiredService<IDataProtectionProvider>().CreateProtector("purpose").Protect("secret");
        Assert.Equal("secret", second.GetRequiredService<IDataProtectionProvider>().CreateProtector("purpose").Unprotect(encrypted));
        Assert.NotEmpty(connection.GetDatabase().ListRange("keyring"));
    }
}
