using System.Diagnostics;
using DotNet.Testcontainers.Builders;
using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category("ProtocolIndependent")]
public class ServerNodeIntegrationTests
{
    [Test]
    [Arguments("redis:6.2.14-alpine", 2, false, false)]
    [Arguments("redis:6.2.14-alpine", 3, false, false)]
    [Arguments("redis:7.2-alpine", 2, true, false)]
    [Arguments("redis:7.2-alpine", 3, true, false)]
    [Arguments("redis:8.10-alpine", 2, true, true)]
    [Arguments("redis:8.10-alpine", 3, true, true)]
    public async Task ReadCommandsReturnOwnedNodeLocalResults(string image, int protocol, bool keyFlagsSupported, bool backupSupported)
    {
        string[] command = ["redis-server", "--latency-monitor-threshold", "1"];
        if (keyFlagsSupported) command = [.. command, "--enable-debug-command", "yes"];
        await using var container = new ContainerBuilder(image).WithCommand(command).WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build();
        await container.StartAsync();
        var endpoint = new RespireEndpoint(container.Hostname, container.GetMappedPublicPort(6379));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [endpoint], Connections = 1, AllowAdmin = true,
            Protocol = protocol == 2 ? RespProtocol.Resp2 : RespProtocol.Resp3,
        });
        var node = client.WithKeyPrefix("ignored:").Server.OnNode(endpoint);
        (await node.AclGeneratePasswordAsync()).Should().HaveLength(64);
        (await node.AclGeneratePasswordAsync(1)).Should().HaveLength(1);
        (await node.AclGeneratePasswordAsync(1024)).Should().HaveLength(256);
        (await node.AclUsersAsync()).Should().ContainSingle().Which.Should().Equal("default"u8.ToArray());
        (await node.MemoryMallocStatsAsync()).Should().NotBeNullOrEmpty();
        byte[] key = [255, 0, 128];
        await client.Strings.SetAsync(key, "value");
        var keys = await node.KeysAsync("*");
        keys.Should().ContainSingle().Which.Should().Equal(key);
        RespireCommandKeyFlags[] flags = [];
        if (keyFlagsSupported)
        {
            flags = await node.CommandGetKeysAndFlagsAsync("MGET", [key, "missing"]);
            flags.Should().HaveCount(2);
            flags[0].Key.Should().Equal(key);
            flags[0].Flags.Should().Contain("RO").And.Contain("access");
        }
        using (var result = await client.ExecuteAsync(RespireCommands.Server.DEBUG, "SLEEP", "0.02")) { }
        (await node.LatencyGraphAsync("command")).Should().Contain("command");
        if (backupSupported)
        {
            var status = await node.BackupStatusAsync();
            status.State.Should().Be("idle");
            status.Error.Should().BeEmpty();
            status.StartTime.Should().BeNull();
            status.EndTime.Should().BeNull();
            (await node.BackupListAsync()).Should().BeEmpty();
        }
        await client.DisposeAsync();
        keys[0].Should().Equal(key);
        if (keyFlagsSupported) flags[0].Key.Should().Equal(key);
    }

    [Test]
    [Arguments("redis:7.2-alpine", 2, false)]
    [Arguments("redis:7.2-alpine", 3, false)]
    [Arguments("redis:7.2-alpine", 2, true)]
    [Arguments("redis:7.2-alpine", 3, true)]
    [Arguments("redis:8.10-alpine", 2, false)]
    [Arguments("redis:8.10-alpine", 3, false)]
    [Arguments("redis:8.10-alpine", 2, true)]
    [Arguments("redis:8.10-alpine", 3, true)]
    public async Task KillWorksDuringBusyWithSingleConfiguredClientConnection(string image, int protocol, bool function)
    {
        const string password = "issue880-owned-fixture";
        await using var container = new ContainerBuilder(image)
            .WithCommand("redis-server", "--lua-time-limit", "100", "--requirepass", password)
            .WithPortBinding(6379, true).WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build();
        await container.StartAsync();
        var endpoint = new RespireEndpoint(container.Hostname, container.GetMappedPublicPort(6379));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [endpoint], Connections = 1, AllowAdmin = true, Database = 2, ClientName = "busy-origin",
            Username = "default", Password = password, CommandTimeout = TimeSpan.FromSeconds(40),
            Protocol = protocol == 2 ? RespProtocol.Resp2 : RespProtocol.Resp3,
        });
        if (function)
            await client.Functions.LoadAsync("#!lua name=busycontrol\nredis.register_function('busycontrol', function() while true do end end)");
        var running = function
            ? client.ExecuteAsync("FCALL", ["busycontrol", 0]).AsTask()
            : client.ExecuteAsync("EVAL", ["while true do end", 0]).AsTask();
        try
        {
            var elapsed = Stopwatch.StartNew();
            while (true)
            {
                var probe = await container.ExecAsync(["redis-cli", "--no-auth-warning", "-a", password, "PING"]);
                if (probe.Stdout.Contains("BUSY", StringComparison.Ordinal)) break;
                if (elapsed.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Owned Redis did not enter BUSY.");
                await Task.Delay(25);
            }
            // Construct after BUSY to prove no prior control handshake or reserved socket is needed.
            var node = client.Server.OnNode(endpoint);
            await (function ? node.FunctionKillAsync() : node.ScriptKillAsync()).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Func<Task> killed = async () => { using var result = await running; };
            await killed.Should().ThrowAsync<RespireServerException>().WithMessage("*killed*");
            await client.Strings.SetAsync("after-kill", "healthy");
            (await client.Strings.GetAsync<string>("after-kill")).Should().Be("healthy");
        }
        finally
        {
            // Cleanup targets only this fixture. The external socket also frees a failed client path.
            await container.ExecAsync(["redis-cli", "--no-auth-warning", "-a", password, function ? "FUNCTION" : "SCRIPT", "KILL"]);
            try { using var result = await running.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception error) when (error is RespireServerException or RespireConnectionException or TimeoutException) { }
        }
    }
}
