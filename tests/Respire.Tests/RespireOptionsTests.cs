using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class RespireOptionsTests
{
    [Test]
    public async Task Defaults_AreExplicitAndBounded()
    {
        var options = new RespireOptions();

        await Assert.That(options.Endpoints).IsEmpty();
        await Assert.That(options.Connections).IsEqualTo(1);
        await Assert.That(options.CommandTimeout).IsEqualTo(TimeSpan.FromSeconds(10));
        await Assert.That(options.Protocol).IsEqualTo(RespProtocol.Resp2);
    }

    [Test]
    public async Task CompatibilityAliases_AreAbsent()
    {
        var properties = typeof(RespireOptions).GetProperties().Select(static property => property.Name);

        await Assert.That(properties).DoesNotContain("Cluster");
        await Assert.That(properties).DoesNotContain("ServiceName");
        await Assert.That(properties).DoesNotContain("ResponseTimeout");
    }

    [Test]
    [Arguments("redis://localhost?connections=0")]
    [Arguments("redis://localhost?connections=-1")]
    public async Task ConnectionString_RejectsNonPositiveConnectionCount(string connectionString)
    {
        await Assert.That(() => RespireOptions.Parse(connectionString))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Create_RejectsEmptyEndpointsAndNonPositiveConnections()
    {
        await Assert.That(() => RespireClient.Create(new RespireOptions { Endpoints = [] }))
            .ThrowsExactly<RespireConfigurationException>();
        await Assert.That(() => RespireClient.Create(new RespireOptions
            {
                Connections = 0,
                Endpoints = { new RespireEndpoint("localhost") },
            }))
            .ThrowsExactly<RespireConfigurationException>();
    }

    [Test]
    [Arguments(nameof(RespireOptions.Database))]
    [Arguments(nameof(RespireOptions.ConnectTimeout))]
    [Arguments(nameof(RespireOptions.CommandTimeout))]
    [Arguments(nameof(RespireOptions.ConnectionIdleReadTimeout))]
    [Arguments(nameof(RespireOptions.ReceiveBufferSize))]
    [Arguments(nameof(RespireOptions.WriteBufferSize))]
    [Arguments(nameof(RespireOptions.MaxInflightCommands))]
    [Arguments(nameof(RespireOptions.SubscriptionBufferSize))]
    [Arguments(nameof(RespireOptions.TcpKeepAliveTime))]
    [Arguments(nameof(RespireOptions.TcpKeepAliveInterval))]
    [Arguments(nameof(RespireOptions.TcpKeepAliveRetryCount))]
    public async Task Create_RejectsInvalidOptionRanges(string optionName)
    {
        var options = optionName switch
        {
            nameof(RespireOptions.Database) => ValidOptions() with { Database = -1 },
            nameof(RespireOptions.ConnectTimeout) => ValidOptions() with { ConnectTimeout = TimeSpan.Zero },
            nameof(RespireOptions.CommandTimeout) => ValidOptions() with { CommandTimeout = TimeSpan.Zero },
            nameof(RespireOptions.ConnectionIdleReadTimeout) => ValidOptions() with
            {
                ConnectionIdleReadTimeout = TimeSpan.Zero,
            },
            nameof(RespireOptions.ReceiveBufferSize) => ValidOptions() with { ReceiveBufferSize = 0 },
            nameof(RespireOptions.WriteBufferSize) => ValidOptions() with { WriteBufferSize = 0 },
            nameof(RespireOptions.MaxInflightCommands) => ValidOptions() with { MaxInflightCommands = 0 },
            nameof(RespireOptions.SubscriptionBufferSize) => ValidOptions() with { SubscriptionBufferSize = 0 },
            nameof(RespireOptions.TcpKeepAliveTime) => ValidOptions() with
            {
                TcpKeepAliveTime = TimeSpan.FromMilliseconds(999),
            },
            nameof(RespireOptions.TcpKeepAliveInterval) => ValidOptions() with
            {
                TcpKeepAliveTime = TimeSpan.FromSeconds(1),
                TcpKeepAliveInterval = TimeSpan.FromMilliseconds(999),
            },
            nameof(RespireOptions.TcpKeepAliveRetryCount) => ValidOptions() with
            {
                TcpKeepAliveTime = TimeSpan.FromSeconds(1),
                TcpKeepAliveRetryCount = 0,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(optionName)),
        };

        var exception = Assert.Throws<RespireConfigurationException>(() => RespireClient.Create(options));

        await Assert.That(exception.Message).Contains($"RespireOptions.{optionName}");
    }

    [Test]
    [Arguments("localhost:notaport")]
    [Arguments("localhost:")]
    [Arguments("localhost:0")]
    [Arguments("localhost:65536")]
    [Arguments("[::1]:0")]
    [Arguments("[::1]:65536")]
    public async Task Endpoint_RejectsInvalidPorts(string endpoint)
    {
        var exception = Assert.Throws<ArgumentException>(() => RespireEndpoint.Parse(endpoint));

        await Assert.That(exception.Message).Contains("port");
    }

    [Test]
    [Arguments(0)]
    [Arguments(65536)]
    public async Task Create_RejectsEndpointPortOutsideTcpRange(int port)
    {
        var options = new RespireOptions
        {
            Endpoints = { new RespireEndpoint("localhost", port) },
        };

        var exception = Assert.Throws<RespireConfigurationException>(() => RespireClient.Create(options));

        await Assert.That(exception.Message).Contains("RespireOptions.Endpoints");
    }

    [Test]
    public async Task Create_SnapshotsMutableEndpoints()
    {
        List<RespireEndpoint> endpoints = [new RespireEndpoint("first", 6379)];
        var options = new RespireOptions { Endpoints = endpoints };
        await using var client = RespireClient.Create(options);

        endpoints[0] = new RespireEndpoint("second", 6380);

        await Assert.That(client.Endpoint).IsEqualTo(new RespireEndpoint("first", 6379));
        await Assert.That(client.Core.Options.Endpoints)
            .IsEquivalentTo([new RespireEndpoint("first", 6379)]);
    }

    [Test]
    public async Task UriOptions_ExposeDescriptivePropertyNames()
    {
        var options = RespireOptions.Parse(
            "redis://localhost?useCluster=true&sentinelPrimaryName=primary&connectionIdleReadTimeoutMs=2500");

        await Assert.That(options.UseCluster).IsTrue();
        await Assert.That(options.SentinelPrimaryName).IsEqualTo("primary");
        await Assert.That(options.ConnectionIdleReadTimeout)
            .IsEqualTo(TimeSpan.FromMilliseconds(2500));
    }

    [Test]
    public async Task StackExchangeConnectionString_ParsesEndpointPasswordAndTls()
    {
        var options = RespireOptions.Parse("localhost:6379,password=secret,ssl=true");

        await Assert.That(options.Endpoints).IsEquivalentTo([new RespireEndpoint("localhost", 6379)]);
        await Assert.That(options.Password).IsEqualTo("secret");
        await Assert.That(options.UseTls).IsTrue();
    }

    [Test]
    public async Task StackExchangeConnectionString_MapsCommonOptions()
    {
        var options = RespireOptions.Parse(
            "cache-a:6380,user=app,password=secret,clientName=api," +
            "defaultDatabase=2,connectTimeout=1500,asyncTimeout=2500,protocol=resp3," +
            "allowAdmin=true");

        await Assert.That(options.Endpoints).IsEquivalentTo([new RespireEndpoint("cache-a", 6380)]);
        await Assert.That(options.Username).IsEqualTo("app");
        await Assert.That(options.Password).IsEqualTo("secret");
        await Assert.That(options.ClientName).IsEqualTo("api");
        await Assert.That(options.Database).IsEqualTo(2);
        await Assert.That(options.ConnectTimeout).IsEqualTo(TimeSpan.FromMilliseconds(1500));
        await Assert.That(options.CommandTimeout).IsEqualTo(TimeSpan.FromMilliseconds(2500));
        await Assert.That(options.Protocol).IsEqualTo(RespProtocol.Resp3);
        await Assert.That(options.AllowAdmin).IsTrue();
    }

    [Test]
    public async Task StackExchangeConnectionString_MultipleEndpointsFailClearly()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => RespireOptions.Parse("cache-a:6380,cache-b,password=secret"));

        await Assert.That(exception.Message).Contains("multiple endpoints");
        await Assert.That(exception.Message).Contains("RespireOptions");
    }

    [Test]
    public async Task StackExchangeConnectionString_UnknownOptionFailsClearly()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => RespireOptions.Parse("localhost,connectRetry=3"));

        await Assert.That(exception.Message).Contains("connectRetry");
        await Assert.That(exception.Message).Contains("redis://");
    }

    [Test]
    public async Task StackExchangeConnectionString_KeepAliveFailsClearly()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => RespireOptions.Parse("localhost,keepAlive=60"));

        await Assert.That(exception.Message).Contains("keepAlive");
        await Assert.That(exception.Message).Contains("redis://");
    }

    [Test]
    public async Task StackExchangeConnectionString_AllowsUriMarkerInsideOptionValue()
    {
        var options = RespireOptions.Parse("localhost,password=p://secret");

        await Assert.That(options.Endpoints).IsEquivalentTo([new RespireEndpoint("localhost")]);
        await Assert.That(options.Password).IsEqualTo("p://secret");
    }

    [Test]
    public async Task StackExchangeConnectionString_AsyncTimeoutTakesPrecedenceRegardlessOfOrder()
    {
        var asyncFirst = RespireOptions.Parse("localhost,asyncTimeout=1500,syncTimeout=2500");
        var asyncLast = RespireOptions.Parse("localhost,syncTimeout=2500,asyncTimeout=1500");

        await Assert.That(asyncFirst.CommandTimeout).IsEqualTo(TimeSpan.FromMilliseconds(1500));
        await Assert.That(asyncLast.CommandTimeout).IsEqualTo(TimeSpan.FromMilliseconds(1500));
    }

    [Test]
    public async Task Endpoint_ParsesBareAndBracketedIpv6()
    {
        await Assert.That(RespireEndpoint.Parse("::1")).IsEqualTo(new RespireEndpoint("::1"));
        await Assert.That(RespireEndpoint.Parse("[::1]")).IsEqualTo(new RespireEndpoint("::1"));
        await Assert.That(RespireEndpoint.Parse("[::1]:6380")).IsEqualTo(new RespireEndpoint("::1", 6380));
        await Assert.That(new RespireEndpoint("::1").ToString()).IsEqualTo("[::1]:6379");
    }

    [Test]
    public async Task ConnectionString_DefaultsAllowAdminToFalse()
    {
        var hostOnly = RespireOptions.Parse("localhost");
        var uri = RespireOptions.Parse("redis://localhost");

        await Assert.That(hostOnly.AllowAdmin).IsFalse();
        await Assert.That(uri.AllowAdmin).IsFalse();
    }

    [Test]
    public async Task ConnectionString_ParsesAllowAdmin()
    {
        var enabled = RespireOptions.Parse("redis://localhost?allowAdmin=true");
        var disabled = RespireOptions.Parse("redis://localhost?allowAdmin=false");

        await Assert.That(enabled.AllowAdmin).IsTrue();
        await Assert.That(disabled.AllowAdmin).IsFalse();
    }

    private static RespireOptions ValidOptions() => new()
    {
        Endpoints = { new RespireEndpoint("localhost") },
    };
}
