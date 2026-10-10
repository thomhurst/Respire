using Respire.Internal;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class StreamWorkerCapabilityIntegrationTests(RedisTestContainer redis)
{
    [Test]
    [Arguments("ack-delete", 2)]
    [Arguments("nack", 8)]
    public async Task DisabledNativeCommandUsesCompatibleCompletion(string completion, int minimumMinor)
    {
        await using var admin = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString));
        var info = await admin.Server.InfoAsync("server");
        var line = info.Split('\n').Single(value => value.StartsWith("redis_version:"));
        var version = Version.Parse(line["redis_version:".Length..].Trim().Split('-')[0]);
        if (version < new Version(8, minimumMinor)) return;
        var nack = completion == "nack";
        var image = Environment.GetEnvironmentVariable("RESPIRE_TEST_REDIS_IMAGE") ?? "redis:7.0.15";
        await using var disabled = new Testcontainers.Redis.RedisBuilder(image)
            .WithCommand(["redis-server", "--rename-command", nack ? "XNACK" : "XACKDEL", ""])
            .Build();
        await disabled.StartAsync();
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(disabled.GetConnectionString()));
        await client.Streams.AddAsync("disabled", new StreamAddOptions { Id = "1-0" }, ("f", "v"));
        await client.Streams.CreateGroupAsync("disabled", "g", RespireStreamId.Beginning);
        await client.Streams.ReadGroupOnceAsync("disabled", "g", "owner");
        await client.Scripts.ExecuteIntegerAsync(nack ? StreamWorkerScripts.Nack : StreamWorkerScripts.AckAndDelete,
            ["disabled"], nack ? ["g", "owner", "1-0", 1, 0] : ["g", "owner", "1-0", 1]);
        await Assert.That((await client.Streams.PendingSummaryAsync("disabled", "g")).Count).IsEqualTo(nack ? 1 : 0);
        await Assert.That(await client.Streams.CountAsync("disabled")).IsEqualTo(1);
        if (nack)
        {
            var pending = (await client.Streams.PendingAsync("disabled", "g")).Single();
            await Assert.That(pending.Consumer).IsEqualTo("owner");
            await Assert.That(pending.DeliveryCount).IsEqualTo(1);
        }
    }

    [Test]
    public async Task DiscoveryIsRefreshedAfterReconnectAndPermissionChanges()
    {
        await using var admin = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString) with { AllowAdmin = true });
        var info = await admin.Server.InfoAsync("server");
        var line = info.Split('\n').Single(value => value.StartsWith("redis_version:"));
        var version = Version.Parse(line["redis_version:".Length..].Trim().Split('-')[0]);
        if (version < new Version(8, 2)) return;
        var user = "worker_" + Guid.NewGuid().ToString("N");
        try
        {
            await admin.Server.AclSetUserAsync(user, ["on", ">password", "~*", "+@all"]);
            await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString)
                with { Username = user, Password = "password", Connections = 1 });
            var view = client.WithKeyPrefix("tenant:");
            await view.Streams.CreateGroupAsync("reconnect", "g", RespireStreamId.Beginning);
            await view.Streams.AddAsync("reconnect", new StreamAddOptions { Id = "1-0" }, ("f", "v"));
            await view.Streams.ReadGroupOnceAsync("reconnect", "g", "owner");
            await view.Scripts.ExecuteIntegerAsync(StreamWorkerScripts.AckAndDelete, ["reconnect"], ["g", "owner", "1-0", 1]);
            await Assert.That(await view.Streams.CountAsync("reconnect")).IsEqualTo(0);
            await view.Streams.AddAsync("reconnect", new StreamAddOptions { Id = "2-0" }, ("f", "v"));
            await view.Streams.ReadGroupOnceAsync("reconnect", "g", "owner");
            var identity = await client.Server.GetClientConnectionAsync();
            var connection = client.Core.Multiplexer.GetConnection();
            await admin.Server.AclSetUserAsync(user, ["-info"]);
            await admin.Server.KillClientAsync(identity.Id);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await connection.Closed.WaitAsync(deadline.Token);
            await client.Core.Multiplexer.GetHealthyConnectionAsync(deadline.Token);
            await view.Scripts.ExecuteIntegerAsync(StreamWorkerScripts.AckAndDelete, ["reconnect"], ["g", "owner", "2-0", 1]);
            await Assert.That(await view.Streams.CountAsync("reconnect")).IsEqualTo(1);
            await Assert.That((await client.Server.GetClientConnectionAsync()).Id).IsNotEqualTo(identity.Id);
            await admin.Server.AclSetUserAsync(user, ["+info"]);
            await view.Streams.AddAsync("reconnect", new StreamAddOptions { Id = "3-0" }, ("f", "v"));
            await view.Streams.ReadGroupOnceAsync("reconnect", "g", "owner");
            await view.Scripts.ExecuteIntegerAsync(StreamWorkerScripts.AckAndDelete, ["reconnect"], ["g", "owner", "3-0", 1]);
            await Assert.That(await view.Streams.CountAsync("reconnect")).IsEqualTo(1);
        }
        finally { await admin.Server.AclDeleteUsersAsync([user]); }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ServingVersionUsesNativeCommandsOrCompatibleFallback(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString)
            with { Protocol = (RespProtocol)protocol });
        var info = await client.Server.InfoAsync("server");
        var line = info.Split('\n').Single(value => value.StartsWith("redis_version:"));
        var version = Version.Parse(line["redis_version:".Length..].Trim().Split('-')[0]);
        var minor = version.Major < 8 ? 0 : version.Major > 8 ? int.MaxValue : version.Minor;
        await StreamWorkerCapabilityScriptTests.CapabilityScenarioAsync(client, null, minor);
        await StreamWorkerCapabilityScriptTests.ClaimMixScenarioAsync(client, minor);
        await StreamWorkerCapabilityScriptTests.DeletedPendingScenarioAsync(client);
        if (minor >= 4) await StreamWorkerCapabilityScriptTests.CleanupCursorScenarioAsync(client);
        if (minor >= 8) await StreamWorkerCapabilityScriptTests.ReleasedPendingScenarioAsync(client);
    }

    [Test]
    [Arguments("nack", 8)]
    [Arguments("ack-delete", 2)]
    public async Task NativeCommandPermissionFailureDoesNotFallback(string completion, int minimumMinor)
    {
        await using var admin = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString) with { AllowAdmin = true });
        var info = await admin.Server.InfoAsync("server");
        var line = info.Split('\n').Single(value => value.StartsWith("redis_version:"));
        var version = Version.Parse(line["redis_version:".Length..].Trim().Split('-')[0]);
        if (version < new Version(8, minimumMinor)) return; // This version has no native command to deny.
        var nack = completion == "nack";
        var user = "worker_" + Guid.NewGuid().ToString("N");
        try
        {
            await admin.Server.AclSetUserAsync(user, ["on", ">password", "~*", "+@all", nack ? "-xnack" : "-xackdel"]);
            await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString)
                with { Username = user, Password = "password" });
            await client.Streams.AddAsync("denied", new StreamAddOptions { Id = "1-0" }, ("f", "v"));
            await client.Streams.CreateGroupAsync("denied", "g", RespireStreamId.Beginning);
            await client.Streams.ReadGroupOnceAsync("denied", "g", "owner");
            await Assert.That(async () => await client.Scripts.ExecuteIntegerAsync(
                nack ? StreamWorkerScripts.Nack : StreamWorkerScripts.AckAndDelete, ["denied"],
                nack ? ["g", "owner", "1-0", 1, 0] : ["g", "owner", "1-0", 1])).Throws<RespireServerException>();
            var pending = (await client.Streams.PendingAsync("denied", "g", RespireStreamId.Min, RespireStreamId.Max, 1)).Single();
            await Assert.That(pending.Consumer).IsEqualTo("owner");
            await Assert.That(pending.DeliveryCount).IsEqualTo(1);
        }
        finally { await admin.Server.AclDeleteUsersAsync([user]); }
    }

    [Test]
    public async Task DiscoveryPermissionFailureFallsBackWithoutDeletingBody()
    {
        await using var admin = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString) with { AllowAdmin = true });
        var user = "worker_" + Guid.NewGuid().ToString("N");
        try
        {
            await admin.Server.AclSetUserAsync(user, ["on", ">password", "~*", "+@all", "-info"]);
            await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString)
                with { Username = user, Password = "password" });
            await client.Streams.AddAsync("fallback", new StreamAddOptions { Id = "1-0" }, ("f", "v"));
            await client.Streams.CreateGroupAsync("fallback", "g", RespireStreamId.Beginning);
            await client.Streams.ReadGroupOnceAsync("fallback", "g", "old");
            using (var reply = await client.Scripts.ExecuteAsync(StreamWorkerScripts.CapabilityClaim, ["fallback"],
                ["g", "new", 0, "0-0", 1]))
                await Assert.That(reply[1][0][2].AsString()).IsEqualTo("2");
            await client.Scripts.ExecuteIntegerAsync(StreamWorkerScripts.AckAndDelete, ["fallback"], ["g", "new", "1-0", 2]);
            await Assert.That((await client.Streams.RangeAsync("fallback")).Length).IsEqualTo(1);
        }
        finally { await admin.Server.AclDeleteUsersAsync([user]); }
    }
}
