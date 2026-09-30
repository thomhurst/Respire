using DotNet.Testcontainers.Builders;
using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

public class ServerExtensionIntegrationTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task DragonflyRateLimitsAndExpiringMembersUseCatalogDescriptors(int protocol)
    {
        await using var container = new ContainerBuilder("docker.dragonflydb.io/dragonflydb/dragonfly:v2.0.0")
            .WithCommand("--proactor_threads=1", "--maxmemory=256mb", "--logtostderr")
            .WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build();
        await container.StartAsync();
        await using var client = await RespireClient.ConnectAsync(
            $"redis://{container.Hostname}:{container.GetMappedPublicPort(6379)}?protocol={protocol}");
        using var allowed = await client.ExecuteAsync(RespireCommands.Dragonfly.CL_THROTTLE, "user", 0, 1, 3600, 1);
        allowed.Count.Should().Be(5);
        allowed[0].AsInteger().Should().Be(0);
        allowed[3].AsInteger().Should().Be(-1);
        using var limited = await client.ExecuteAsync(RespireCommands.Dragonfly.CL_THROTTLE, "user", 0, 1, 3600, 1);
        limited[0].AsInteger().Should().Be(1);
        limited[3].AsInteger().Should().BeGreaterThan(0);
        byte[] member = [0xff, 0, 0x80];
        using var added = await client.ExecuteAsync(RespireCommands.Dragonfly.SADDEX, "members", 60, member);
        added.AsInteger().Should().Be(1);
        using var ttl = await client.ExecuteAsync(RespireCommands.Dragonfly.FIELDTTL, "members", member);
        ttl.AsInteger().Should().BeInRange(1, 60);
        using var expiry = await client.ExecuteAsync(RespireCommands.Dragonfly.FIELDEXPIRE, "members", 120, member);
        expiry[0].AsInteger().Should().Be(1);
        using var members = await client.ExecuteAsync(RespireCommands.Set.SMEMBERS, "members");
        members[0].AsBytes().Should().Equal(member);
        Func<Task> invalid = async () =>
        {
            using var result = await client.ExecuteAsync(RespireCommands.Dragonfly.CL_THROTTLE, "user");
        };
        await invalid.Should().ThrowAsync<RespireServerException>();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task KeyDbMemberExpiryAndHashRenameUseCatalogDescriptors(int protocol)
    {
        // This pinned image targets the Linux x64 CI runner; ARM hosts require x64 emulation.
        await using var container = new ContainerBuilder("eqalpha/keydb:x86_64_v6.3.4")
            .WithCommand("keydb-server", "--server-threads", "1", "--save", "", "--appendonly", "no")
            .WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build();
        await container.StartAsync();
        await using var client = await RespireClient.ConnectAsync(
            $"redis://{container.Hostname}:{container.GetMappedPublicPort(6379)}?protocol={protocol}");
        byte[] member = [0xff, 0, 0x80];
        using var added = await client.ExecuteAsync(RespireCommands.Set.SADD, "members", member);
        using var expires = await client.ExecuteAsync(RespireCommands.KeyDb.EXPIREMEMBER, "members", member, 60);
        expires.AsInteger().Should().Be(1);
        using var expiresAt = await client.ExecuteAsync(RespireCommands.KeyDb.PEXPIREMEMBERAT,
            "members", member, DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds());
        expiresAt.AsInteger().Should().Be(1);
        // KeyDB extends PTTL with a subkey argument; the wire verb uses the shared descriptor.
        using var ttl = await client.ExecuteAsync(RespireCommands.Key.PTTL, "members", member);
        ttl.AsInteger().Should().BeInRange(1, 120000);
        using var hash = await client.ExecuteAsync(RespireCommands.Hash.HSET, "hash", "old", member);
        using var renamed = await client.ExecuteAsync(RespireCommands.KeyDb.KEYDB_HRENAME, "hash", "old", "new");
        renamed.AsInteger().Should().Be(1);
        using var value = await client.ExecuteAsync(RespireCommands.Hash.HGET, "hash", "new");
        value.AsBytes().Should().Equal(member);
        Func<Task> invalid = async () =>
        {
            using var result = await client.ExecuteAsync(RespireCommands.KeyDb.EXPIREMEMBER, "members", member, "invalid");
        };
        await invalid.Should().ThrowAsync<RespireServerException>();
    }
}
