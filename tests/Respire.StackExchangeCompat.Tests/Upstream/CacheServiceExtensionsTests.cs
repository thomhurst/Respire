// Adapted from dotnet/aspnetcore v10.0.12:
// src/Caching/StackExchangeRedis/test/CacheServiceExtensionsTests.cs
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// Changes: TUnit discovery, adapter factory, and deterministic disposal.
// The internal HybridCache detection test is excluded; see NOTICE.md.

using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Respire;
using Respire.StackExchangeCompat;
using TUnit.Core;
using Assert = Xunit.Assert;

namespace Microsoft.Extensions.Caching.StackExchangeRedis;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class CacheServiceExtensionsTests(RedisTestContainer fixture)
{
    private void AddCache(IServiceCollection services) => services.AddStackExchangeRedisCache(options =>
    {
        options.ConnectionMultiplexerFactory = () => Task.FromResult<StackExchange.Redis.IConnectionMultiplexer>(
            RespireConnectionMultiplexer.Create(RespireOptions.Parse(fixture.ConnectionString)));
    });

    [Test]
    public void AddStackExchangeRedisCache_RegistersDistributedCacheAsSingleton()
    {
        var services = new ServiceCollection();
        AddCache(services);
        var distributedCache = services.FirstOrDefault(desc => desc.ServiceType == typeof(IDistributedCache));
        Assert.NotNull(distributedCache);
        Assert.Equal(ServiceLifetime.Singleton, distributedCache.Lifetime);
    }

    [Test]
    public void AddStackExchangeRedisCache_ReplacesPreviouslyUserRegisteredServices()
    {
        var services = new ServiceCollection();
        services.AddScoped(typeof(IDistributedCache), _ => Mock.Of<IDistributedCache>());
        AddCache(services);
        using var provider = services.BuildServiceProvider();
        var distributedCache = services.FirstOrDefault(desc => desc.ServiceType == typeof(IDistributedCache));
        Assert.NotNull(distributedCache);
        Assert.Equal(ServiceLifetime.Scoped, distributedCache.Lifetime);
        Assert.IsAssignableFrom<RedisCache>(provider.GetRequiredService<IDistributedCache>());
    }

    [Test]
    public void AddStackExchangeRedisCache_allows_chaining()
    {
        var services = new ServiceCollection();
        Assert.Same(services, services.AddStackExchangeRedisCache(_ => { }));
    }

    [Test]
    public void AddStackExchangeRedisCache_IDistributedCacheWithoutLoggingCanBeResolved()
    {
        var services = new ServiceCollection();
        AddCache(services);
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<IDistributedCache>());
    }

    [Test]
    public void AddStackExchangeRedisCache_IDistributedCacheWithLoggingCanBeResolved()
    {
        var services = new ServiceCollection();
        AddCache(services);
        services.AddLogging();
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<IDistributedCache>());
    }

    [Test]
    public void AddStackExchangeRedisCache_UsesLoggerFactoryAlreadyRegisteredWithServiceCollection()
    {
        var services = new ServiceCollection();
        services.AddScoped(typeof(IDistributedCache), _ => Mock.Of<IDistributedCache>());
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(factory => factory.CreateLogger(It.IsAny<string>()))
            .Returns((string name) => NullLoggerFactory.Instance.CreateLogger(name)).Verifiable();
        services.AddScoped(typeof(ILoggerFactory), _ => loggerFactory.Object);
        services.AddLogging();
        AddCache(services);
        using var provider = services.BuildServiceProvider();
        var distributedCache = services.FirstOrDefault(desc => desc.ServiceType == typeof(IDistributedCache));
        Assert.NotNull(distributedCache);
        Assert.Equal(ServiceLifetime.Scoped, distributedCache.Lifetime);
        Assert.IsAssignableFrom<RedisCache>(provider.GetRequiredService<IDistributedCache>());
        loggerFactory.Verify();
    }
}
