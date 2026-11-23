#nullable disable
// Adapted from dotnet/aspnetcore v10.0.12: src/Caching/StackExchangeRedis/test/TimeExpirationTests.cs
// Changes: TUnit discovery, Testcontainers isolation, adapter factory, deterministic disposal, and timing-test isolation.
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.CompilerServices;
using System.Threading;

using Microsoft.Extensions.Caching.Distributed;

using TUnit.Core;
using Assert = Xunit.Assert;
using Respire.StackExchangeCompat;

namespace Microsoft.Extensions.Caching.StackExchangeRedis;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
// Preserve upstream TTLs without concurrent blocking tests starving refresh continuations on small CI runners.
[NotInParallel]
public class TimeExpirationTests(RedisTestContainer fixture)
{
    [Test]
    public void AbsoluteExpirationInThePastThrows()
    {
        using var cache = UpstreamCache.Create(fixture);
        var key = GetNameAndReset(cache);
        var value = new byte[1];

        var expected = DateTimeOffset.Now - TimeSpan.FromMinutes(1);
        UpstreamCache.ThrowsArgumentOutOfRange(
            () =>
            {
                cache.Set(key, value, new DistributedCacheEntryOptions().SetAbsoluteExpiration(expected));
            },
            nameof(DistributedCacheEntryOptions.AbsoluteExpiration),
            "The absolute expiration value must be in the future.",
            expected);
    }

    [Test]
    public void AbsoluteExpirationExpires()
    {
        using var cache = UpstreamCache.Create(fixture);
        var key = GetNameAndReset(cache);
        var value = new byte[1];

        cache.Set(key, value, new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromSeconds(1)));

        byte[] result = cache.Get(key);
        Assert.Equal(value, result);

        for (int i = 0; i < 4 && (result != null); i++)
        {
            Thread.Sleep(TimeSpan.FromSeconds(0.5));
            result = cache.Get(key);
        }

