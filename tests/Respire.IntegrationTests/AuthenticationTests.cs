using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Respire;
using Respire.FastClient;
using Respire.Networking;
using TUnit.Core;
using TUnit.Core.Interfaces;

namespace Respire.IntegrationTests;

/// <summary>Redis container started with --requirepass for authentication tests.</summary>
public class SecuredRedisFixture : IAsyncInitializer, IAsyncDisposable
{
    public const string Password = "integration-pass";

    private readonly IContainer _container = new ContainerBuilder()
        .WithImage("redis:7-alpine")
        .WithPortBinding(6379, true)
        .WithCommand("redis-server", "--requirepass", Password)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(6379))
        .Build();

    public string Host => _container.Hostname;
    public int Port => _container.GetMappedPublicPort(6379);

    public Task InitializeAsync() => _container.StartAsync();

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

[ClassDataSource<SecuredRedisFixture>(Shared = SharedType.PerClass)]
[NotInParallel("redis-integration")]
public class AuthenticationTests(SecuredRedisFixture fixture)
{
    [Test]
    public async Task CorrectPassword_Resp2_CommandsWork()
    {
        await using var client = await RespireClient.CreateAsync(
            fixture.Host, fixture.Port, connectionCount: 1,
            options: new RespireConnectionOptions { Password = SecuredRedisFixture.Password });

        var pong = await client.PingAsync();
        pong.Should().Be("PONG");

        await client.SetAsync("auth:key", "auth-value");
        var value = await client.GetAsync("auth:key");
        value.AsString().Should().Be("auth-value");
        value.Dispose();
    }

    [Test]
    public async Task CorrectPassword_Resp3Hello_CommandsWork()
    {
        await using var client = await RespireClient.CreateAsync(
            fixture.Host, fixture.Port, connectionCount: 1,
            options: new RespireConnectionOptions { Password = SecuredRedisFixture.Password, UseResp3 = true });

        await client.SetAsync("auth:resp3", "resp3-value");
        var value = await client.GetAsync("auth:resp3");
        value.AsString().Should().Be("resp3-value");
        value.Dispose();
    }

    [Test]
    public async Task ClientName_AcceptedDuringHandshake()
    {
        await using var client = await RespireClient.CreateAsync(
            fixture.Host, fixture.Port, connectionCount: 1,
            options: new RespireConnectionOptions
            {
                Password = SecuredRedisFixture.Password,
                ClientName = "respire-integration",
            });

        (await client.PingAsync()).Should().Be("PONG");
    }

    [Test]
    public async Task WrongPassword_ConnectThrows()
    {
        var act = async () => await RespireClient.CreateAsync(
            fixture.Host, fixture.Port, connectionCount: 1,
            options: new RespireConnectionOptions { Password = "wrong-password" });

        await act.Should().ThrowAsync<RespireConnectionException>();
    }

    [Test]
    public async Task NoPassword_FirstCommandFailsWithNoAuth()
    {
        await using var client = await RespireClient.CreateAsync(
            fixture.Host, fixture.Port, connectionCount: 1);

        var act = async () => await client.PingAsync();

        (await act.Should().ThrowAsync<RespireServerException>())
            .Which.Message.Should().Contain("NOAUTH");
    }
}
