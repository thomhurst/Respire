using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class SingleItemCancellationTests
{
    [Test]
    public async Task DefaultCancellationAndExplicitSecondKeysPreserveWireShapes()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray());
        await using var connection = await FakeRespServer.ConnectClientAsync(server.Port);
        IRespireClient client = connection.WithKeyPrefix("tenant:");

        await client.DeleteAsync("key", default);
        await client.DeleteAsync("key", "other");
        await client.Keys.UnlinkAsync("key", default);
        await client.Keys.UnlinkAsync("key", "other");
        await client.Keys.TouchAsync("key", default);
        await client.Keys.TouchAsync("key", "other");
        await client.Sets.UnionStoreAsync("dest", "key", default);
        await client.Sets.UnionStoreAsync("dest", "key", "other");
        await client.Sets.IntersectCountAsync(2, "key", default);
        await client.Sets.IntersectCountAsync(2, "key", "other");
        await client.Sets.DifferenceCountAsync(2, "key", default);
        await client.Sets.DifferenceCountAsync(2, "key", "other");
        await client.Sets.UnionCountAsync(new RespireSetUnionCountOptions(2), "key", default);
        await client.Sets.UnionCountAsync(new RespireSetUnionCountOptions(2), "key", "other");
        await client.SortedSets.IntersectCountAsync("key", default);
        await client.SortedSets.IntersectCountAsync("key", "other");
        await client.HyperLogLog.CountAsync("key", default);
        await client.HyperLogLog.CountAsync("key", "other");
        await client.Bitmaps.OperateAsync(RespireBitOperation.Or, "dest", "key", default);
        await client.Bitmaps.OperateAsync(RespireBitOperation.Or, "dest", "key", "other");
        // An explicitly typed default remains a real (empty) second key.
        await client.DeleteAsync("key", default(RespireKey));

        await Assert.That(server.ReceivedCommands).IsEquivalentTo([
            "DEL tenant:key", "DEL tenant:key tenant:other",
            "UNLINK tenant:key", "UNLINK tenant:key tenant:other",
            "TOUCH tenant:key", "TOUCH tenant:key tenant:other",
            "SUNIONSTORE tenant:dest tenant:key", "SUNIONSTORE tenant:dest tenant:key tenant:other",
            "SINTERCARD 1 tenant:key LIMIT 2", "SINTERCARD 2 tenant:key tenant:other LIMIT 2",
            "SDIFFCARD 1 tenant:key LIMIT 2", "SDIFFCARD 2 tenant:key tenant:other LIMIT 2",
            "SUNIONCARD 1 tenant:key LIMIT 2", "SUNIONCARD 2 tenant:key tenant:other LIMIT 2",
            "ZINTERCARD 1 tenant:key", "ZINTERCARD 2 tenant:key tenant:other",
            "PFCOUNT tenant:key", "PFCOUNT tenant:key tenant:other",
            "BITOP OR tenant:dest tenant:key", "BITOP OR tenant:dest tenant:key tenant:other",
            "DEL tenant:key tenant:",
        ], CollectionOrdering.Matching);
    }

    [Test]
    public async Task GetManyDefaultCancellationPreservesSingleAndMultipleKeys()
    {
        var oneReply = "*1\r\n$1\r\n7\r\n"u8.ToArray();
        var twoReply = "*2\r\n$1\r\n7\r\n$1\r\n9\r\n"u8.ToArray();
        await using var server = new FakeRespServer(oneReply, twoReply, oneReply, twoReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var single = await client.Strings.GetManyAsync("a", default);
        var multiple = await client.Strings.GetManyAsync("a", "b");
        var typedSingle = await client.Strings.GetManyAsync<int>("a", default);
        var typedMultiple = await client.Strings.GetManyAsync<int>("a", "b");

        await Assert.That(single).IsEquivalentTo(new string?[] { "7" });
        await Assert.That(multiple).IsEquivalentTo(new string?[] { "7", "9" });
        await Assert.That(typedSingle.Length).IsEqualTo(1);
        await Assert.That(typedSingle[0]).IsEqualTo(7);
        await Assert.That(typedMultiple.Length).IsEqualTo(2);
        await Assert.That(typedMultiple[0]).IsEqualTo(7);
        await Assert.That(typedMultiple[1]).IsEqualTo(9);

        await Assert.That(server.ReceivedCommands).IsEquivalentTo(
            ["MGET a", "MGET a b", "MGET a", "MGET a b"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("script")]
    [Arguments("script-span")]
    [Arguments("function")]
    [Arguments("function-span")]
    public async Task SingleKeyLeasesPreservePrefixOwnershipAndCancellation(string operation)
    {
        await using var server = new FakeRespServer("*1\r\n$5\r\nvalue\r\n"u8.ToArray());
        await using var connection = await FakeRespServer.ConnectClientAsync(server.Port);
        IRespireClient client = connection.WithKeyPrefix("tenant:");
        var script = RespireScript.Create("return {ARGV[1]}");
        var function = RespireFunction.Create("single");
        using var cancellation = new CancellationTokenSource();

        ValueTask<RespireResult> Execute(CancellationToken token) => operation switch
        {
            "script" => client.Scripts.ExecuteLeaseAsync(script, "key", ["value"], token),
            "script-span" => client.Scripts.ExecuteLeaseSpanAsync(script, "key", ["value"], token),
            "function" => client.Functions.ExecuteLeaseAsync(function, "key", ["value"], token),
            _ => client.Functions.ExecuteLeaseSpanAsync(function, "key", ["value"], token),
        };

        using var result = await Execute(cancellation.Token);
        var child = result[0];
        await Assert.That(child.AsString()).IsEqualTo("value");
        var arguments = server.ReceivedArguments.Last();
        await Assert.That(arguments.Select(System.Text.Encoding.UTF8.GetString).ToArray())
            .IsEquivalentTo(operation.StartsWith("script", StringComparison.Ordinal)
                ? new[] { "EVALSHA", script.Sha1, "1", "tenant:key", "value" }
                : new[] { "FCALL", "single", "1", "tenant:key", "value" }, CollectionOrdering.Matching);
        result.Dispose();
        await Assert.That(child.IsDisposed).IsTrue();
        await Assert.That(() => child.AsString()).ThrowsExactly<ObjectDisposedException>();

        cancellation.Cancel();
        var error = await Assert.That(async () =>
        {
            using var cancelled = await Execute(cancellation.Token);
        }).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
    }

    [Test]
    public async Task HashExpiryPreservesScalarAndCollectionResults()
    {
        await using var server = new FakeRespServer("*1\r\n:123\r\n"u8.ToArray());
        await using var connection = await FakeRespServer.ConnectClientAsync(server.Port);
        IRespireClient client = connection.WithKeyPrefix("tenant:");
        using var cancellation = new CancellationTokenSource();

        RespireTtlResult scalar = await client.Hashes.ExpiryAsync("hash", "field", cancellation.Token);
        RespireTtlResult binary = await client.Hashes.ExpiryAsync("hash", (RespireKey)"field", cancellation.Token);
        RespireTtlResult[] collection = await client.Hashes.ExpiryAsync("hash", (ReadOnlySpan<string>)["field"], cancellation.Token);

        await Assert.That(scalar).IsEqualTo(binary);
        await Assert.That(collection).IsEquivalentTo([scalar]);
        await Assert.That(server.ReceivedCommands.TakeLast(3)).IsEquivalentTo([
            "HPTTL tenant:hash FIELDS 1 field", "HPTTL tenant:hash FIELDS 1 field",
            "HPTTL tenant:hash FIELDS 1 field"]);
    }

    [Test]
    public async Task InterpolatedCommandsKeepTheirTokenOverloads()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray());
        await using var connection = await FakeRespServer.ConnectClientAsync(server.Port);
        IRespireClient client = connection;
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var key = "key with spaces";

        await connection.ExecuteFireAndForgetAsync($"FUNCTION FLUSH", token);
        await client.ExecuteFireAndForgetAsync($"FUNCTION FLUSH", token);
        await connection.ExecuteFireAndForgetAsync($"DEL {key}", token);
        await client.ExecuteFireAndForgetAsync($"DEL {key}", token);
        using var concrete = await connection.ExecuteAsync($"PING", cancellationToken: token);
        using var contract = await client.ExecuteAsync($"PING", cancellationToken: token);
        // A reply fences the preceding fire-and-forget writes.
        await Assert.That(concrete.AsInteger()).IsEqualTo(1);
        await Assert.That(contract.AsInteger()).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands.TakeLast(6)).IsEquivalentTo([
            "FUNCTION FLUSH", "FUNCTION FLUSH", "DEL key with spaces", "DEL key with spaces", "PING", "PING"]);
        foreach (var arguments in server.ReceivedArguments.TakeLast(4).Take(2))
        {
            await Assert.That(arguments.Length).IsEqualTo(2);
            await Assert.That(System.Text.Encoding.UTF8.GetString(arguments[1])).IsEqualTo(key);
        }
    }

    [Test]
    public async Task SortedSetPopOverloadsPreserveExistingReturnTypes()
    {
        var compileControl = CompileSortedSetPopCalls;
        await Assert.That(compileControl).IsNotNull();
    }

    private static void CompileSortedSetPopCalls(IRespireSortedSetCommands commands, CancellationToken token)
    {
        ValueTask<RespireSortedSetEntry?> scalar = commands.PopAsync("key", default);
        ValueTask<RespireSortedSetEntry<int>?> typedScalar = commands.PopAsync<int>("key", default);
        // Existing single-key calls must keep choosing ZPOPMIN/ZPOPMAX and their array result.
        ValueTask<RespireSortedSetEntry[]> existing = commands.PopManyAsync("key", count: 2);
        ValueTask<RespireSortedSetEntry<int>[]> typed = commands.PopManyAsync<int>("key", count: 2, cancellationToken: token);
        // ZMPOP/BZMPOP retain the key-bearing result and require their extra options explicitly.
        ValueTask<RespireSortedSetPopManyResult?> multi = commands.PopManyAsync("key", 2, false, null, token);
        ValueTask<RespireSortedSetPopManyResult<int>?> typedMulti = commands.PopManyAsync<int>("key", 2, false, TimeSpan.FromSeconds(1), token);
        ValueTask<RespireSortedSetPopResult?> blocking = commands.PopAsync("key", TimeSpan.FromSeconds(1), false, token);
        ValueTask<RespireSortedSetPopResult<int>?> typedBlocking = commands.PopAsync<int>("key", TimeSpan.FromSeconds(1), false, token);
        _ = (scalar, typedScalar, existing, typed, multi, typedMulti, blocking, typedBlocking);
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
        await Assert.That(await client.Bitmaps.OperateAsync(RespireBitOperation.Or, "dest", "key", token)).IsEqualTo(1);
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
