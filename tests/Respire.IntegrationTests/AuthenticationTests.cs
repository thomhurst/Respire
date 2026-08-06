using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
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
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint(fixture.Host, fixture.Port) },
            Password = SecuredRedisFixture.Password,
            Connections = 1,
        });

        var roundTrip = await client.PingAsync();
        roundTrip.Should().BePositive();

        (await client.SetAsync("auth:key", "auth-value")).Should().BeTrue();
        (await client.GetStringAsync("auth:key")).Should().Be("auth-value");
    }

    [Test]
    public async Task CorrectPassword_Resp3Hello_CommandsWork()
    {
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint(fixture.Host, fixture.Port) },
            Password = SecuredRedisFixture.Password,
            Protocol = RespProtocol.Resp3,
            Connections = 1,
        });

        (await client.SetAsync("auth:resp3", "resp3-value")).Should().BeTrue();
        (await client.GetStringAsync("auth:resp3")).Should().Be("resp3-value");
    }

    [Test]
    public async Task ClientName_AcceptedDuringHandshake()
    {
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint(fixture.Host, fixture.Port) },
            Password = SecuredRedisFixture.Password,
            ClientName = "respire-integration",
            Connections = 1,
        });

        (await client.PingAsync()).Should().BePositive();
    }

    [Test]
    public async Task WrongPassword_ConnectThrows()
    {
        var act = async () => await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint(fixture.Host, fixture.Port) },
            Password = "wrong-password",
            Connections = 1,
        });

        await act.Should().ThrowAsync<RespireConnectionException>();
    }

    [Test]
    public async Task NoPassword_FirstCommandFailsWithNoAuth()
    {
        // A RESP2 connection with no password sends no handshake commands, so the connect itself
        // succeeds; the NOAUTH error surfaces on the first command.
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint(fixture.Host, fixture.Port) },
            Connections = 1,
        });

        var act = async () => await client.PingAsync();

        (await act.Should().ThrowAsync<RespireServerException>())
            .Which.Code.Should().Be("NOAUTH");
    }
}
