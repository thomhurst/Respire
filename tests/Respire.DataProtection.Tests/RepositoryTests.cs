using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Respire.Testing;
using StackExchange.Redis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.DataProtection.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class RepositoryTests(RedisTestContainer fixture)
{
    [Test]
    public async Task RoundTripPreservesOrderDuplicatesAndXmlWithoutExpiry()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var key = $"keys:{Guid.NewGuid():N}";
        var repository = new RespireXmlRepository(() => client, key);
        await Assert.That(repository.GetAllElements().Count).IsEqualTo(0);
        var first = XElement.Parse("<key id='one'><secret>héllo &amp; 世界</secret></key>");
        var second = XElement.Parse("<revocation><reason>retired</reason></revocation>");
        repository.StoreElement(first, "duplicate-name");
        repository.StoreElement(second, "duplicate-name");
        repository.StoreElement(first, "duplicate-name");

        var elements = repository.GetAllElements().ToArray();
        await Assert.That(elements.Length).IsEqualTo(3);
        await Assert.That(XNode.DeepEquals(first, elements[0])).IsTrue();
        await Assert.That(XNode.DeepEquals(second, elements[1])).IsTrue();
        await Assert.That(XNode.DeepEquals(first, elements[2])).IsTrue();
        using var ttl = await client.ExecuteAsync("PTTL", key);
        await Assert.That(ttl.AsInteger()).IsEqualTo(-1);
    }

    [Test]
    public async Task MicrosoftAndRespireReadEachOthersElements()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        using var connection = await ConnectionMultiplexer.ConnectAsync(fixture.StackExchangeConnectionString);
        var key = $"keys:{Guid.NewGuid():N}";
        var microsoft = new RedisXmlRepository(() => connection.GetDatabase(), key);
        var respire = new RespireXmlRepository(() => client, key);
        var first = XElement.Parse("<key id='microsoft'><value>one</value></key>");
        var second = XElement.Parse("<revocation><value>two</value></revocation>");

        microsoft.StoreElement(first, "first");
        await Assert.That(XNode.DeepEquals(first, respire.GetAllElements().Single())).IsTrue();
        respire.StoreElement(second, "second");
        var elements = microsoft.GetAllElements().ToArray();
        await Assert.That(elements.Length).IsEqualTo(2);
        await Assert.That(XNode.DeepEquals(first, elements[0])).IsTrue();
        await Assert.That(XNode.DeepEquals(second, elements[1])).IsTrue();
    }

    [Test]
    public async Task MalformedEntryFailsEntireRead()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var key = $"keys:{Guid.NewGuid():N}";
        var repository = new RespireXmlRepository(() => client, key);
        repository.StoreElement(new XElement("key"), "valid");
        await client.Lists.RightPushAsync(key, "<broken");

        await Assert.That(() => repository.GetAllElements()).Throws<XmlException>();
    }

    [Test]
    public async Task WrongRedisTypePropagatesReadAndWriteErrors()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var key = $"keys:{Guid.NewGuid():N}";
        using var response = await client.ExecuteAsync("SET", key, "not-a-list");
        var repository = new RespireXmlRepository(() => client, key);

        await Assert.That(() => repository.GetAllElements()).Throws<RespireServerException>();
        await Assert.That(() => repository.StoreElement(new XElement("key"), "key"))
            .Throws<RespireServerException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ProvidersShareProtectedPayloadsAcrossImplementations(bool respireCreatesKey)
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        using var connection = await ConnectionMultiplexer.ConnectAsync(fixture.StackExchangeConnectionString);
        var key = $"keys:{Guid.NewGuid():N}";
        var microsoftServices = new ServiceCollection();
        microsoftServices.AddDataProtection().SetApplicationName("shared-app")
            .PersistKeysToStackExchangeRedis(connection, key);
        using var microsoftProvider = microsoftServices.BuildServiceProvider();
        var microsoft = microsoftProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("purpose");
        var respireServices = new ServiceCollection();
        respireServices.AddDataProtection().SetApplicationName("shared-app")
            .PersistKeysToRespire(() => client, key);
        using (var respireProvider = respireServices.BuildServiceProvider())
        {
            var respire = respireProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("purpose");
            var writer = respireCreatesKey ? respire : microsoft;
            var reader = respireCreatesKey ? microsoft : respire;
            await Assert.That(reader.Unprotect(writer.Protect("writer payload"))).IsEqualTo("writer payload");
            await Assert.That(writer.Unprotect(reader.Protect("reader payload"))).IsEqualTo("reader payload");
        }

        // Disposing the service provider must not dispose the caller's connection.
        await Assert.That((await client.Lists.RangeAsync(key)).Length).IsGreaterThan(0);
    }

    [Test]
    public async Task RegistrationIsLazyAndUsesSuppliedFactoryForEachOperation()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var calls = 0;
        var services = new ServiceCollection();
        var builder = services.AddDataProtection();
        var result = builder.PersistKeysToRespire(() => { calls++; return client; }, $"keys:{Guid.NewGuid():N}");
        using var provider = services.BuildServiceProvider();
        var repository = provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository!;
        await Assert.That(ReferenceEquals(builder, result)).IsTrue();
        await Assert.That(calls).IsEqualTo(0);
        repository.StoreElement(new XElement("key"), "one");
        repository.GetAllElements();
        await Assert.That(calls).IsEqualTo(2);
    }
}
