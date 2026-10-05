using System.Text;
using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ClassDataSource<VersionedServerFixture>(Shared = SharedType.PerTestSession)]
public class ServerCommandLogIntegrationTests(VersionedServerFixture servers)
{
    private const string CommandLogKey = "valkey-commandlog";

    // Logging thresholds and resets affect the whole node, so the Valkey cases share a server that
    // only they use, one case at a time; each case resets every log and threshold it relies on.
    [ClassDataSource<VersionedServerFixture>(Shared = SharedType.Keyed, Key = CommandLogKey)]
    public required VersionedServerFixture CommandLogServers { get; init; }

    [Test]
    [NotInParallel(CommandLogKey)]
    [MatrixDataSource]
    public async Task ValkeyLogsKeepUnitsBinaryArgumentsCountsAndIndependentResets([Matrix(2, 3)] int protocol,
        [Matrix(RespireCommandLogType.Slow, RespireCommandLogType.LargeRequest, RespireCommandLogType.LargeReply)] RespireCommandLogType type)
    {
        var lease = await CommandLogServers.LeaseAsync("valkey/valkey:8.1-alpine");
        var address = lease.ConnectionString(protocol);
        await using var client = await RespireClient.ConnectAsync(address + "&allowAdmin=true&connections=1&clientName=commandlog-owner");
        var server = client.WithKeyPrefix("ignored:").Server;
        string[] thresholds = ["commandlog-execution-slower-than", "commandlog-request-larger-than", "commandlog-reply-larger-than"];
        foreach (var threshold in thresholds) await server.SetConfigAsync(threshold, "-1");
        foreach (var logType in Enum.GetValues<RespireCommandLogType>()) await server.ResetCommandLogAsync(logType);
        await server.SetConfigAsync(thresholds[(int)type], "0");
        byte[] key = [0, 255, 32], value = [254, 0, 32, 1];
        await client.SetAsync(key, value);
        (await client.GetBytesAsync(key)).Should().Equal(value);
        await server.SetConfigAsync(thresholds[(int)type], "-1");
        var all = await server.CommandLogAsync(type, -1);
        all.Should().NotBeEmpty();
        all.Select(x => x.Id).Should().BeInDescendingOrder();
        var write = all.Single(x => x.Arguments.Length >= 3 && Encoding.ASCII.GetString(x.Arguments[0]).Equals("SET", StringComparison.OrdinalIgnoreCase));
        write.Type.Should().Be(type);
        write.Arguments[1].Should().Equal(key);
        write.Arguments[2].Should().Equal(value);
        write.ClientName.Should().Equal("commandlog-owner"u8.ToArray());
        write.ClientAddress.Should().NotBeEmpty();
        write.TimestampUnixSeconds.Should().BeGreaterThan(0);
        if (type == RespireCommandLogType.Slow) { write.DurationMicroseconds.Should().BeGreaterThanOrEqualTo(0); write.RequestBytes.Should().BeNull(); }
        else if (type == RespireCommandLogType.LargeRequest) { write.RequestBytes.Should().BeGreaterThan(0); write.DurationMicroseconds.Should().BeNull(); }
        else { write.ReplyBytes.Should().BeGreaterThan(0); write.DurationMicroseconds.Should().BeNull(); }
        (await server.CommandLogAsync(type, 0)).Should().BeEmpty();
        (await server.CommandLogAsync(type, 1)).Single().Id.Should().Be(all[0].Id);
        (await server.CommandLogLengthAsync(type)).Should().Be(all.Length);
        var fanOut = await server.CommandLogOnAllNodesAsync(type, -1);
        fanOut.Should().ContainSingle();
        fanOut[0].Endpoint.Port.Should().Be(lease.Port);
        fanOut[0].Value.Select(x => x.Id).Should().Equal(all.Select(x => x.Id));
        (await server.CommandLogLengthOnAllNodesAsync(type)).Single().Value.Should().Be(all.Length);
        foreach (var other in Enum.GetValues<RespireCommandLogType>().Where(x => x != type))
            (await server.CommandLogLengthAsync(other)).Should().Be(0);

        // A different log's RESET cannot clear this one.
        await server.ResetCommandLogAsync((RespireCommandLogType)(((int)type + 1) % 3));
        (await server.CommandLogLengthAsync(type)).Should().Be(all.Length);
        (await server.ResetCommandLogOnAllNodesAsync(type)).Single().Value.Should().BeTrue();
        (await server.CommandLogLengthAsync(type)).Should().Be(0);
        await server.SetConfigAsync(thresholds[(int)type], "0");
        await client.SetAsync(key, value);
        await server.SetConfigAsync(thresholds[(int)type], "-1");
        (await server.CommandLogAsync(type, 1)).Single().Id.Should().BeGreaterThan(all[0].Id);
        await server.ResetCommandLogAsync(type);
        (await server.CommandLogAsync(type, -1)).Should().BeEmpty();

        // Server ACL errors remain intact even though inspection does not require AllowAdmin locally.
        await server.AclSetUserAsync("restricted", ["reset", "on", ">commandlog-test-password", "+ping"]);
        await using var restricted = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [lease.Endpoint], Connections = 1,
            Protocol = (RespProtocol)protocol, Username = "restricted", Password = "commandlog-test-password",
        });
        Func<Task> denied = async () => await restricted.Server.CommandLogAsync(type);
        (await denied.Should().ThrowAsync<RespireServerException>()).Which.Code.Should().Be("NOPERM");
        await client.DisposeAsync();
        write.Arguments[2].Should().Equal(value);
        fanOut[0].Value.Should().HaveCount(all.Length);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task RedisDoesNotSilentlySubstituteSlowlog(int protocol)
    {
        // Unsupported commands change nothing, so the shared Redis 7.2 server is safe.
        var lease = await servers.LeaseAsync("redis:7.2-alpine");
        await using var client = await RespireClient.ConnectAsync(lease.ConnectionString(protocol) + "&allowAdmin=true");
        foreach (var type in Enum.GetValues<RespireCommandLogType>())
        {
            Func<Task>[] unsupported = [async () => await client.Server.CommandLogAsync(type),
                async () => await client.Server.CommandLogLengthAsync(type), async () => await client.Server.ResetCommandLogAsync(type)];
            foreach (var action in unsupported) await action.Should().ThrowAsync<RespireServerException>();
            (await client.Server.CommandLogOnAllNodesAsync(type)).Single().Error.Should().BeOfType<RespireServerException>();
            (await client.Server.CommandLogLengthOnAllNodesAsync(type)).Single().Error.Should().BeOfType<RespireServerException>();
            (await client.Server.ResetCommandLogOnAllNodesAsync(type)).Single().Error.Should().BeOfType<RespireServerException>();
        }
    }
}
