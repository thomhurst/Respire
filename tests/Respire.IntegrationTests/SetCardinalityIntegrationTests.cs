using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

public sealed class Redis810SetTestContainer() : StandaloneRedisTestContainer("redis:8.10-alpine");

[ClassDataSource<Redis810SetTestContainer>(Shared = SharedType.PerTestSession)]
public class SetCardinalityIntegrationTests(Redis810SetTestContainer fixture)
{
    [Test]
    [Arguments("immediate", 2, false)]
    [Arguments("batch", 2, false)]
    [Arguments("transaction", 2, false)]
    [Arguments("immediate", 3, false)]
    [Arguments("batch", 3, false)]
    [Arguments("transaction", 3, false)]
    [Arguments("immediate", 2, true)]
    [Arguments("batch", 2, true)]
    [Arguments("transaction", 2, true)]
    [Arguments("immediate", 3, true)]
    [Arguments("batch", 3, true)]
    [Arguments("transaction", 3, true)]
    public async Task RealAndFakeCountsRespectLimitsAndSetSemantics(string mode, int protocol, bool fake)
    {
        await using var server = new RespireFakeServer();
        var options = fake ? server.CreateOptions() : RespireOptions.Parse(fixture.ConnectionString);
        await using var root = await RespireClient.ConnectAsync(options with { Protocol = (RespProtocol)protocol });
        var client = root.WithKeyPrefix($"set-cardinality:{Guid.NewGuid():N}:");
        await client.Sets.AddAsync("first", "a", "b", "c");
        await client.Sets.AddAsync("second", "c", "d", "e");
        (await Count(client, mode, false, ["first", "second"])).Should().Be(2);
        (await Count(client, mode, false, ["first", "second"], 1)).Should().Be(1);
        (await Count(client, mode, false, ["first", "second"], long.MaxValue)).Should().Be(2);
        (await Count(client, mode, false, ["first", "first"])).Should().Be(0);
        (await Count(client, mode, false, ["missing", "first"])).Should().Be(0);
        (await Count(client, mode, false, ["first", "missing"])).Should().Be(3);
        (await Count(client, mode, true, ["first", "second"])).Should().Be(5);
        (await Count(client, mode, true, ["first", "first"])).Should().Be(3);
        (await Count(client, mode, true, ["missing", "first"])).Should().Be(3);
        (await Count(client, mode, true, ["missing"])).Should().Be(0);
        (await Count(client, mode, true, ["first", "second"], 2)).Should().Be(2);
        (await Count(client, mode, true, ["first", "second"], approximate: true)).Should().Be(5);
        (await Count(client, mode, true, ["first", "second"], 2, approximate: true)).Should().Be(2);
        // Existing intersection counts retain their previous argument layout and behavior.
        (await client.Sets.IntersectCountAsync("first", "second")).Should().Be(1);
        await client.SetAsync("wrong-type", "value");
        foreach (var union in new[] { false, true })
        {
            Func<Task> read = async () => await Count(client, mode, union, ["wrong-type", "first"]);
            await read.Should().ThrowAsync<RespireServerException>();
        }
    }

    [Test]
    public async Task CachedCountsInvalidateWhenEitherInputChanges()
    {
        await using var root = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString)
            with { Protocol = RespProtocol.Resp3, ClientSideCache = new() });
        var client = root.WithKeyPrefix($"set-count-cache:{Guid.NewGuid():N}:");
        await client.Sets.AddAsync("first", "a", "b");
        await client.Sets.AddAsync("second", "b", "c");
        for (var iteration = 0; iteration < 2; iteration++)
        {
            (await client.Sets.DifferenceCountAsync("first", "second")).Should().Be(1);
            (await client.Sets.UnionCountAsync("first", "second")).Should().Be(3);
            (await client.Sets.UnionCountAsync(new RespireSetUnionCountOptions(1), "first", "second")).Should().Be(1);
        }
        await client.Sets.AddAsync("second", "a", "d");
        (await client.Sets.DifferenceCountAsync("first", "second")).Should().Be(0);
        (await client.Sets.UnionCountAsync("first", "second")).Should().Be(4);
        await client.Sets.AddAsync("first", "e");
        (await client.Sets.DifferenceCountAsync("first", "second")).Should().Be(1);
        (await client.Sets.UnionCountAsync("first", "second")).Should().Be(5);
    }

    private static async Task<long> Count(IRespireClient client, string mode, bool union, RespireKey[] keys,
        long limit = 0, bool approximate = false)
    {
        if (mode == "immediate") return union ? await client.Sets.UnionCountAsync(new(limit, approximate), keys)
            : await client.Sets.DifferenceCountAsync(limit, keys);
        using var batch = mode == "batch" ? client.CreateBatch() : null;
        await using var transaction = mode == "transaction" ? client.CreateTransaction() : null;
        IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
        var pending = union ? queue.Sets.UnionCount(new(limit, approximate), keys) : queue.Sets.DifferenceCount(limit, keys);
        if (transaction is not null) await transaction.CommitAsync();
        else await batch!.ExecuteAsync();
        return pending.Result;
    }
}
