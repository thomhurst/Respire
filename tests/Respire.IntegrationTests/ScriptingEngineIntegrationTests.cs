using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ParallelLimiter<DockerHeavy>]
public class ScriptingEngineIntegrationTests
{
    private const string Source = "#!lua name=engine_test\nredis.register_function('answer', function() return 42 end)";

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ModernAbsentEngineAndLegacyAclControls(int protocol)
    {
        await using var modern = CreateServer("valkey/valkey:9.1.0-alpine");
        await using var legacy = CreateServer("valkey/valkey:9.0-alpine");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = deadline.Token;
        await modern.StartAsync(token);
        await legacy.StartAsync(token);
        await using var client = await ConnectAsync(modern, protocol, restricted: false, token);
        await using var older = await ConnectAsync(legacy, protocol, restricted: false, token);
        var script = RespireScript.Create("return 42");
        (await client.Scripts.ExecuteIntegerAsync(script, cancellationToken: token)).Should().Be(42);
        (await older.Scripts.ExecuteIntegerAsync(script, cancellationToken: token)).Should().Be(42);
        using (var info = await client.ExecuteAsync("INFO", ["scriptingengines"], cancellationToken: token))
            info.AsString().Should().Contain("engines_count:1").And.Contain("name=LUA");
        using (var info = await older.ExecuteAsync("INFO", ["scriptingengines"], cancellationToken: token))
            info.AsString().Should().BeEmpty();

        foreach (var admin in new[] { client, older })
            using (await admin.ExecuteAsync("ACL SETUSER", ["engine_test", "on", ">fixture-password", "~*", "+@all", "-info"], cancellationToken: token)) { }
        await using var restricted = await ConnectAsync(modern, protocol, restricted: true, token);
        await using var restrictedOlder = await ConnectAsync(legacy, protocol, restricted: true, token);
        foreach (var user in new[] { restricted, restrictedOlder })
        {
            Func<Task> info = async () => { using var reply = await user.ExecuteAsync("INFO", ["scriptingengines"], cancellationToken: token); };
            (await info.Should().ThrowExactlyAsync<RespireServerException>()).Which.Code.Should().Be("NOPERM");
            (await user.Scripts.ExecuteIntegerAsync(script, cancellationToken: token)).Should().Be(42);
        }

        // The released image statically loads Lua at startup; MODULE UNLOAD is supported.
        // Unloading clears both cached scripts and registered function libraries.
        var library = RespireFunctionLibrary.Create(Source);
        (await client.Functions.ExecuteIntegerAsync(library.Function("answer"), cancellationToken: token)).Should().Be(42);
        var dump = await client.Functions.DumpAsync(token);
        using (await client.ExecuteAsync("MODULE UNLOAD", ["lua"], cancellationToken: token)) { }
        using (var info = await client.ExecuteAsync("INFO", ["scriptingengines"], cancellationToken: token))
            info.AsString().Should().Contain("engines_count:0");

        Func<Task> evaluate = async () => { await client.Scripts.ExecuteIntegerAsync(script, cancellationToken: token); };
        (await evaluate.Should().ThrowExactlyAsync<RespireScriptingEngineUnavailableException>()).Which.Engine.Should().Be("lua");
        Func<Task> load = async () => { await client.Scripts.LoadAsync(script, token); };
        await load.Should().ThrowExactlyAsync<RespireScriptingEngineUnavailableException>();
        Func<Task> function = async () => { await client.Functions.ExecuteIntegerAsync(library.Function("answer"), cancellationToken: token); };
        await function.Should().ThrowExactlyAsync<RespireScriptingEngineUnavailableException>();
        Func<Task> restore = async () => { await client.Functions.RestoreAsync(dump, cancellationToken: token); };
        await restore.Should().ThrowExactlyAsync<RespireScriptingEngineUnavailableException>();
        Func<Task> raw = async () => { using var reply = await client.ExecuteAsync("EVAL", ["return 42", 0], cancellationToken: token); };
        await raw.Should().ThrowExactlyAsync<RespireScriptingEngineUnavailableException>();

        // Without INFO permission, preserve the actual script error, not the diagnostic's NOPERM.
        Func<Task> denied = async () => { await restricted.Scripts.ExecuteIntegerAsync(script, cancellationToken: token); };
        (await denied.Should().ThrowExactlyAsync<RespireServerException>()).Which.Message
            .Should().Be("ERR Could not find scripting engine 'lua'");
        (await restrictedOlder.Scripts.ExecuteIntegerAsync(script, cancellationToken: token)).Should().Be(42);
    }

    private static IContainer CreateServer(string image) => new ContainerBuilder(image)
        .WithEntrypoint("valkey-server")
        .WithCommand("--enable-module-command", "yes", "--save", "", "--appendonly", "no")
        .WithPortBinding(6379, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379))
        .Build();

    private static ValueTask<RespireClient> ConnectAsync(IContainer server, int protocol, bool restricted, CancellationToken token)
        => RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(server.Hostname, server.GetMappedPublicPort(6379))],
            Protocol = protocol == 2 ? RespProtocol.Resp2 : RespProtocol.Resp3,
            Connections = 1, AllowAdmin = true,
            Username = restricted ? "engine_test" : null,
            Password = restricted ? "fixture-password" : null,
            ConnectTimeout = TimeSpan.FromSeconds(5), CommandTimeout = TimeSpan.FromSeconds(5),
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
        }, token);
}
