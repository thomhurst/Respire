using Respire.Testing.Containers;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ParallelLimiter<DockerHeavy>]
public class ReplicaRedirectIntegrationTests
{
    [Test]
    [Arguments(RespireProtocol.Resp2)]
    [Arguments(RespireProtocol.Resp3)]
    public async Task Valkey92ReplicaRedirectsKeylessWriteAndPreservesReplicaReads(RespireProtocol protocol)
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new()
        {
            Server = RespireContainerServer.Valkey, Topology = RespireContainerTopology.Sentinel,
            Image = "valkey/valkey:9.2.0-rc1-alpine",
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var primary = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [fixture.DataEndpoints[0]], Protocol = protocol, Connections = 1, AllowAdmin = true,
        }, deadline.Token);
        await primary.SetAsync("redirect:key", "value", cancellationToken: deadline.Token);
        await using var replica = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [fixture.DataEndpoints[1]], Protocol = protocol, Connections = 1,
            EnableReplicaRedirects = true, AllowAdmin = true,
        }, deadline.Token);
        // This primary-policy connection actually points at a replica. Both keyless data reads
        // and FLUSHDB must follow REDIRECT to the real primary.
        using (var key = await replica.ExecuteAsync(RespireCommands.Key.RANDOMKEY, deadline.Token))
            await Assert.That(key.AsString()).IsEqualTo("redirect:key");
        await using (var scan = replica.Keys.ScanAsync(cancellationToken: deadline.Token).GetAsyncEnumerator())
        {
            var error = await Assert.That(async () => _ = await scan.MoveNextAsync()).Throws<RespireServerException>();
            await Assert.That(error!.Code).IsEqualTo("REDIRECT");
        }
        await replica.Server.FlushDatabaseAsync(deadline.Token);
        await Assert.That(await primary.Server.DatabaseSizeAsync(deadline.Token)).IsEqualTo(0);

        // READONLY preserves explicit replica routing after capability negotiation.
        await using var reader = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [fixture.DataEndpoints[0]], ReplicaEndpoints = [fixture.DataEndpoints[1]],
            Protocol = protocol, Connections = 1, EnableReplicaRedirects = true, ReadFrom = RespireReadFrom.Replica,
        }, deadline.Token);
        await Assert.That(await reader.GetStringAsync("missing", deadline.Token)).IsNull();
        var keys = new List<string>();
        await foreach (var key in reader.Keys.ScanAsync(cancellationToken: deadline.Token)) keys.Add(key);
        // The replica may still expose the seeded key until it applies FLUSHDB.
        await Assert.That(keys.All(key => key == "redirect:key")).IsTrue();
    }
}
