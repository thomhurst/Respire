using FluentAssertions;
using Testcontainers.Redis;
using TUnit.Core;

namespace Respire.IntegrationTests;

public class FunctionIntegrationTests
{
    private const string Source = """
        #!lua name=sample
        redis.register_function{function_name='echo', callback=function(keys,args) return args[1] end, flags={'no-writes'}, description='binary echo'}
        redis.register_function{function_name='read', callback=function(keys,args) return redis.call('GET',keys[1]) end, flags={'no-writes'}}
        redis.register_function('write', function(keys,args) return redis.call('INCR',keys[1]) end)
        redis.register_function{function_name='owned', callback=function(keys,args) return {args[1],{args[1]}} end, flags={'no-writes'}}
        """;

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task AdministrationAndDeferredCallsRoundTrip(int protocol)
    {
        await using var server = new RedisBuilder("redis:7.0.15").Build();
        await server.StartAsync();
        await using var owner = await RespireClient.ConnectAsync($"redis://{server.Hostname}:{server.GetMappedPublicPort(6379)}?protocol={protocol}");
        var client = owner.WithKeyPrefix("tenant:");
        var library = RespireFunctionLibrary.Create(Source);
        (await client.Functions.LoadAsync(library)).Should().Be("sample");
        var info = (await client.Functions.ListAsync("sam*", withCode: true)).Single();
        info.Name.Should().Be("sample"); info.Engine.Should().Be("LUA"); info.Code.Should().Be(Source);
        info.Functions.Single(f => f.Name == "echo").Description.Should().Be("binary echo");
        info.Functions.Single(f => f.Name == "echo").Flags.Should().Contain("no-writes");
        (await client.Functions.ListAsync("sample")).Single().Code.Should().BeNull();
        var stats = await client.Functions.StatsAsync();
        stats.RunningFunction.Should().BeNull();
        stats.Engines["LUA"].Should().Be(new RespireFunctionEngineStats(1, 4));
        byte[] binary = [0, 255, 128, 13, 10];
        (await client.Functions.ExecuteAsync<byte[]>(library.Function("echo", true), args: [binary])).Should().Equal(binary);
        (await client.Functions.ExecuteAsync<Record>(library.Function("echo", true), args: ["{\"Value\":42}"])).Should().Be(new Record(42));
        await client.SetAsync("key", "value");
        (await client.Functions.ExecuteStringAsync(library.Function("read", true), ["key"])).Should().Be("value");
        (await client.Functions.ExecuteStringAsync(library.Function("read", true), ["missing"])).Should().BeNull();
        (await client.Functions.ExecuteIntegerAsync(library.Function("write"), ["counter"])).Should().Be(1);
        (await owner.Keys.ExistsAsync("counter")).Should().BeFalse();
        var dump = await client.Functions.DumpAsync();
        var saved = dump.ToArray();
        (await client.Functions.DeleteAsync("sample")).Should().BeTrue();
        (await client.Functions.ListAsync()).Should().BeEmpty();
        (await client.Functions.RestoreAsync(dump)).Should().BeTrue();
        foreach (var policy in new[] { FunctionRestorePolicy.Replace, FunctionRestorePolicy.Flush })
            (await client.Functions.RestoreAsync(dump, policy)).Should().BeTrue();
        Func<Task> collision = async () => { await client.Functions.RestoreAsync(dump); };
        await collision.Should().ThrowAsync<RespireServerException>();
        Func<Task> invalid = async () => { await client.Functions.RestoreAsync(new byte[] { 0, 255 }); };
        await invalid.Should().ThrowAsync<RespireServerException>();
        (await client.Functions.LoadAsync(Source, replace: true)).Should().Be("sample");
        foreach (var mode in Enum.GetValues<FunctionFlushMode>())
        {
            (await client.Functions.FlushAsync(mode)).Should().BeTrue();
            (await client.Functions.ListAsync()).Should().BeEmpty();
            await client.Functions.RestoreAsync(dump);
        }
        foreach (var transaction in new[] { false, true })
        {
            RespireResult owned;
            byte[] deferredDump;
            using (var batch = client.CreateBatch())
            {
                await using var tx = client.CreateTransaction();
                var functions = transaction ? tx.Functions : batch.Functions;
                var flushed = functions.Flush(FunctionFlushMode.Sync);
                var loaded = functions.Load(library);
                var listed = functions.List("sample", true);
                var execution = functions.Execute(library.Function("owned", true), args: [binary]);
                var integer = functions.ExecuteInteger(library.Function("write"), ["counter"]);
                var text = functions.ExecuteString(library.Function("echo", true), args: ["text"]);
                var typed = functions.Execute<Record>(library.Function("echo", true), args: ["{\"Value\":42}"]);
                var dumped = functions.Dump();
                var observed = functions.Stats();
                var deleted = functions.Delete("sample");
                var restored = functions.Restore(dump);
                if (transaction) await tx.CommitAsync(); else await batch.ExecuteAsync();
                flushed.Result.Should().BeTrue(); loaded.Result.Should().Be("sample"); listed.Result.Single().Code.Should().Be(Source);
                owned = execution.Result; deferredDump = dumped.Result;
                integer.Result.Should().BeGreaterThan(1); text.Result.Should().Be("text"); typed.Result.Should().Be(new Record(42));
                observed.Result.Engines["LUA"].FunctionsCount.Should().Be(4); deleted.Result.Should().BeTrue(); restored.Result.Should().BeTrue();
            }
            using (owned) { owned[0].AsBytes().Should().Equal(binary); owned[1][0].AsBytes().Should().Equal(binary); }
            deferredDump.Should().Equal(saved);
        }
        dump.Should().Equal(saved);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ReusableLibrariesReloadConcurrentlyAndRespectReplacement(int protocol)
    {
        await using var server = new RedisBuilder("redis:7.0.15").Build();
        await server.StartAsync();
        await using var client = await RespireClient.ConnectAsync($"redis://{server.Hostname}:{server.GetMappedPublicPort(6379)}?protocol={protocol}");
        var library = RespireFunctionLibrary.Create(Source);
        var function = library.Function("write");
        foreach (var round in new[] { 0, 1 })
        {
            if (round == 1) await client.Functions.FlushAsync();
            var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => client.Functions.ExecuteIntegerAsync(function, ["counter"]).AsTask()));
            results.Distinct().Should().HaveCount(12);
        }
        (await client.GetAsync<long>("counter")).Should().Be(24);
        var replacement = Source + "\nredis.register_function('extra', function() return 99 end)";
        var conflicting = RespireFunctionLibrary.Create(replacement).Function("extra");
        Func<Task> refused = async () => { await client.Functions.ExecuteIntegerAsync(conflicting); };
        await refused.Should().ThrowAsync<RespireServerException>();
        (await client.Functions.ListAsync("sample", true)).Single().Code.Should().Be(Source);
        var allowed = RespireFunctionLibrary.Create(replacement, replace: true).Function("extra");
        (await client.Functions.ExecuteIntegerAsync(allowed)).Should().Be(99);
        var missing = RespireFunctionLibrary.Create(replacement).Function("undefined");
        Func<Task> absent = async () => { await client.Functions.ExecuteIntegerAsync(missing); };
        (await absent.Should().ThrowAsync<RespireServerException>()).Which.Message.Should().Be("ERR Function not found");
        Func<Task> readOnlyWrite = async () => { await client.Functions.ExecuteIntegerAsync(library.Function("write", true), ["counter"]); };
        await readOnlyWrite.Should().ThrowAsync<RespireServerException>();
        (await client.GetAsync<long>("counter")).Should().Be(24);
        // Restart loses non-persisted libraries; a missing-function reply triggers the same bounded reload.
        await client.Functions.FlushAsync();
        using var batch = client.CreateBatch();
        var pending = batch.Functions.ExecuteInteger(function, ["counter"]);
        var following = batch.Increment("counter");
        var result = await batch.TryExecuteAsync();
        result.Failures.Should().HaveCount(1);
        Action failed = () => _ = pending.Result;
        failed.Should().Throw<RespireServerException>();
        following.Result.Should().Be(25);
        (await client.Functions.ListAsync()).Should().BeEmpty();
        await using var tx = client.CreateTransaction();
        var txMissing = tx.Functions.ExecuteInteger(function, ["counter"]);
        var txFollowing = tx.Increment("counter");
        await tx.CommitAsync();
        Action txFailed = () => _ = txMissing.Result;
        txFailed.Should().Throw<RespireServerException>();
        txFollowing.Result.Should().Be(26);
        (await client.Functions.ListAsync()).Should().BeEmpty();
    }

    public sealed record Record(int Value);
}
