// Adapted from dotnet/aspnetcore v10.0.12:
// src/DataProtection/StackExchangeRedis/test/DataProtectionRedisTests.cs
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// Changes: TUnit discovery and Testcontainers; replace mocks with real shim/database calls.
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.StackExchangeRedis;
using StackExchange.Redis;
using TUnit.Core;
using Assert = Xunit.Assert;

namespace Respire.StackExchangeCompat.Tests.Upstream;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class DataProtectionRedisTests(RedisTestContainer fixture)
{
    private RespireConnectionMultiplexer Connect() => RespireConnectionMultiplexer.Create(RespireOptions.Parse(fixture.ConnectionString));

    [Test]
    public void GetAllElements_ReturnsAllXmlValuesForGivenKey()
    {
        using var connection = Connect();
        var database = connection.GetDatabase();
        database.ListRightPush("Key", ["<Element1/>", "<Element2/>"]);
        var repo = new RedisXmlRepository(() => database, "Key");
        var elements = repo.GetAllElements().ToArray();
        Assert.Equal(new XElement("Element1").ToString(), elements[0].ToString());
        Assert.Equal(new XElement("Element2").ToString(), elements[1].ToString());
    }

    [Test]
    public void GetAllElements_ThrowsParsingException()
    {
        using var connection = Connect();
        var database = connection.GetDatabase();
        database.ListRightPush("Key", ["<Element1/>", "<Element2"]);
        var repo = new RedisXmlRepository(() => database, "Key");
        Assert.Throws<XmlException>(() => repo.GetAllElements());
    }

    [Test]
    public void StoreElement_PushesValueToList()
    {
        using var connection = Connect();
        var database = connection.GetDatabase();
        var repo = new RedisXmlRepository(() => database, "Key");
        repo.StoreElement(new XElement("Element2"), null!);
        Assert.Equal("<Element2 />", (string?)database.ListRange("Key").Single());
    }

    [Test]
    public async Task XmlRoundTripsToActualRedisServer()
    {
        var guid = Guid.NewGuid().ToString();
        RedisKey key = "Test:DP:Key" + guid;
        await using (var redis = Connect())
        {
            var repo = new RedisXmlRepository(() => redis.GetDatabase(), key);
            repo.StoreElement(new XElement("HelloRedis", guid), guid);
        }
        await using (var redis = Connect())
        {
            var repo = new RedisXmlRepository(() => redis.GetDatabase(), key);
            Assert.Contains(repo.GetAllElements(), e => e.Name == "HelloRedis" && e.Value == guid);
            await redis.GetDatabase().KeyDeleteAsync(key);
        }
    }
}
