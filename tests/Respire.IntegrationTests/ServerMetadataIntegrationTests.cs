using FluentAssertions;
using Testcontainers.Redis;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ClassDataSource<VersionedServerFixture>(Shared = SharedType.PerTestSession)]
public class ServerMetadataIntegrationTests(VersionedServerFixture servers)
{
    [Test]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task InspectMetadataAndPersistOnlyAnOwnedServer(int protocol, bool modern)
    {
        // CONFIG REWRITE and persistence affect the whole server. This test owns its server,
        // writable configuration and ephemeral /data; no shared fixture is mutated.
        await using var container = new RedisBuilder(modern ? "redis:7.2-alpine" : "redis:6.2.14-alpine")
            .WithEntrypoint("/bin/sh")
            .WithCommand("-c", "printf 'bind 0.0.0.0\nprotected-mode no\ndir /data\nsave \"\"\nappendonly yes\n' > /data/metadata.conf; exec redis-server /data/metadata.conf")
            .Build();
        await container.StartAsync();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(container.Hostname, container.GetMappedPublicPort(6379))], Connections = 1,
            Protocol = protocol == 2 ? RespProtocol.Resp2 : RespProtocol.Resp3, AllowAdmin = true,
        });
        var server = client.WithKeyPrefix("ignored:").Server;
        var info = await server.CommandInfoAsync(["GET", "NOTAREDISCOMMAND"]);
        info.Should().HaveCount(2);
        info[0]!.Name.Should().Be("get");
        info[0]!.Arity.Should().Be(2);
        info[0]!.Flags.Should().Contain("readonly");
        info[0]!.FirstKey.Should().Be(1);
        info[1].Should().BeNull();
        if (modern)
        {
            var docs = await server.CommandDocsAsync(["GET", "CONFIG GET", "NOTAREDISCOMMAND"]);
            docs.Select(x => x.Name).Should().BeEquivalentTo("get", "config|get");
            docs.Single(x => x.Name == "get").Arguments.Should().Contain(x => x.Type == "key");
            (await server.CommandInfoAsync(["CONFIG GET"]))[0]!.Name.Should().Be("config|get");
            info[0]!.KeySpecifications.Should().NotBeEmpty();
            var setDocs = (await server.CommandDocsAsync(["SET"])).Single();
            setDocs.History.Should().NotBeEmpty();
            (await server.CommandInfoAsync(["CONFIG"]))[0]!.Subcommands.Should().Contain(x => x.Name == "config|get");
            (await server.CommandDocsOnAllNodesAsync(["GET"]))[0].Value.Single().Name.Should().Be("get");
        }
        else
        {
            Func<Task> unsupported = async () => await server.CommandDocsAsync(["GET"]);
            await unsupported.Should().ThrowAsync<RespireServerException>();
            (await server.CommandDocsOnAllNodesAsync(["GET"]))[0].Error.Should().BeOfType<RespireServerException>();
            info[0]!.KeySpecifications.Should().BeEmpty();
        }
        byte[] key = [255, 0, 32, 128];
        (await server.CommandGetKeysAsync("MSET", [key, "value", "other", "value2"]))[0].Should().Equal(key);
        (await server.CommandGetKeysAsync("XGROUP CREATE", [key, "group", "0"]))[0].Should().Equal(key);
        Func<Task> badCommand = async () => await server.CommandGetKeysAsync("NOTAREDISCOMMAND", []);
        await badCommand.Should().ThrowAsync<RespireServerException>();
        (await server.ModuleListAsync()).Should().BeEmpty();
        (await server.CommandInfoOnAllNodesAsync(["GET"]))[0].Value[0]!.Name.Should().Be("get");
        var keysByNode = await server.CommandGetKeysOnAllNodesAsync("GET", [key]);
        keysByNode.Single().Endpoint.Port.Should().Be(container.GetMappedPublicPort(6379));
        keysByNode[0].Value.Single().Should().Equal(key);
        (await server.ModuleListOnAllNodesAsync())[0].Value.Should().BeEmpty();

        await client.Strings.SetAsync("persisted", "value");
        await server.ConfigRewriteAsync();
        await server.ConfigResetStatisticsAsync();
        await server.SaveAsync();
        (await server.LastSaveAsync()).Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(-5));
        (await server.ConfigRewriteOnAllNodesAsync())[0].Value.Should().BeTrue();
        (await server.ConfigResetStatisticsOnAllNodesAsync())[0].Value.Should().BeTrue();
        (await server.SaveOnAllNodesAsync())[0].Value.Should().BeTrue();
        (await server.BackgroundSaveAsync(schedule: true)).State.Should().Be(RespireBackgroundPersistenceState.Started);
        await WaitForPersistence(server);
        (await server.BackgroundSaveOnAllNodesAsync())[0].Value.State.Should().Be(RespireBackgroundPersistenceState.Started);
        await WaitForPersistence(server);
        (await server.BackgroundRewriteAofAsync()).State.Should().Be(RespireBackgroundPersistenceState.Started);
        await WaitForPersistence(server);
        (await server.BackgroundRewriteAofOnAllNodesAsync())[0].Value.State.Should().Be(RespireBackgroundPersistenceState.Started);
        await WaitForPersistence(server);
        (await server.InfoAsync("persistence")).Should().Contain("rdb_last_bgsave_status:ok").And.Contain("aof_last_bgrewrite_status:ok");
        await client.DisposeAsync();
        info[0]!.AclCategories.Should().Contain("@read");
        keysByNode[0].Value[0].Should().Equal(key);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ConfigurationWithoutAFilePreservesServerError(int protocol)
    {
        // A server started without a config file rejects CONFIG REWRITE and changes nothing.
        var lease = await servers.LeaseAsync("redis:7.2-alpine");
        await using var client = await RespireClient.ConnectAsync(lease.ConnectionString(protocol) + "&allowAdmin=true");
        Func<Task> rewrite = async () => await client.Server.ConfigRewriteAsync();
        await rewrite.Should().ThrowAsync<RespireServerException>();
        (await client.Server.ConfigRewriteOnAllNodesAsync())[0].Error.Should().BeOfType<RespireServerException>();
    }

    private static async Task WaitForPersistence(IServerCommands server)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var report = await server.InfoAsync("persistence", timeout.Token);
            if (report.Contains("rdb_bgsave_in_progress:0") && report.Contains("aof_rewrite_in_progress:0")
                && report.Contains("aof_rewrite_scheduled:0")) return;
            await Task.Delay(20, timeout.Token);
        }
    }
}
