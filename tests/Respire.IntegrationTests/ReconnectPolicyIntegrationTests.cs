using System.Collections.Concurrent;
using FluentAssertions;
using Testcontainers.Redis;
using TUnit.Core;

namespace Respire.IntegrationTests;

public class ReconnectPolicyIntegrationTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task KilledCommandConnectionRecoversAndResetsPolicy(int protocol)
    {
        await using var container = new RedisBuilder("redis:7.2.4-alpine").Build();
        await container.StartAsync();
        var options = new RespireOptions
        {
            Endpoints = { new RespireEndpoint(container.Hostname, container.GetMappedPublicPort(6379)) },
            Connections = 1, Protocol = (RespProtocol)protocol, AllowAdmin = true,
            ReconnectPolicy = new() { InitialDelay = TimeSpan.FromMilliseconds(25), JitterRatio = 0, MaxAttempts = 1 },
        };
        await using var client = await RespireClient.ConnectAsync(options);
        await using var observer = await RespireClient.ConnectAsync(options with { ReconnectPolicy = null });
        var attempts = new ConcurrentQueue<RespireConnectionStateChange>();
        client.ConnectionStateChanged += change =>
        {
            if (change.NextReconnectDelay is not null) attempts.Enqueue(change);
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        for (var index = 0; index < 2; index++)
        {
            var identity = await client.Server.GetClientConnectionAsync(deadline.Token);
            var original = client.Core.Multiplexer.GetConnection();
            (await observer.Server.KillClientAsync(identity.Id, cancellationToken: deadline.Token)).Should().BeTrue();
            await original.Closed.WaitAsync(deadline.Token);
            await client.Core.Multiplexer.GetHealthyConnectionAsync(deadline.Token);
            await client.SetAsync("reconnect:verified", index, cancellationToken: deadline.Token);
            (await client.GetAsync<int>("reconnect:verified", deadline.Token)).Should().Be(index);
            (await client.Server.GetClientConnectionAsync(deadline.Token)).Id.Should().NotBe(identity.Id);
        }
        attempts.Select(change => change.ReconnectAttempt).Should().Equal(1, 1);
        attempts.Should().OnlyContain(change => change.ConnectionSlot == 0
            && change.NextReconnectDelay == TimeSpan.FromMilliseconds(25));
    }
}
