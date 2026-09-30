using FluentAssertions;
using Testcontainers.Redis;
using TUnit.Core;

namespace Respire.IntegrationTests;

public class ScriptCacheIntegrationTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ReadOnlyScriptsAndCacheCommandsRoundTrip(int protocol)
    {
        // SCRIPT FLUSH is server-wide, so these tests must not use the shared Redis fixture.
        await using var server = new RedisBuilder().WithImage("redis:7.4-alpine").Build();
        await server.StartAsync();
        await using var owner = await RespireClient.ConnectAsync(
            $"redis://{server.Hostname}:{server.GetMappedPublicPort(6379)}?protocol={protocol}");
        var client = owner.WithKeyPrefix("tenant:");
        await client.SetAsync("key", "value");
        var script = RespireScript.Create("return redis.call('GET', KEYS[1])", readOnly: true);
        (await client.Scripts.ExistsAsync(script.Sha1)).Should().Equal(false);
        (await client.Scripts.ExecuteStringAsync(script, ["key"])).Should().Be("value");
        (await client.Scripts.ExistsAsync(script.Sha1)).Should().Equal(true);
        foreach (var mode in new[] { ScriptFlushMode.Default, ScriptFlushMode.Sync, ScriptFlushMode.Async })
        {
            await client.Scripts.FlushAsync(mode);
            (await client.Scripts.ExistsAsync(script.Sha1)).Should().Equal(false);
            (await client.Scripts.LoadAsync(script)).Should().Be(script.Sha1);
        }
        var writes = RespireScript.Create("return redis.call('SET', KEYS[1], 'changed')", readOnly: true);
        Func<Task> execute = async () => { using var result = await client.Scripts.ExecuteAsync(writes, ["key"]); };
        await execute.Should().ThrowAsync<RespireServerException>();
        (await client.GetStringAsync("key")).Should().Be("value");
        using (var batch = client.CreateBatch())
        {
            var flush = batch.Scripts.Flush();
            var read = batch.Scripts.Evaluate(script, ["key"]);
            await batch.ExecuteAsync();
            flush.Result.Should().BeTrue();
            using var result = read.Result;
            result.AsString().Should().Be("value");
        }
        await using var transaction = client.CreateTransaction();
        var cleared = transaction.Scripts.Flush();
        var absent = transaction.Scripts.Exists(script.Sha1);
        var readOnly = transaction.Scripts.Evaluate(script, ["key"]);
        var loaded = transaction.Scripts.Load(script);
        var present = transaction.Scripts.Exists(script.Sha1);
        await transaction.CommitAsync();
        cleared.Result.Should().BeTrue();
        absent.Result.Should().Equal(false);
        using var final = readOnly.Result;
        final.AsString().Should().Be("value");
        loaded.Result.Should().Be(script.Sha1);
        present.Result.Should().Equal(true);
    }
}
