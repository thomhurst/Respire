using Respire.Testing.StackExchangeCompat;
using StackExchange.Redis;
using TUnit.Core;
using Assert = Xunit.Assert;

namespace Respire.StackExchangeCompat.Tests;

public class FakeConnectionTests
{
    [Test]
    public async Task CompatibilityCallsUseRealTransportAndBinaryValues()
    {
        await using var fixture = new RespireFakeConnection();
        IConnectionMultiplexer connection = fixture.Connection;
        var database = connection.GetDatabase();
        Assert.Same(database, fixture.Database);
        Assert.Same(connection, database.Multiplexer);
        Assert.Equal(0, database.Database);
        RedisKey key = new byte[] { 0xff, 0, 0x80 };
        byte[] value = [0xfe, 0, 0x81];
        Assert.True(await database.HashSetAsync(key, "field", value));
        Assert.Equal(value, (byte[]?)database.HashGet(key, "field"));
        await using var native = await RespireClient.ConnectAsync(fixture.Server.CreateOptions());
        using var reply = await native.ExecuteAsync(RespireCommands.Hash.HGET, (byte[]?)key!, "field");
        Assert.Equal(value, reply.AsBytes());
        Assert.True(await database.HashSetAsync("hash", "field", value));
        Assert.Equal(value, (byte[]?)await database.HashGetAsync("hash", "field"));
        Assert.Equal(1, database.ListRightPush("list", "item"));
        Assert.Equal("item", (string?)await database.ListGetByIndexAsync("list", 0));
        Assert.Equal(1, await database.StringIncrementAsync("counter"));
        Assert.Equal("1", (string?)await database.StringGetAsync("counter"));
        var batch = database.CreateBatch();
        var set = batch.HashSetAsync("batched", "field", "value");
        var get = batch.HashGetAsync("batched", "field");
        Assert.False(set.IsCompleted);
        batch.Execute();
        Assert.True(await set);
        Assert.Equal("value", (string?)await get);
    }

    [Test]
    public async Task BorrowedServerSharesStateAndSurvivesConnectionDisposal()
    {
        var clock = new RespireFakeClock();
        await using var server = new RespireFakeServer(clock);
        await using var first = new RespireFakeConnection(server);
        await using var second = new RespireFakeConnection(server);
        await first.Database.HashSetAsync("shared", "field", "value");
        await first.Database.KeyExpireAsync("shared", TimeSpan.FromSeconds(1));
        Assert.Equal("value", (string?)await second.Database.HashGetAsync("shared", "field"));
        await first.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => first.Connection.GetDatabase());
        Assert.Equal("value", (string?)await second.Database.HashGetAsync("shared", "field"));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True((await second.Database.HashGetAsync("shared", "field")).IsNull);
        await second.DisposeAsync();
        await using var later = new RespireFakeConnection(server);
        Assert.True(await later.Database.HashSetAsync("later", "field", "still alive"));
    }

    [Test]
    public async Task DefaultFixturesAreIsolatedAndDisposeTheirServers()
    {
        await using var first = new RespireFakeConnection();
        await using var second = new RespireFakeConnection();
        await first.Database.HashSetAsync("key", "field", "first");
        Assert.True((await second.Database.HashGetAsync("key", "field")).IsNull);
        var database = first.Database;
        await first.DisposeAsync();
        await first.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => first.Server.CreateOptions());
        Assert.Throws<ObjectDisposedException>(() => first.Database);
        Assert.Throws<ObjectDisposedException>(() => database.HashGet("key", "field"));
        Assert.False(first.Connection.IsConnected);
        Assert.True(await second.Database.HashSetAsync("key", "field", "second"));
    }

    [Test]
    public async Task DisposalDrainsClientsBeforeClosingOwnedServer()
    {
        await using var fixture = new RespireFakeConnection();
        var gate = new RespireFakeGate();
        using var fault = fixture.Server.InjectFault("HSET", RespireFakeFault.Pause(gate, afterExecution: true));
        var pending = fixture.Database.HashSetAsync("key", "field", "value");
        Task? disposal = null;
        try
        {
            await fault.Matched.WaitAsync(TimeSpan.FromSeconds(10));
            disposal = fixture.DisposeAsync().AsTask();
            var again = fixture.DisposeAsync().AsTask();
            Assert.Same(disposal, again);
            Assert.False(disposal.IsCompleted);
            // The server is still alive while the multiplexer drains the held response.
            await using var observer = await RespireClient.ConnectAsync(fixture.Server.CreateOptions());
            Assert.Equal("value", await observer.Hashes.GetAsync<string>("key", "field"));
        }
        finally
        {
            gate.Release();
            await pending.WaitAsync(TimeSpan.FromSeconds(10));
            if (disposal is not null) await disposal.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.True(await pending);
        Assert.Throws<ObjectDisposedException>(() => fixture.Server.CreateOptions());
    }

    [Test]
    public async Task DisposingConnectionDirectlyStillLeavesServerForFixtureCleanup()
    {
        await using var fixture = new RespireFakeConnection();
        await fixture.Database.HashSetAsync("key", "field", "value");
        fixture.Connection.Dispose();
        Assert.Throws<ObjectDisposedException>(() => fixture.Database);
        await using (var observer = new RespireFakeConnection(fixture.Server))
            Assert.Equal("value", (string?)await observer.Database.HashGetAsync("key", "field"));
        await fixture.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => fixture.Server.CreateOptions());
    }

    [Test]
    public async Task UnsupportedMembersCommandsAndDatabasesFailExplicitly()
    {
        await using var fixture = new RespireFakeConnection();
        Assert.Same(fixture.Database, fixture.Connection.GetDatabase(0));
        Assert.Throws<NotSupportedException>(() => fixture.Connection.GetDatabase(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Connection.GetDatabase(-2));
        Assert.Throws<NotSupportedException>(() => fixture.Database.StringGetLease("key"));
        Assert.Throws<NotSupportedException>(() => { _ = fixture.Database.StringSetAsync("key", "value"); });
        var error = await Assert.ThrowsAsync<RespireServerException>(
            () => fixture.Database.ScriptEvaluateAsync("return 1"));
        Assert.Contains("fake server supports only", error.Message);
        Assert.True(await fixture.Database.HashSetAsync("after-error", "field", "value"));
        Assert.Equal("value", (string?)await fixture.Database.HashGetAsync("after-error", "field"));
    }

    [Test]
    [Arguments(RespireProtocol.Resp2)]
    [Arguments(RespireProtocol.Resp3)]
    public async Task DirectCompositionUsesTheSameFakeTransport(RespireProtocol protocol)
    {
        await using var server = new RespireFakeServer();
        await using var connection = RespireConnectionMultiplexer.Create(server.CreateOptions() with { Protocol = protocol });
        Assert.True(await connection.GetDatabase().HashSetAsync("direct", "field", "value"));
        Assert.Equal("value", (string?)connection.GetDatabase().HashGet("direct", "field"));
    }

    [Test]
    public async Task InvalidServersFailDuringConstruction()
    {
        Assert.Throws<ArgumentNullException>(() => new RespireFakeConnection(null!));
        var server = new RespireFakeServer();
        await server.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => new RespireFakeConnection(server));
    }
}
