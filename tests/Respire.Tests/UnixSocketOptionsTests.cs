using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class UnixSocketOptionsTests
{
    [Test]
    [Arguments("unix:///tmp/redis.sock")]
    [Arguments("redis+unix:///tmp/redis.sock")]
    [Arguments("!/tmp/redis.sock")]
    public async Task ParsesSocketEndpoint(string value)
    {
        var endpoint = RespireEndpoint.Parse(value);
        await Assert.That(endpoint.IsUnixSocket).IsTrue();
        await Assert.That(endpoint.Host).IsEqualTo("/tmp/redis.sock");
        await Assert.That(endpoint.Port).IsEqualTo(0);
        await Assert.That(RespireEndpoint.Parse(endpoint.ToString())).IsEqualTo(endpoint);
        await Assert.That(RespireOptions.Parse(value).Endpoints.Single()).IsEqualTo(endpoint);
    }

    [Test]
    [Arguments("unix")]
    [Arguments("redis+unix")]
    public async Task UriDecodesSocketPathAndKeepsDatabaseInQuery(string scheme)
    {
        var options = RespireOptions.Parse($"{scheme}:///tmp/Redis%20%23%3F.sock?db=4&protocol=3&connections=2");
        await Assert.That(options.Endpoints.Single().Host).IsEqualTo("/tmp/Redis #?.sock");
        await Assert.That(options.Database).IsEqualTo(4);
        await Assert.That(options.Protocol).IsEqualTo(RespProtocol.Resp3);
        await Assert.That(options.Connections).IsEqualTo(2);
    }

    [Test]
    public async Task StackExchangeSyntaxKeepsAuthenticationAndTimeouts()
    {
        var options = RespireOptions.Parse("!/tmp/redis.sock,user=app,password=secret,defaultDatabase=2,connectTimeout=1234");
        await Assert.That(options.Endpoints.Single()).IsEqualTo(RespireEndpoint.UnixSocket("/tmp/redis.sock"));
        await Assert.That(options.Username).IsEqualTo("app");
        await Assert.That(options.Password).IsEqualTo("secret");
        await Assert.That(options.Database).IsEqualTo(2);
        await Assert.That(options.ConnectTimeout).IsEqualTo(TimeSpan.FromMilliseconds(1234));
    }

    [Test]
    public async Task SocketIdentityIsCaseSensitiveWhileDnsIdentityIsNot()
    {
        var comparer = RespireEndpointComparer.Instance;
        var lower = RespireEndpoint.UnixSocket("/tmp/redis.sock");
        var upper = RespireEndpoint.UnixSocket("/tmp/Redis.sock");
        await Assert.That(comparer.Equals(lower, upper)).IsFalse();
        await Assert.That(comparer.Equals(lower, RespireEndpoint.Parse(lower.ToString()))).IsTrue();
        await Assert.That(comparer.GetHashCode(lower)).IsEqualTo(comparer.GetHashCode(RespireEndpoint.Parse(lower.ToString())));
        await Assert.That(comparer.Equals(new("HOST"), new("host"))).IsTrue();
    }

    [Test]
    [Arguments("unix://remote/tmp/redis.sock")]
    [Arguments("unix:///tmp/redis.sock#fragment")]
    [Arguments("unix:///")]
    [Arguments("redis+unix:///tmp/a%00b")]
    public async Task RejectsInvalidSocketUris(string value)
        => await Assert.That(() => RespireOptions.Parse(value)).Throws<ArgumentException>();

    [Test]
    public async Task RejectsEmptyAndEmbeddedNullPaths()
    {
        await Assert.That(() => RespireEndpoint.UnixSocket("")).Throws<ArgumentException>();
        await Assert.That(() => RespireEndpoint.UnixSocket("/tmp/a\0b")).Throws<ArgumentException>();
    }

    [Test]
    public async Task RejectsTcpDiscoveryAndTlsForSocketEndpoints()
    {
        var options = new RespireOptions { Endpoints = [RespireEndpoint.UnixSocket("/tmp/redis.sock")] };
        await Assert.That(() => RespireClient.Create(options with { UseCluster = true }))
            .Throws<RespireConfigurationException>();
        await Assert.That(() => RespireClient.Create(options with { SentinelPrimaryName = "primary" }))
            .Throws<RespireConfigurationException>();
        await Assert.That(() => RespireClient.Create(options with { UseTls = true }))
            .Throws<RespireConfigurationException>();
        await Assert.That(() => RespireClient.Create(options with { MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled }))
            .Throws<RespireConfigurationException>();
    }

    [Test]
    public async Task StandaloneReplicaEndpointsCanUseEitherTransport()
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [RespireEndpoint.UnixSocket("/tmp/primary.sock")],
            ReplicaEndpoints = [RespireEndpoint.UnixSocket("/tmp/replica.sock"), new("localhost", 6380)],
            ReadFrom = RespireReadFrom.Replica,
            TcpKeepAliveTime = TimeSpan.FromSeconds(10),
        });
        await Assert.That(client).IsNotNull();
    }
}
