// Adapted from dotnet/aspnetcore v10.0.12:
// src/DataProtection/StackExchangeRedis/test/RedisDataProtectionBuilderExtensionsTest.cs
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// Changes: TUnit discovery, real adapter in place of the multiplexer mock, and deterministic disposal.

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Core;
using Assert = Xunit.Assert;

namespace Respire.StackExchangeCompat.Tests.Upstream;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class RedisDataProtectionBuilderExtensionsTest(RedisTestContainer fixture)
{
    [Test]
    public void PersistKeysToRedis_UsesRedisXmlRepository()
    {
        using var connection = RespireConnectionMultiplexer.Create(RespireOptions.Parse(fixture.IsolatedConnectionString));
        var serviceCollection = new ServiceCollection();
        var builder = serviceCollection.AddDataProtection();
        builder.PersistKeysToStackExchangeRedis(connection);
        using var services = serviceCollection.BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<KeyManagementOptions>>();
        Assert.IsType<RedisXmlRepository>(options.Value.XmlRepository);
    }
}
