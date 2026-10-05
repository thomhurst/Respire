using FluentAssertions;
using Testcontainers.Redis;
using TUnit.Core;

namespace Respire.IntegrationTests;

public class ServerFlushIntegrationTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task EveryModeAndExecutionFormDeletesTheIntendedDatabases(int protocol)
    {
        // FLUSHALL must never share a container with other tests.
        await using var container = new RedisBuilder("redis:7.0.15").Build();
        await container.StartAsync();
        RespireOptions Options(int database) => new()
        {
            Protocol = (RespProtocol)protocol, Database = database, AllowAdmin = true,
            Endpoints = { new RespireEndpoint(container.Hostname, container.GetMappedPublicPort(6379)) },
        };
        await using var first = await RespireClient.ConnectAsync(Options(0));
        await using var second = await RespireClient.ConnectAsync(Options(1));
        foreach (var mode in Enum.GetValues<ServerFlushMode>())
        foreach (var execution in new[] { 0, 1, 2 })
        {
            await first.SetAsync("key", (RespireValue)"first");
            await second.SetAsync("key", (RespireValue)"second");
            await FlushAsync(first, all: false, mode, execution);
            (await first.ExistsAsync("key")).Should().BeFalse();
            (await second.GetStringAsync("key")).Should().Be("second");
            await first.SetAsync("key", (RespireValue)"replacement");
            await FlushAsync(first, all: true, mode, execution);
            (await first.ExistsAsync("key")).Should().BeFalse();
            (await second.ExistsAsync("key")).Should().BeFalse();
        }
    }

    private static async Task FlushAsync(RespireClient client, bool all, ServerFlushMode mode, int execution)
    {
        if (execution == 0)
        {
            if (all) await client.Server.FlushAllAsync(mode); else await client.Server.FlushDatabaseAsync(mode);
            return;
        }
        using var batch = client.CreateBatch();
        await using var tx = client.CreateTransaction();
        IRespireCommandQueue queue = execution == 2 ? tx : batch;
        var pending = all ? queue.Server.FlushAll(mode) : queue.Server.FlushDatabase(mode);
        if (execution == 2) await tx.CommitAsync(); else await batch.ExecuteAsync();
        pending.Result.Should().BeTrue();
    }
}
