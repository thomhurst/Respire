using DotNet.Testcontainers.Builders;
using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

public class SetManyIfNotExistsIntegrationTests
{
    [Test]
    [Arguments("redis:8.4-alpine", 2)]
    [Arguments("redis:8.4-alpine", 3)]
    [Arguments("valkey/valkey:8.1-alpine", 2)]
    [Arguments("valkey/valkey:8.1-alpine", 3)]
    public async Task AtomicWrites_WorkAcrossServersProtocolsAndDeferredSurfaces(string image, int protocol)
    {
        await using var server = new ContainerBuilder(image).WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build();
        await server.StartAsync();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(server.Hostname, server.GetMappedPublicPort(6379))], Connections = 1,
            Protocol = protocol == 2 ? RespProtocol.Resp2 : RespProtocol.Resp3,
        });
        foreach (var mode in new[] { "immediate", "batch", "transaction" })
        {
            var view = client.WithKeyPrefix($"{mode}:");
            (await Run(view, mode, ("first", "one"), ("second", "two"))).Should().BeTrue();
            (await view.Strings.GetManyAsync("first", "second")).Should().Equal("one", "two");
            (await view.Keys.ExpiryAsync("first")).HasExpiry.Should().BeFalse();
            (await Run(view, mode, ("missing", "new"), ("second", "replacement"))).Should().BeFalse();
            (await view.Keys.ExistsAsync("missing")).Should().BeFalse();
            (await view.GetStringAsync("second")).Should().Be("two");

            await view.Lists.RightPushAsync("list", "item");
            (await Run(view, mode, ("missing", "new"), ("list", "replacement"))).Should().BeFalse();
            (await view.Keys.ExistsAsync("missing")).Should().BeFalse();
            (await view.Lists.RangeAsync("list")).Should().Equal("item");

            (await Run(view, mode, ("duplicate", "first"), ("duplicate", "last"))).Should().BeTrue();
            (await view.GetStringAsync("duplicate")).Should().Be("last");
            byte[] key = [0, 255, 13, 10];
            byte[] value = [255, 0, 128, 13, 10];
            (await Run(view, mode, (key, value), (RespireKey.Empty, ""))).Should().BeTrue();
            (await view.GetAsync<byte[]>(key)).Should().Equal(value);
            (await view.GetStringAsync(RespireKey.Empty)).Should().BeEmpty();
            (await client.Keys.ExistsAsync("first")).Should().BeFalse();
            (await client.Keys.ExistsAsync(key)).Should().BeFalse();
        }
    }

    private static async Task<bool> Run(IRespireClient client, string mode,
        params (RespireKey Key, RespireValue Value)[] pairs)
    {
        if (mode == "immediate") return await client.Strings.SetManyIfNotExistsAsync(pairs, CancellationToken.None);
        if (mode == "batch")
        {
            using var batch = client.CreateBatch();
            var pending = batch.Strings.SetManyIfNotExists(pairs);
            await batch.ExecuteAsync();
            return pending.Result;
        }
        await using var transaction = client.CreateTransaction();
        var result = transaction.Strings.SetManyIfNotExists(pairs);
        await transaction.CommitAsync();
        return result.Result;
    }
}
