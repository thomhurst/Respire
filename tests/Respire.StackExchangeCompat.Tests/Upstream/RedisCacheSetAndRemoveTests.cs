#nullable disable
// Adapted from dotnet/aspnetcore v10.0.12: src/Caching/StackExchangeRedis/test/RedisCacheSetAndRemoveTests.cs
// Changes: TUnit discovery, Testcontainers isolation, adapter factory, and deterministic disposal.
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using TUnit.Core;
using Assert = Xunit.Assert;
using Respire.StackExchangeCompat;

namespace Microsoft.Extensions.Caching.StackExchangeRedis;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class RedisCacheSetAndRemoveTests(RedisTestContainer fixture)
{
[Test]
    public void GetMissingKeyReturnsNull()
    {
        using var cache = UpstreamCache.Create(fixture);
        string key = "non-existent-key";

        var result = cache.Get(key);
        Assert.Null(result);
    }

    [Test]
    public void SetAndGetReturnsObject()
    {
        using var cache = UpstreamCache.Create(fixture);
        var value = new byte[1];
        string key = "myKey";

        cache.Set(key, value);

        var result = cache.Get(key);
        Assert.Equal(value, result);
    }

    [Test]
    public void SetAndGetWorksWithCaseSensitiveKeys()
    {
        using var cache = UpstreamCache.Create(fixture);
        var value = new byte[1];
        string key1 = "myKey";
        string key2 = "Mykey";

        cache.Set(key1, value);

        var result = cache.Get(key1);
        Assert.Equal(value, result);

        result = cache.Get(key2);
        Assert.Null(result);
    }

    [Test]
    public void SetAlwaysOverwrites()
    {
        using var cache = UpstreamCache.Create(fixture);
        var value1 = new byte[1] { 1 };
        string key = "myKey";

        cache.Set(key, value1);
        var result = cache.Get(key);
        Assert.Equal(value1, result);

        var value2 = new byte[1] { 2 };
        cache.Set(key, value2);
        result = cache.Get(key);
        Assert.Equal(value2, result);
    }

    [Test]
    public void RemoveRemoves()
    {
        using var cache = UpstreamCache.Create(fixture);
        var value = new byte[1];
        string key = "myKey";

        cache.Set(key, value);
        var result = cache.Get(key);
        Assert.Equal(value, result);

        cache.Remove(key);
        result = cache.Get(key);
        Assert.Null(result);
    }

    [Test]
    public void SetNullValueThrows()
    {
        using var cache = UpstreamCache.Create(fixture);
        byte[] value = null;
        string key = "myKey";

        Assert.Throws<ArgumentNullException>(() => cache.Set(key, value));
    }

    [Test]
    public void SetGetEmptyNonNullBuffer()
    {
        using var cache = UpstreamCache.Create(fixture);
        var key = Me();
        cache.Remove(key); // known state
        Assert.Null(cache.Get(key)); // expect null

        cache.Set(key, Array.Empty<byte>());
        var arr = cache.Get(key);
        Assert.NotNull(arr);
        Assert.Empty(arr);
    }

    [Test]
    public async Task SetGetEmptyNonNullBufferAsync()
    {
        using var cache = UpstreamCache.Create(fixture);
        var key = Me();
        await cache.RemoveAsync(key); // known state
        Assert.Null(await cache.GetAsync(key)); // expect null

        await cache.SetAsync(key, Array.Empty<byte>());
        var arr = await cache.GetAsync(key);
        Assert.NotNull(arr);
        Assert.Empty(arr);
    }

    [Test]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments("abc")]
    public void SetGetNonNullString(string payload)
    {
        using var cache = UpstreamCache.Create(fixture);
        var key = Me();
        cache.Remove(key); // known state
        Assert.Null(cache.Get(key)); // expect null
        cache.SetString(key, payload);

        // check raw bytes
        var raw = cache.Get(key);
        Assert.Equal(Hex(payload), Hex(raw));

        // check via string API
        var value = cache.GetString(key);
        Assert.NotNull(value);
        Assert.Equal(payload, value);
    }

    [Test]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments("abc")]
    [Arguments("abc def ghi jkl mno pqr stu vwx yz!")]
    public async Task SetGetNonNullStringAsync(string payload)
    {
        using var cache = UpstreamCache.Create(fixture);
        var key = Me();
        await cache.RemoveAsync(key); // known state
        Assert.Null(await cache.GetAsync(key)); // expect null
        await cache.SetStringAsync(key, payload);

        // check raw bytes
        var raw = await cache.GetAsync(key);
        Assert.Equal(Hex(payload), Hex(raw));

        // check via string API
        var value = await cache.GetStringAsync(key);
        Assert.NotNull(value);
        Assert.Equal(payload, value);
    }

    static string Hex(byte[] value) => BitConverter.ToString(value);
    static string Hex(string value) => Hex(Encoding.UTF8.GetBytes(value));

    private static string Me([CallerMemberName] string caller = "") => caller;
}
