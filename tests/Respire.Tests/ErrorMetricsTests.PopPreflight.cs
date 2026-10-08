using System.Text;
using Respire.Internal;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public partial class ErrorMetricsTests
{
    [Test]
    [MatrixDataSource]
    public async Task MultiKeyPopConstructionHasOneFinalOwner(
        [Matrix("BLPOP", "BRPOP", "LMPOP", "BLMPOP", "ZMPOP", "BZMPOP", "BZPOPMIN", "BZPOPMAX",
            "generic-ZMPOP", "generic-BZMPOP", "generic-BZPOPMIN", "generic-BZPOPMAX", "LMOVEM", "BLMOVEM")] string route,
        [Matrix(false, true)] bool crossSlot, [Matrix(false, true)] bool prefix, [Matrix(false, true)] bool enabled)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = enabled ? RespireMetricGroups.Resiliency : RespireMetricGroups.None });
        await using var server = new FakeRespServer(8, FakeRespServer.OkReply);
        server.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{server.Port}\r\n")
            : "-WRONGTYPE injected pop failure\r\n"u8.ToArray();
        await using var root = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        var client = prefix ? root.WithKeyPrefix("tenant:") : root;
        RespireKey[] keys = crossSlot ? ["{first}:one", "{second}:two"] : ["{same}:one", "{same}:two"];
        using var capture = new Capture(throwOnMeasurement: true);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        async Task Execute()
        {
            switch (route)
            {
                case "BLPOP": await client.Lists.PopAsync(keys, TimeSpan.Zero, cancellationToken: guard.Token); break;
                case "BRPOP": await client.Lists.PopAsync(keys, TimeSpan.Zero, ListSide.Right, guard.Token); break;
                case "LMPOP": await client.Lists.PopManyAsync(keys, cancellationToken: guard.Token); break;
                case "BLMPOP": await client.Lists.PopManyAsync(keys, waitFor: TimeSpan.Zero, cancellationToken: guard.Token); break;
                case "ZMPOP": await client.SortedSets.PopManyAsync(keys, cancellationToken: guard.Token); break;
                case "BZMPOP": await client.SortedSets.PopManyAsync(keys, waitFor: TimeSpan.Zero, cancellationToken: guard.Token); break;
                case "BZPOPMIN": await client.SortedSets.PopAsync(keys, TimeSpan.Zero, cancellationToken: guard.Token); break;
                case "BZPOPMAX": await client.SortedSets.PopAsync(keys, TimeSpan.Zero, true, guard.Token); break;
                case "generic-ZMPOP": await client.SortedSets.PopManyAsync<int>(keys, cancellationToken: guard.Token); break;
                case "generic-BZMPOP": await client.SortedSets.PopManyAsync<int>(keys, waitFor: TimeSpan.Zero, cancellationToken: guard.Token); break;
                case "generic-BZPOPMIN": await client.SortedSets.PopAsync<int>(keys, TimeSpan.Zero, cancellationToken: guard.Token); break;
                case "generic-BZPOPMAX": await client.SortedSets.PopAsync<int>(keys, TimeSpan.Zero, true, guard.Token); break;
                case "LMOVEM": await client.Lists.MoveManyAsync(keys[0], keys[1], cancellationToken: guard.Token); break;
                case "BLMOVEM": await client.Lists.MoveManyAsync(keys[0], keys[1], waitFor: TimeSpan.Zero, cancellationToken: guard.Token); break;
            }
        }
        var error = await Assert.That(Execute).ThrowsExactly<RespireServerException>();
        await Assert.That(error!.Code).IsEqualTo(crossSlot ? "CROSSSLOT" : "WRONGTYPE");
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(enabled ? 1 : 0);
        if (enabled)
        {
            await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsFalse();
            await Assert.That(items[0].Tags["error.type"]).IsEqualTo(typeof(RespireServerException).FullName);
            await Assert.That(items[0].Tags["db.response.status_code"]).IsEqualTo(error.Code);
            await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        }
        var operation = route.Replace("generic-", "", StringComparison.Ordinal);
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith(operation + " ", StringComparison.Ordinal)))
            .IsEqualTo(crossSlot ? 0 : 1);
    }
}
