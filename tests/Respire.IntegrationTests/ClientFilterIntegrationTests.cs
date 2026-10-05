using FluentAssertions;
using Testcontainers.Redis;
using TUnit.Core;

namespace Respire.IntegrationTests;

[NotInParallel]
public class ClientFilterIntegrationTests
{
    [Test]
    [Arguments("redis:8.10-alpine", 2, 0)]
    [Arguments("redis:8.10-alpine", 3, 0)]
    [Arguments("redis:8.10-alpine", 2, 1)]
    [Arguments("redis:8.10-alpine", 3, 1)]
    [Arguments("redis:8.10-alpine", 2, 2)]
    [Arguments("redis:8.10-alpine", 3, 2)]
    [Arguments("valkey/valkey:9.0-alpine", 2, 0)]
    [Arguments("valkey/valkey:9.0-alpine", 3, 0)]
    [Arguments("valkey/valkey:9.0-alpine", 2, 1)]
    [Arguments("valkey/valkey:9.0-alpine", 3, 1)]
    [Arguments("valkey/valkey:9.0-alpine", 2, 2)]
    [Arguments("valkey/valkey:9.0-alpine", 3, 2)]
    public async Task FiltersSelectAndKillOnlyOwnedTargets(string image, int protocol, int execution)
    {
        await using var container = new RedisBuilder(image).Build();
        await container.StartAsync();
        var options = new RespireOptions
        {
            Endpoints = [new(container.Hostname, container.GetMappedPublicPort(6379))],
            Protocol = protocol == 2 ? RespProtocol.Resp2 : RespProtocol.Resp3,
            Connections = 1, AllowAdmin = true, ClientName = "filter-admin",
        };
        await using var admin = await RespireClient.ConnectAsync(options);
        await using var target = await RespireClient.ConnectAsync(options with { ClientName = "filter-target" });
        await using var survivor = await RespireClient.ConnectAsync(options with { ClientName = "filter-survivor" });
        var targetHandle = await target.Server.GetClientConnectionAsync();
        var survivorHandle = await survivor.Server.GetClientConnectionAsync();
        var adminHandle = await admin.Server.GetClientConnectionAsync();
        var targetInfo = await targetHandle.InfoAsync();
        var valkey = image.StartsWith("valkey/", StringComparison.Ordinal);
        if (valkey) await admin.Server.AclSetUserAsync("unused-user", ["off"]);
        var filter = new RespireClientFilterOptions { Ids = [targetHandle.Id, survivorHandle.Id] };
        if (valkey)
            filter = filter with
            {
                Type = RespireClientType.Normal, ExcludedIds = [survivorHandle.Id], ExcludedType = RespireClientType.PubSub,
                ExcludedName = "filter-survivor", ExcludedAddress = (await survivorHandle.InfoAsync()).Address,
                ExcludedUser = "unused-user", ExcludedLocalAddress = "127.0.0.1:1",
                ExcludedDatabase = 1, ExcludedLibraryName = "unused-library", ExcludedLibraryVersion = "unused-version",
                ExcludedIp = "192.0.2.1", Name = "filter-target", Database = 0,
            };
        else
        {
            (await adminHandle.ClientsAsync(new() { Type = RespireClientType.Normal }))
                .Select(row => row.Id).Should().Contain(targetHandle.Id);
            Func<Task> combined = async () => await adminHandle.ClientsAsync(filter with { Type = RespireClientType.Normal });
            await combined.Should().ThrowAsync<RespireServerException>();
            Func<Task> unsupported = async () => await adminHandle.ClientsAsync(new() { ExcludedIds = [survivorHandle.Id] });
            await unsupported.Should().ThrowAsync<RespireServerException>();
        }
        RespireServerClientInfo[] rows;
        if (execution == 0) rows = await adminHandle.ClientsAsync(filter);
        else
        {
            using var batch = admin.CreateBatch();
            await using var tx = admin.CreateTransaction();
            IRespireCommandQueue queue = execution == 1 ? batch : tx;
            var result = queue.Server.Clients(filter);
            if (execution == 1) await batch.ExecuteAsync(); else await tx.CommitAsync();
            rows = result.Result;
        }
        rows.Select(row => row.Id).Should().BeEquivalentTo(valkey ? [targetHandle.Id] : new[] { targetHandle.Id, survivorHandle.Id });
        var kill = filter with
        {
            Type = RespireClientType.Normal, Ids = [targetHandle.Id], User = "default", Address = targetInfo.Address,
            LocalAddress = targetInfo.Attributes["laddr"], SkipMe = true,
        };
        long killed;
        if (execution == 0) killed = await adminHandle.KillClientsAsync(kill);
        else
        {
            using var batch = admin.CreateBatch();
            await using var tx = admin.CreateTransaction();
            IRespireCommandQueue queue = execution == 1 ? batch : tx;
            var result = queue.Server.KillClients(kill);
            if (execution == 1) await batch.ExecuteAsync(); else await tx.CommitAsync();
            killed = result.Result;
        }
        killed.Should().Be(1);
        (await adminHandle.ClientsAsync(new() { Ids = [targetHandle.Id] })).Should().BeEmpty();
        (await survivorHandle.InfoAsync()).Id.Should().Be(survivorHandle.Id);
        (await adminHandle.KillClientsAsync(new() { Ids = [adminHandle.Id], SkipMe = true })).Should().Be(0);
        (await adminHandle.InfoAsync()).Id.Should().Be(adminHandle.Id);
        // An unattainable positive age exercises MAXAGE without timing sleeps or collateral kills.
        (await adminHandle.KillClientsAsync(new() { Ids = [survivorHandle.Id], MaximumAgeSeconds = long.MaxValue })).Should().Be(0);
        var fanout = await admin.Server.ClientsOnAllNodesAsync(new() { Ids = [survivorHandle.Id] }, default);
        fanout.Should().ContainSingle();
        fanout[0].Value.Single().Id.Should().Be(survivorHandle.Id);
    }
}
