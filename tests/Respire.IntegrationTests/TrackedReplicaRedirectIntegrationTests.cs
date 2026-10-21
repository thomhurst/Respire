using Respire.Tests.Networking;
using Testcontainers.Redis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ParallelLimiter<DockerHeavy>]
public class TrackedReplicaRedirectIntegrationTests
{
    [Test]
    [Arguments("cancel", false)]
    [Arguments("cancel", true)]
    [Arguments("timeout", false)]
    [Arguments("timeout", true)]
    [Arguments("lost-reply", false)]
    [Arguments("lost-reply", true)]
    public async Task UncertainRedirectCannotMutateAfterCleanup(string failure, bool cache)
    {
        // PAUSE affects the whole server, so this test owns a separate Redis container.
        await using var server = await ContainerStartRetry.StartAsync(() => new RedisBuilder("redis:7.4.11-alpine").Build(), [6379]);
        var port = server.GetMappedPublicPort(6379);
        await using var admin = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(server.Hostname, port)], Protocol = RespireProtocol.Resp2, Connections = 1, AllowAdmin = true,
        });
        var script = RespireScript.Create("return redis.call('SET', KEYS[1], 'latent')");
        await admin.Scripts.LoadAsync(script);
        await using var fixture = new ReplicaRedirectTestFixture(sentinel: false, cache, targetPort: port);
        await using var client = await RespireClient.ConnectAsync(fixture.Options with
        {
            CommandTimeout = failure == "timeout" ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(10),
        });
        await client.EnsureReliableCorrectionOrderingAsync();
        var pools = client.Core.OwnedPools.Count;
        using var caller = new CancellationTokenSource();
        var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        using (await admin.ExecuteAsync("CLIENT", ["PAUSE", 30000, "WRITE"])) { }
        try
        {
            var execution = await client.StartTrackedScriptExecutionAsync(script, ["key"], [], caller.Token,
                requireReliableCorrectionOrdering: true,
                onSerialized: _ => { if (Interlocked.Increment(ref attempts) == 2) submitted.TrySetResult(); });
            var response = client.ExecuteWithCorrectionAsync(execution, RespireClient.CorrectionOrdering.FenceFirst).AsTask();
            await submitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(execution.ConnectionIdentity.Endpoint.Port).IsEqualTo(port);
            if (failure == "cancel") caller.Cancel();
            if (failure == "lost-reply") await execution.Connection.DisposeAsync();
            Exception? observed = null;
            try { using var reply = await response; }
            catch (Exception error) { observed = error; }
            await Assert.That(observed).IsNotNull();
            if (failure == "cancel") await Assert.That(observed).IsAssignableTo<OperationCanceledException>();
            if (failure == "timeout") await Assert.That(observed).IsAssignableTo<RespireTimeoutException>();
            using var clients = await admin.ExecuteAsync("CLIENT", ["LIST", "ID", execution.ConnectionIdentity.ServerClientId]);
            await Assert.That(clients.AsString()).IsEqualTo("");
            await Assert.That(client.Core.OwnedPools.Count).IsEqualTo(pools);
        }
        finally { using var unpause = await admin.ExecuteAsync("CLIENT", ["UNPAUSE"]); }

        // The server resumes processing buffered writes. The fenced target can never run its SET.
        await Assert.That(await admin.GetStringAsync("key")).IsNull();
    }
}
