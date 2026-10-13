using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class SingleItemCancellationTests
{
    [Test]
    public async Task SortedSetPopOverloadsPreserveExistingReturnTypes()
    {
        var compileControl = CompileSortedSetPopCalls;
        await Assert.That(compileControl).IsNotNull();
    }

    private static void CompileSortedSetPopCalls(ISortedSetCommands commands, CancellationToken token)
    {
        // Existing single-key calls must keep choosing ZPOPMIN/ZPOPMAX and their array result.
        ValueTask<SortedSetEntry[]> existing = commands.PopManyAsync("key", count: 2);
        ValueTask<SortedSetEntry<int>[]> typed = commands.PopManyAsync<int>("key", count: 2, cancellationToken: token);
        // ZMPOP/BZMPOP retain the key-bearing result and require their extra options explicitly.
        ValueTask<RespireSortedSetPopManyResult?> multi = commands.PopManyAsync("key", 2, false, null, token);
        ValueTask<RespireSortedSetPopManyResult<int>?> typedMulti = commands.PopManyAsync<int>("key", 2, false, TimeSpan.FromSeconds(1), token);
        _ = (existing, typed, multi, typedMulti);
    }

    [Test]
    [Arguments(typeof(IRespireClient))]
    [Arguments(typeof(RespireClient))]
    public async Task SubscriptionsHaveOnlySingleAndSpanOverloads(Type type)
    {
        foreach (var name in new[] { "SubscribeAsync", "SubscribePatternAsync", "SubscribeShardedAsync" })
        {
            var methods = type.GetMethods().Where(method => method.Name == name).ToArray();
            await Assert.That(methods.Length).IsEqualTo(2);
            await Assert.That(methods.Select(method => method.GetParameters()[0].ParameterType))
                .IsEquivalentTo([typeof(RespireChannel), typeof(ReadOnlySpan<RespireChannel>)]);
            foreach (var method in methods)
            {
                var parameters = method.GetParameters();
                await Assert.That(parameters.Length).IsEqualTo(3);
                await Assert.That(parameters[1].ParameterType).IsEqualTo(typeof(RespireSubscriptionOptions));
                await Assert.That(parameters[2].ParameterType).IsEqualTo(typeof(CancellationToken));
                await Assert.That(parameters.Skip(1).All(parameter => parameter.IsOptional)).IsTrue();
            }
        }
        foreach (var name in new[] { "PublishAsync", "PublishShardedAsync" })
        {
            var method = type.GetMethods().Single(method => method.Name == name);
            await Assert.That(method.GetParameters()[0].ParameterType).IsEqualTo(typeof(RespireChannel));
        }
    }

    [Test]
    public async Task SingleItemsPreserveWireCommandsAndKeyPrefixes()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray());
        await using var connection = await FakeRespServer.ConnectClientAsync(server.Port);
        IRespireClient client = connection.WithKeyPrefix("tenant:");
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;

        await Assert.That(await client.DeleteAsync("key", token)).IsEqualTo(1);
        await Assert.That(await client.Keys.UnlinkAsync("key", token)).IsEqualTo(1);
        await Assert.That(await client.Keys.TouchAsync("key", token)).IsEqualTo(1);
        await Assert.That(await client.Sets.UnionStoreAsync("dest", "key", token)).IsEqualTo(1);
        await Assert.That(await client.SortedSets.IntersectCountAsync("key", token)).IsEqualTo(1);
        await Assert.That(await client.HyperLogLog.CountAsync("key", token)).IsEqualTo(1);
        await Assert.That(await client.Bitmaps.OperateAsync(BitOperation.Or, "dest", "key", token)).IsEqualTo(1);
        await Assert.That(await client.Lists.LeftPushAsync("key", "value", token)).IsEqualTo(1);
        await Assert.That(await client.Hashes.RemoveAsync("key", "field", token)).IsEqualTo(1);
        using var result = await client.ExecuteAsync("DEL", token, "key");
        await Assert.That(result.AsInteger()).IsEqualTo(1);

        await Assert.That(server.ReceivedCommands.TakeLast(10)).IsEquivalentTo([
            "DEL tenant:key", "UNLINK tenant:key", "TOUCH tenant:key", "SUNIONSTORE tenant:dest tenant:key",
            "ZINTERCARD 1 tenant:key", "PFCOUNT tenant:key", "BITOP OR tenant:dest tenant:key",
            "LPUSH tenant:key value", "HDEL tenant:key field", "DEL key"]);
    }

    [Test]
    [Arguments("delete")]
    [Arguments("set")]
    [Arguments("list")]
    [Arguments("hash")]
    [Arguments("raw")]
    [Arguments("forget")]
    public async Task SingleItemsForwardCancellation(string operation)
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray());
        await using var connection = await FakeRespServer.ConnectClientAsync(server.Port);
        IRespireClient client = connection;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var token = cancellation.Token;

        var error = await Assert.That(async () =>
        {
            switch (operation)
            {
                case "delete": await client.DeleteAsync("key", token); break;
                case "set": await client.Sets.UnionStoreAsync("dest", "key", token); break;
                case "list": await client.Lists.LeftPushAsync("key", "value", token); break;
                case "hash": await client.Hashes.RemoveAsync("key", "field", token); break;
                case "raw": using (await client.ExecuteAsync("DEL", token, "key")) { } break;
                default: await client.ExecuteFireAndForgetAsync("DEL", token, "key"); break;
            }
        }).Throws<OperationCanceledException>();

        await Assert.That(error!.CancellationToken).IsEqualTo(token);
        // The existing transport abandons the wait; cancellation does not promise to undo a write.
    }
}
