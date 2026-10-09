#nullable disable
// Adapted from dotnet/aspnetcore v10.0.12: src/Caching/StackExchangeRedis/test/TimeExpirationAsyncTests.cs
// Changes: TUnit discovery, Testcontainers isolation, adapter factory, deterministic disposal, and timing-test isolation.
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;

using TUnit.Core;
using Assert = Xunit.Assert;
using Respire.StackExchangeCompat;

namespace Microsoft.Extensions.Caching.StackExchangeRedis;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
// Preserve upstream TTLs without concurrent blocking tests starving refresh continuations on small CI runners.
[NotInParallel]
public class TimeExpirationAsyncTests(RedisTestContainer fixture)
{
// async twin to UpstreamCache.ThrowsArgumentOutOfRange
    static async Task ThrowsArgumentOutOfRangeAsync(Func<Task> test, string paramName, string message, object actualValue)
    {
        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(test);
        if (paramName is not null)
        {
            Assert.Equal(paramName, ex.ParamName);
        }
        if (message is not null)
        {
            Assert.StartsWith(message, ex.Message); // can have "\r\nParameter name:" etc
        }
        if (actualValue is not null)
        {
            Assert.Equal(actualValue, ex.ActualValue);
        }
    }

    [Test]
    public async Task AbsoluteExpirationInThePastThrows()
    {
        using var cache = UpstreamCache.Create(fixture);
        var key = await GetNameAndReset(cache);
        var value = new byte[1];

        var expected = DateTimeOffset.Now - TimeSpan.FromMinutes(1);
        await ThrowsArgumentOutOfRangeAsync(
            async () =>
            {
                await cache.SetAsync(key, value, new DistributedCacheEntryOptions().SetAbsoluteExpiration(expected));
            },
            nameof(DistributedCacheEntryOptions.AbsoluteExpiration),
            "The absolute expiration value must be in the future.",
            expected);
    }

    [Test]
    public async Task AbsoluteExpirationExpires()
    {
        using var cache = UpstreamCache.Create(fixture);
        var key = await GetNameAndReset(cache);
        var value = new byte[1];

        await cache.SetAsync(key, value, new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromSeconds(1)));

        byte[] result = await cache.GetAsync(key);
        Assert.Equal(value, result);

