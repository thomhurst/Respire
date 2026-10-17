using System.Globalization;
using Respire.Protocol;
using Testcontainers.Redis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ParallelLimiter<DockerHeavy>]
public class BlockingCancellationIntegrationTests
{
    [Test]
    [Arguments(RespireProtocol.Resp2)]
    [Arguments(RespireProtocol.Resp3)]
    public async Task ThousandCancelledPopsKeepOriginalRedisConnection(RespireProtocol protocol)
    {
        await using var redis = new RedisBuilder("redis:7.2-alpine").Build();
        await redis.StartAsync();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = protocol, Connections = 1, AllowAdmin = true,
            Endpoints = [new(redis.Hostname, redis.GetMappedPublicPort(6379))],
            DedicatedPool = new() { UnblockOnCancel = true, MaximumSize = 1 },
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var pool = await client.Core.GetDedicatedPoolAsync(deadline.Token, observation: default);
        var original = await pool.RentAsync(deadline.Token);
        var id = await original.EnsureServerClientIdAsync(deadline.Token);
        pool.Return(original);
        for (var index = 0; index < 1000; index++)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var pending = client.Lists.LeftPopAsync("jobs", waitFor: null, cancellation.Token).AsTask();
            while (true)
            {
                using var list = await client.ExecuteAsync("CLIENT", ["LIST", "ID", id.ToString(CultureInfo.InvariantCulture)],
                    cancellationToken: deadline.Token);
                if (list.AsString()!.Contains("flags=b", StringComparison.Ordinal)) break;
                await Task.Delay(1, deadline.Token);
            }
            cancellation.Cancel();
            await Assert.That(async () => await pending.WaitAsync(deadline.Token)).Throws<OperationCanceledException>();
            var reused = await pool.RentAsync(deadline.Token);
            await Assert.That(ReferenceEquals(original, reused)).IsTrue();
            pool.Return(reused);
        }
    }
}
