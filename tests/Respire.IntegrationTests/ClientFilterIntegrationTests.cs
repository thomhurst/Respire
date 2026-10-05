using FluentAssertions;
using Testcontainers.Redis;
using TUnit.Core;

namespace Respire.IntegrationTests;

[NotInParallel]
public class ClientFilterIntegrationTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ValkeyFiltersIndependentlyChangeSelectionAndKillCounts(int protocol)
    {
        await using var container = new RedisBuilder("valkey/valkey:9.0-alpine").Build();
        await container.StartAsync();
        var options = new RespireOptions
        {
            Endpoints = [new(container.Hostname, container.GetMappedPublicPort(6379))],
            Protocol = (RespProtocol)protocol, Connections = 1, AllowAdmin = true,
        };
        await using var admin = await RespireClient.ConnectAsync(options);
        await admin.Server.AclSetUserAsync("filter-user", ["on", ">test-password", "+@all", "~*", "&*"]);
        var handle = await admin.Server.GetClientConnectionAsync();
        string[] cases = ["type", "user", "address", "local-address", "name", "flags", "library", "version", "database", "capabilities", "ip",
            "excluded-type", "excluded-ids", "excluded-user", "excluded-address", "excluded-local-address", "excluded-name",
            "excluded-flags", "excluded-library", "excluded-version", "excluded-database", "excluded-capabilities", "excluded-ip"];
        foreach (var filter in cases)
        {
            await using var target = await RespireClient.ConnectAsync(options with
            {
                Username = "filter-user", Password = "test-password", ClientName = "filter-target", Database = 1,
            });
            var targetHandle = await target.Server.GetClientConnectionAsync();
            await targetHandle.SetInfoAsync(RespireClientInfoAttribute.LibraryName, "filter-library");
            await targetHandle.SetInfoAsync(RespireClientInfoAttribute.LibraryVersion, "1.2.3");
            await targetHandle.SetNoEvictAsync(true);
            using (var response = await target.ExecuteAsync("CLIENT", "CAPA", "redirect")) { }
            var info = await targetHandle.InfoAsync();
            var ip = info.Address[..info.Address.LastIndexOf(':')].Trim('[', ']');
            var baseline = new RespireClientFilterOptions { Ids = [targetHandle.Id] };
            var (matches, excludes) = filter switch
            {
                "type" => (baseline with { Type = RespireClientType.Normal }, baseline with { Type = RespireClientType.PubSub }),
                "user" => (baseline with { User = "filter-user" }, baseline with { User = "default" }),
                "address" => (baseline with { Address = info.Address }, baseline with { Address = "127.0.0.1:1" }),
                "local-address" => (baseline with { LocalAddress = info.Attributes["laddr"] }, baseline with { LocalAddress = "127.0.0.1:1" }),
                "name" => (baseline with { Name = "filter-target" }, baseline with { Name = "different" }),
                "flags" => (baseline with { Flags = "e" }, baseline with { Flags = "b" }),
                "library" => (baseline with { LibraryName = "filter-library" }, baseline with { LibraryName = "different" }),
                "version" => (baseline with { LibraryVersion = "1.2.3" }, baseline with { LibraryVersion = "different" }),
                "database" => (baseline with { Database = 1 }, baseline with { Database = 0 }),
                "capabilities" => (baseline with { Capabilities = "r" }, baseline with { ExcludedCapabilities = "r" }),
                "ip" => (baseline with { Ip = ip }, baseline with { Ip = "192.0.2.1" }),
                "excluded-type" => (baseline with { ExcludedType = RespireClientType.PubSub }, baseline with { ExcludedType = RespireClientType.Normal }),
                "excluded-ids" => (baseline with { ExcludedIds = [handle.Id] }, baseline with { ExcludedIds = [targetHandle.Id] }),
                "excluded-user" => (baseline with { ExcludedUser = "default" }, baseline with { ExcludedUser = "filter-user" }),
                "excluded-address" => (baseline with { ExcludedAddress = "127.0.0.1:1" }, baseline with { ExcludedAddress = info.Address }),
                "excluded-local-address" => (baseline with { ExcludedLocalAddress = "127.0.0.1:1" }, baseline with { ExcludedLocalAddress = info.Attributes["laddr"] }),
                "excluded-name" => (baseline with { ExcludedName = "different" }, baseline with { ExcludedName = "filter-target" }),
                "excluded-flags" => (baseline with { ExcludedFlags = "b" }, baseline with { ExcludedFlags = "e" }),
                "excluded-library" => (baseline with { ExcludedLibraryName = "different" }, baseline with { ExcludedLibraryName = "filter-library" }),
                "excluded-version" => (baseline with { ExcludedLibraryVersion = "different" }, baseline with { ExcludedLibraryVersion = "1.2.3" }),
                "excluded-database" => (baseline with { ExcludedDatabase = 0 }, baseline with { ExcludedDatabase = 1 }),
                "excluded-capabilities" => (baseline with { ExcludedCapabilities = "r" }, baseline with { Capabilities = "r" }),
                "excluded-ip" => (baseline with { ExcludedIp = "192.0.2.1" }, baseline with { ExcludedIp = ip }),
                _ => throw new InvalidOperationException(filter),
            };
            if (filter == "excluded-capabilities")
            {
                // A fresh client has no redirect capability; use that absence to exercise NOT-CAPA independently.
                await using var ordinary = await RespireClient.ConnectAsync(options);
                var ordinaryHandle = await ordinary.Server.GetClientConnectionAsync();
                matches = matches with { Ids = [ordinaryHandle.Id] };
                excludes = excludes with { Ids = [ordinaryHandle.Id] };
                await Check(matches, excludes, ordinaryHandle.Id, filter);
            }
            else await Check(matches, excludes, targetHandle.Id, filter);
        }

        async Task Check(RespireClientFilterOptions matches, RespireClientFilterOptions excludes, long id, string filter)
        {
            (await handle.ClientsAsync(matches)).Select(row => row.Id).Should().Equal([id], filter);
            (await handle.ClientsAsync(excludes)).Should().BeEmpty(filter);
            (await handle.KillClientsAsync(excludes)).Should().Be(0, filter);
            (await handle.KillClientsAsync(matches)).Should().Be(1, filter);
            (await handle.ClientsAsync(new() { Ids = [id] })).Should().BeEmpty(filter);
        }
    }

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