        for (int i = 0; i < 4 && (result != null); i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(0.5));
            result = await cache.GetAsync(key);
        }

        Assert.Null(result);
    }

    [Test]
    public async Task AbsoluteSubSecondExpirationExpiresImmediately()
    {
        using var cache = UpstreamCache.Create(fixture);
        var key = await GetNameAndReset(cache);
        var value = new byte[1];

        await cache.SetAsync(key, value, new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromSeconds(0.25)));

        var result = await cache.GetAsync(key);
        Assert.Null(result);
    }

    [Test]
    public async Task NegativeRelativeExpirationThrows()
    {
        using var cache = UpstreamCache.Create(fixture);
        var key = await GetNameAndReset(cache);
        var value = new byte[1];

        await ThrowsArgumentOutOfRangeAsync(async () =>
        {
            await cache.SetAsync(key, value, new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromMinutes(-1)));
        },
        nameof(DistributedCacheEntryOptions.AbsoluteExpirationRelativeToNow),
        "The relative expiration value must be positive.",
        TimeSpan.FromMinutes(-1));
    }

    [Test]
    public async Task ZeroRelativeExpirationThrows()
    {
        using var cache = UpstreamCache.Create(fixture);
        var key = await GetNameAndReset(cache);
        var value = new byte[1];

        await ThrowsArgumentOutOfRangeAsync(async () =>
            {
                await cache.SetAsync(key, value, new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.Zero));
            },
            nameof(DistributedCacheEntryOptions.AbsoluteExpirationRelativeToNow),
            "The relative expiration value must be positive.",
            TimeSpan.Zero);
    }

    [Test]
    public async Task RelativeExpirationExpires()
    {
        using var cache = UpstreamCache.Create(fixture);
        var key = await GetNameAndReset(cache);
        var value = new byte[1];

        await cache.SetAsync(key, value, new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromSeconds(1)));

        var result = await cache.GetAsync(key);
        Assert.Equal(value, result);

        for (int i = 0; i < 4 && (result != null); i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(0.5));
            result = await cache.GetAsync(key);
        }
        Assert.Null(result);
    }

    [Test]
    public async Task RelativeSubSecondExpirationExpiresImmediately()
    {
        using var cache = UpstreamCache.Create(fixture);
        var key = await GetNameAndReset(cache);
        var value = new byte[1];

        await cache.SetAsync(key, value, new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromSeconds(0.25)));

        var result = await cache.GetAsync(key);
        Assert.Null(result);
    }

    [Test]
    public async Task NegativeSlidingExpirationThrows()
    {
        using var cache = UpstreamCache.Create(fixture);
        var key = await GetNameAndReset(cache);
        var value = new byte[1];

        await ThrowsArgumentOutOfRangeAsync(async () =>
        {
            await cache.SetAsync(key, value, new DistributedCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromMinutes(-1)));
        }, nameof(DistributedCacheEntryOptions.SlidingExpiration), "The sliding expiration value must be positive.", TimeSpan.FromMinutes(-1));
    }

    [Test]
    public async Task ZeroSlidingExpirationThrows()
    {
        using var cache = UpstreamCache.Create(fixture);
        var key = await GetNameAndReset(cache);
        var value = new byte[1];

        await ThrowsArgumentOutOfRangeAsync(async () =>
        {
            await cache.SetAsync(key, value, new DistributedCacheEntryOptions().SetSlidingExpiration(TimeSpan.Zero));
        },
        nameof(DistributedCacheEntryOptions.SlidingExpiration),
        "The sliding expiration value must be positive.",
        TimeSpan.Zero);
    }

    [Test]
    public async Task SlidingExpirationExpiresIfNotAccessed()
    {
        using var cache = UpstreamCache.Create(fixture);
        var key = await GetNameAndReset(cache);
        var value = new byte[1];

        await cache.SetAsync(key, value, new DistributedCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromSeconds(1)));

        var result = await cache.GetAsync(key);
        Assert.Equal(value, result);

        await Task.Delay(TimeSpan.FromSeconds(3.5));

        result = await cache.GetAsync(key);
        Assert.Null(result);
    }

    [Test]
    public async Task SlidingSubSecondExpirationExpiresImmediately()
    {
        using var cache = UpstreamCache.Create(fixture);
        var key = await GetNameAndReset(cache);
        var value = new byte[1];

        await cache.SetAsync(key, value, new DistributedCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromSeconds(0.25)));

        var result = await cache.GetAsync(key);
        Assert.Null(result);
    }

    [Test]
    public async Task SlidingExpirationRenewedByAccess()
    {
        using var cache = UpstreamCache.Create(fixture);
        var key = await GetNameAndReset(cache);
        var value = new byte[1];

        await cache.SetAsync(key, value, new DistributedCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromSeconds(1)));

        var result = await cache.GetAsync(key);
        Assert.Equal(value, result);

        for (int i = 0; i < 5; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(0.5));

            result = await cache.GetAsync(key);
            Assert.Equal(value, result);
        }

        await Task.Delay(TimeSpan.FromSeconds(3));
        result = await cache.GetAsync(key);
        Assert.Null(result);
    }

    [Test]
    public async Task SlidingExpirationRenewedByAccessUntilAbsoluteExpiration()
    {
        using var cache = UpstreamCache.Create(fixture);
        var key = await GetNameAndReset(cache);
        var value = new byte[1];

        await cache.SetAsync(key, value, new DistributedCacheEntryOptions()
            .SetSlidingExpiration(TimeSpan.FromSeconds(1))
            .SetAbsoluteExpiration(TimeSpan.FromSeconds(3)));

        var setTime = DateTime.Now;
        var result = await cache.GetAsync(key);
        Assert.Equal(value, result);

        for (int i = 0; i < 5; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(0.5));

            result = await cache.GetAsync(key);
            Assert.NotNull(result);
            Assert.Equal(value, result);
        }

        while ((DateTime.Now - setTime).TotalSeconds < 4)
        {
            await Task.Delay(TimeSpan.FromSeconds(0.5));
        }

        result = await cache.GetAsync(key);
        Assert.Null(result);
    }

    static async Task<string> GetNameAndReset(IDistributedCache cache, [CallerMemberName] string caller = "")
    {
        await cache.RemoveAsync(caller);
        return caller;
    }
}
