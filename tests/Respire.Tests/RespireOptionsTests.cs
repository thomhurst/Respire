using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class RespireOptionsTests
{
    [Test]
    public async Task StackExchangeConnectionString_ParsesEndpointPasswordAndTls()
    {
        var options = RespireOptions.Parse("localhost:6379,password=secret,ssl=true");

        await Assert.That(options.Endpoints).IsEquivalentTo([new RespireEndpoint("localhost", 6379)]);
        await Assert.That(options.Password).IsEqualTo("secret");
        await Assert.That(options.UseTls).IsTrue();
    }

    [Test]
    public async Task StackExchangeConnectionString_MapsCommonOptionsAndMultipleEndpoints()
    {
        var options = RespireOptions.Parse(
            "cache-a:6380,cache-b,user=app,password=secret,clientName=api," +
            "defaultDatabase=2,connectTimeout=1500,asyncTimeout=2500,protocol=resp3," +
            "allowAdmin=true,keepAlive=60");

        await Assert.That(options.Endpoints).IsEquivalentTo(
            [new RespireEndpoint("cache-a", 6380), new RespireEndpoint("cache-b")]);
        await Assert.That(options.Username).IsEqualTo("app");
        await Assert.That(options.Password).IsEqualTo("secret");
        await Assert.That(options.ClientName).IsEqualTo("api");
        await Assert.That(options.Database).IsEqualTo(2);
        await Assert.That(options.ConnectTimeout).IsEqualTo(TimeSpan.FromMilliseconds(1500));
        await Assert.That(options.CommandTimeout).IsEqualTo(TimeSpan.FromMilliseconds(2500));
        await Assert.That(options.Protocol).IsEqualTo(RespProtocol.Resp3);
        await Assert.That(options.AllowAdmin).IsTrue();
        await Assert.That(options.TcpKeepAliveTime).IsEqualTo(TimeSpan.FromSeconds(60));
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
}