        Assert.Null(result);
    }

    [Test]
    public void AbsoluteSubSecondExpirationExpiresImmediately()
    {
        using var cache = UpstreamCache.Create(fixture);
        var key = GetNameAndReset(cache);
        var value = new byte[1];

        cache.Set(key, value, new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromSeconds(0.25)));

        var result = cache.Get(key);
        Assert.Null(result);
    }

    [Test]
    public void NegativeRelativeExpirationThrows()
    {
        using var cache = UpstreamCache.Create(fixture);
        var key = GetNameAndReset(cache);
        var value = new byte[1];

        UpstreamCache.ThrowsArgumentOutOfRange(() =>
        {
            cache.Set(key, value, new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromMinutes(-1)));
        },
        nameof(DistributedCacheEntryOptions.AbsoluteExpirationRelativeToNow),
        "The relative expiration value must be positive.",
        TimeSpan.FromMinutes(-1));
    }

    [Test]
    public void ZeroRelativeExpirationThrows()
    {
        using var cache = UpstreamCache.Create(fixture);
        var key = GetNameAndReset(cache);
        var value = new byte[1];

        UpstreamCache.ThrowsArgumentOutOfRange(
            () =>
            {
                cache.Set(key, value, new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.Zero));
            },
            nameof(DistributedCacheEntryOptions.AbsoluteExpirationRelativeToNow),
            "The relative expiration value must be positive.",
            TimeSpan.Zero);
    }

    [Test]
    public void RelativeExpirationExpires()
    {
        using var cache = UpstreamCache.Create(fixture);
        var key = GetNameAndReset(cache);
        var value = new byte[1];

        cache.Set(key, value, new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromSeconds(1)));

        var result = cache.Get(key);
        Assert.Equal(value, result);

        for (int i = 0; i < 4 && (result != null); i++)
        {
            Thread.Sleep(TimeSpan.FromSeconds(0.5));
            result = cache.Get(key);
        }

        Assert.Null(result);
    }

    [Test]
    public void RelativeSubSecondExpirationExpiresImmediately()
    {
        using var cache = UpstreamCache.Create(fixture);
        var key = GetNameAndReset(cache);
        var value = new byte[1];

        cache.Set(key, value, new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromSeconds(0.25)));

        var result = cache.Get(key);
        Assert.Null(result);
    }

    [Test]
    public void NegativeSlidingExpirationThrows()
    {
        using var cache = UpstreamCache.Create(fixture);
        var key = GetNameAndReset(cache);
        var value = new byte[1];

        UpstreamCache.ThrowsArgumentOutOfRange(() =>
        {
            cache.Set(key, value, new DistributedCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromMinutes(-1)));
        }, nameof(DistributedCacheEntryOptions.SlidingExpiration), "The sliding expiration value must be positive.", TimeSpan.FromMinutes(-1));
    }

    [Test]
    public void ZeroSlidingExpirationThrows()
    {
        using var cache = UpstreamCache.Create(fixture);
        var key = GetNameAndReset(cache);
        var value = new byte[1];

        UpstreamCache.ThrowsArgumentOutOfRange(
            () =>
            {
                cache.Set(key, value, new DistributedCacheEntryOptions().SetSlidingExpiration(TimeSpan.Zero));
            },
            nameof(DistributedCacheEntryOptions.SlidingExpiration),
            "The sliding expiration value must be positive.",
            TimeSpan.Zero);
    }

    [Test]
    public void SlidingExpirationExpiresIfNotAccessed()
    {
        using var cache = UpstreamCache.Create(fixture);
        var key = GetNameAndReset(cache);
        var value = new byte[1];

        cache.Set(key, value, new DistributedCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromSeconds(1)));

        var result = cache.Get(key);
        Assert.Equal(value, result);

        Thread.Sleep(TimeSpan.FromSeconds(3.5));

        result = cache.Get(key);
        Assert.Null(result);
    }

    [Test]
    public void SlidingSubSecondExpirationExpiresImmediately()
    {
        using var cache = UpstreamCache.Create(fixture);
        var key = GetNameAndReset(cache);
        var value = new byte[1];

        cache.Set(key, value, new DistributedCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromSeconds(0.25)));

        var result = cache.Get(key);
        Assert.Null(result);
    }

    [Test]
    public void SlidingExpirationRenewedByAccess()
    {
        using var cache = UpstreamCache.Create(fixture);
        var key = GetNameAndReset(cache);
        var value = new byte[1];

        cache.Set(key, value, new DistributedCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromSeconds(1)));

        var result = cache.Get(key);
        Assert.Equal(value, result);

        for (int i = 0; i < 5; i++)
        {
            Thread.Sleep(TimeSpan.FromSeconds(0.5));

            result = cache.Get(key);
            Assert.Equal(value, result);
        }

        Thread.Sleep(TimeSpan.FromSeconds(3));
        result = cache.Get(key);
        Assert.Null(result);
    }

    [Test]
    public void SlidingExpirationRenewedByAccessUntilAbsoluteExpiration()
    {
        using var cache = UpstreamCache.Create(fixture);
        using var control = StackExchange.Redis.ConnectionMultiplexer.Connect(fixture.IsolatedStackExchangeConnectionString);
        var database = control.GetDatabase();
        var key = GetNameAndReset(cache);
        var redisKey = UpstreamCache.KeyPrefix + key;
        var value = new byte[1];
        var absoluteExpiration = DateTimeOffset.UtcNow.AddMinutes(5);
        var shortenedTtl = TimeSpan.FromMinutes(1);

        cache.Set(key, value, new DistributedCacheEntryOptions()
            .SetSlidingExpiration(TimeSpan.FromMinutes(10))
            .SetAbsoluteExpiration(absoluteExpiration));

        // Shorten the server TTL without changing the cache entry's expiration metadata.
        Assert.True(database.KeyExpire(redisKey, shortenedTtl));

        var beforeRead = DateTimeOffset.UtcNow;
        Assert.Equal(value, cache.Get(key));
        TimeSpan? renewedTtl = database.KeyTimeToLive(redisKey);
        var afterProbe = DateTimeOffset.UtcNow;

        Assert.NotNull(renewedTtl);
        Assert.True(renewedTtl.Value > shortenedTtl, $"Expected a TTL greater than {shortenedTtl}, got {renewedTtl}.");

        // Allow millisecond rounding when setting and reading the TTL.
        var roundingTolerance = TimeSpan.FromMilliseconds(2);
        Assert.InRange(renewedTtl.Value, absoluteExpiration - afterProbe - roundingTolerance,
            absoluteExpiration - beforeRead + roundingTolerance);
    }

    static string GetNameAndReset(IDistributedCache cache, [CallerMemberName] string caller = "")
    {
        cache.Remove(caller);
        return caller;
    }
}
