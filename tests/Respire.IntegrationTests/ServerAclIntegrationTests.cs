using System.Text;
using FluentAssertions;
using Testcontainers.Redis;
using TUnit.Core;

namespace Respire.IntegrationTests;

public class ServerAclIntegrationTests
{
    [Test]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task AdministerIsolatedUsersAndInspectNodeLocalResults(int protocol, bool modern)
    {
        // ACL state and logs are global to a server, so this test owns the entire server.
        await using var container = new RedisBuilder(modern ? "redis:7.2.4" : "redis:6.2.14").Build();
        await container.StartAsync();
        var address = $"redis://{container.Hostname}:{container.GetMappedPublicPort(6379)}?protocol={protocol}";
        await using var client = await RespireClient.ConnectAsync(address + "&allowAdmin=true");
        var server = client.WithKeyPrefix("ignored:").Server;
        (await server.AclWhoAmIAsync()).Should().Equal("default"u8.ToArray());
        (await server.AclGetUserAsync("missing")).Should().BeNull();
        await server.AclSetUserAsync("empty", []);
        (await server.AclGetUserAsync("empty"))!.Flags.Should().Contain("off");
        await server.AclSetUserAsync("reader", ["reset", "on", ">acl-test-password", "~allowed:*", "+get", "+acl|whoami"]);
        if (modern) await server.AclSetUserAsync("reader", ["(+get ~selected:*)"]);
        var user = (await server.AclGetUserAsync("reader"))!;
        user.Flags.Should().Contain("on");
        user.PasswordHashes.Should().ContainSingle();
        user.PasswordHashes[0].Should().HaveLength(64);
        if (modern)
        {
            Encoding.UTF8.GetString(user.Keys.RuleExpression!).Should().Be("~allowed:*");
            user.Selectors.Should().ContainSingle();
            Encoding.UTF8.GetString(user.Selectors[0].Keys.RuleExpression!).Should().Be("~selected:*");
            (await server.AclDryRunAsync("reader", "GET", ["allowed:key"])).IsAllowed.Should().BeTrue();
            (await server.AclDryRunAsync("reader", "GET", ["selected:key"])).IsAllowed.Should().BeTrue();
            var denied = await server.AclDryRunAsync("reader", "SET", ["allowed:key", "value"]);
            denied.IsAllowed.Should().BeFalse();
            denied.DenialReason.Should().NotBeNullOrEmpty();
            Func<Task> missing = async () => await server.AclDryRunAsync("missing", "GET", ["key"]);
            await missing.Should().ThrowAsync<RespireServerException>();
            Func<Task> invalidCommand = async () => await server.AclDryRunAsync("reader", "NOTAREDISCOMMAND", []);
            await invalidCommand.Should().ThrowAsync<RespireServerException>();
        }
        else user.Keys.LegacyPatterns!.Single().Should().Equal("allowed:*"u8.ToArray());

        (await server.AclListAsync()).Select(Encoding.UTF8.GetString).Should().Contain(line => line.StartsWith("user reader "));
        (await server.AclCategoriesAsync()).Should().Contain("read");
        (await server.AclCategoriesAsync("read")).Should().Contain("get");
        Func<Task> invalidRules = async () => await server.AclSetUserAsync("reader", ["+notarediscommand"]);
        await invalidRules.Should().ThrowAsync<RespireServerException>();
        await server.AclLogResetAsync();
        (await server.AclLogAsync()).Should().BeEmpty();
        await using (var restricted = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(container.Hostname, container.GetMappedPublicPort(6379))], Connections = 1,
            Protocol = protocol == 3 ? RespProtocol.Resp3 : RespProtocol.Resp2, Username = "reader", Password = "acl-test-password",
        }))
        {
            (await restricted.Server.AclWhoAmIAsync()).Should().Equal("reader"u8.ToArray());
            Func<Task> deniedRead = async () => await restricted.Strings.GetAsync<string>("forbidden:key");
            await deniedRead.Should().ThrowAsync<RespireServerException>();
            Func<Task> deniedInspection = async () => await restricted.Server.AclListAsync();
            await deniedInspection.Should().ThrowAsync<RespireServerException>();
        }
        var log = await server.AclLogAsync(10);
        log.Should().NotBeEmpty();
        log.Should().Contain(entry => Encoding.UTF8.GetString(entry.Username) == "reader" && entry.Reason == "key");
        log[0].AgeSeconds.Should().BeGreaterThanOrEqualTo(0);
        if (modern) log[0].EntryId.Should().NotBeNull();

        var who = await server.AclWhoAmIOnAllNodesAsync();
        who.Should().ContainSingle();
        who[0].Endpoint.Port.Should().Be(container.GetMappedPublicPort(6379));
        who[0].Value.Should().Equal("default"u8.ToArray());
        (await server.AclListOnAllNodesAsync())[0].Value.Should().NotBeEmpty();
        (await server.AclGetUserOnAllNodesAsync("reader"))[0].Value!.Flags.Should().Contain("on");
        (await server.AclGetUserOnAllNodesAsync("missing"))[0].Value.Should().BeNull();
        (await server.AclCategoriesOnAllNodesAsync("read"))[0].Value.Should().Contain("get");
        (await server.AclLogOnAllNodesAsync(1))[0].Value.Should().ContainSingle();
        (await server.AclSetUserOnAllNodesAsync("fanout", ["reset", "on", "nopass", "+get", "~*"]))[0].Value.Should().BeTrue();
        if (modern) (await server.AclDryRunOnAllNodesAsync("fanout", "GET", ["key"]))[0].Value.IsAllowed.Should().BeTrue();
        (await server.AclDeleteUsersOnAllNodesAsync(["fanout"]))[0].Value.Should().Be(1);
        (await server.AclLogResetOnAllNodesAsync())[0].Value.Should().BeTrue();
        (await server.AclLogAsync(0)).Should().BeEmpty();
        (await server.AclDeleteUsersAsync(["reader", "empty", "missing"])).Should().Be(2);
        (await server.AclGetUserAsync("reader")).Should().BeNull();
        await client.DisposeAsync();
        user.PasswordHashes[0].Should().HaveLength(64);
        log[0].Username.Should().Equal("reader"u8.ToArray());
    }
}
