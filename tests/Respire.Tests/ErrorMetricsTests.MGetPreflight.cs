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
    public async Task MGetPreflightHasOneFinalOwner(
        [Matrix(false, true)] bool generic, [Matrix(false, true)] bool cache,
        [Matrix(false, true)] bool prefix, [Matrix(false, true)] bool crossSlot,
        [Matrix(false, true)] bool enabled)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = enabled ? RespireMetricGroups.Resiliency : RespireMetricGroups.None });
        await using var server = new FakeRespServer(8, FakeRespServer.OkReply);
        server.ReplyOverride = (_, command) => command switch
        {
            "CLUSTER SLOTS" => Encoding.ASCII.GetBytes(
                $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{server.Port}\r\n"),
            "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
            _ when command.StartsWith("MGET ", StringComparison.Ordinal)
                => "*2\r\n$1\r\n1\r\n$1\r\n2\r\n"u8.ToArray(),
            _ => null,
        };
        await using var root = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            ClientSideCache = cache ? new() { CoalesceConcurrentMisses = false } : null,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        var client = prefix ? root.WithKeyPrefix("tenant:") : root;
        RespireKey[] keys = crossSlot ? ["{first}:one", "{second}:two"] : ["{same}:one", "{same}:two"];
        using var capture = new Capture(throwOnMeasurement: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        async Task Execute()
        {
            if (generic)
                await Assert.That((await client.Strings.GetManyAsync<int>(keys, deadline.Token)).SequenceEqual([1, 2])).IsTrue();
            else
                await Assert.That((await client.Strings.GetManyAsync(keys, deadline.Token)).SequenceEqual(["1", "2"])).IsTrue();
        }
        if (crossSlot)
        {
            await Assert.That(ClusterHash.GetSlot("{first}:one")).IsNotEqualTo(ClusterHash.GetSlot("{second}:two"));
            var error = await Assert.That(Execute).Throws<RespireServerException>();
            await Assert.That(error!.Code).IsEqualTo("CROSSSLOT");
            await Assert.That(error.Message).Contains("Keys in request don't hash to the same slot");
        }
        else await Execute();
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(enabled && crossSlot ? 1 : 0);
        if (enabled && crossSlot)
        {
            await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsFalse();
            await Assert.That(items[0].Tags["error.type"]).IsEqualTo(typeof(RespireServerException).FullName);
            await Assert.That(items[0].Tags["db.response.status_code"]).IsEqualTo("CROSSSLOT");
            await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        }
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("MGET ", StringComparison.Ordinal)))
            .IsEqualTo(crossSlot ? 0 : 1);
    }
}
