using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<SecuredRedisTestContainer>(Shared = SharedType.PerTestSession)]
public class AuthenticationTests(SecuredRedisTestContainer fixture)
{
    [Test]
    public async Task CorrectPassword_Resp2_CommandsWork()
    {
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint(fixture.Host, fixture.Port) },
            Password = SecuredRedisTestContainer.Password,
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
            Password = SecuredRedisTestContainer.Password,
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
            Password = SecuredRedisTestContainer.Password,
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
