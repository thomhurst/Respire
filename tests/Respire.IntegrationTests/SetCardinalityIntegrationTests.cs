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
        var estimate = await Count(client, mode, true, ["first", "second"], approximate: true);
        var limitedEstimate = await Count(client, mode, true, ["first", "second"], 2, approximate: true);
        if (fake)
        {
            estimate.Should().Be(5);
            limitedEstimate.Should().Be(2);
        }
        else
        {
            // Redis uses a probabilistic estimate; LIMIT still caps the result.
            estimate.Should().BeGreaterThan(0);
            limitedEstimate.Should().BeInRange(1, 2);
        }
        (await Count(client, mode, true, ["missing"], approximate: true)).Should().Be(0);
        // Existing intersection counts retain their previous argument layout and behavior.
        (await client.Sets.IntersectCountAsync("first", "second")).Should().Be(1);
        await client.SetAsync("wrong-type", "value");
        foreach (var union in new[] { false, true })
        {
            Func<Task> read = async () => await Count(client, mode, union, ["wrong-type", "first"]);
            await read.Should().ThrowAsync<RespireServerException>();
        }
    }

    /// <summary>Checks the new commands against Redis 8.10 key metadata, including optional arguments.</summary>
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task Redis810DiscoversCountedKeysAndReportsReadOnlyCommands(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString)
            with { Protocol = (RespProtocol)protocol });
        foreach (var command in new[] { "SDIFFCARD", "SUNIONCARD" })
        {
            using var metadata = await client.ExecuteAsync("COMMAND", "INFO", command);
            var flags = metadata[0][2];
            Enumerable.Range(0, flags.Count).Select(index => flags[index].AsString()).Should().Contain("readonly");
            RespireValue[] arguments = command == "SUNIONCARD"
                ? ["2", "key-a", "key-b", "APPROX", "LIMIT", "3"]
                : ["2", "key-a", "key-b", "LIMIT", "3"];
            var layout = Respire.Commands.RawCommandKeyLayouts.GetDeferredLayout(command, arguments);
            using var keys = await client.ExecuteAsync("COMMAND", ["GETKEYS", command, .. arguments]);
            var selected = Enumerable.Range(0, layout.Count)
                .Select(index => arguments[layout.Start + index * layout.Stride].ToString()).ToArray();
            selected.Should().Equal(Enumerable.Range(0, keys.Count).Select(index => keys[index].AsString()));
            layout.Extra.Should().Be(-1);
        }
    }

    /// <summary>Checks that cardinality reads preserve WATCH and real mutations still invalidate it.</summary>
    [Test]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task CardinalityReadsPreserveWatch(int protocol, bool fake)
    {
        await using var server = fake ? new RespireFakeServer() : null;
        var options = (server?.CreateOptions() ?? RespireOptions.Parse(fixture.ConnectionString))
            with { Protocol = (RespProtocol)protocol };
        await using var watcher = await RespireClient.ConnectAsync(options);
        await using var writer = await RespireClient.ConnectAsync(options);
        var first = new RespireKey($"set-watch:{Guid.NewGuid():N}:first");
        var second = new RespireKey($"set-watch:{Guid.NewGuid():N}:second");
        await writer.Sets.AddAsync(first, "a", "b");
        await writer.Sets.AddAsync(second, "b", "c");
        await using (var transaction = await watcher.CreateTransactionAsync([first, second]))
        {
            (await writer.Sets.DifferenceCountAsync(first, second)).Should().Be(1);
            (await writer.Sets.UnionCountAsync(new RespireSetUnionCountOptions(2, true), first, second)).Should().BeInRange(1, 2);
            var pending = transaction.Sets.Count(first);
            (await transaction.CommitAsync()).Should().BeTrue();
            pending.Result.Should().Be(2);
        }
        await using (var transaction = await watcher.CreateTransactionAsync([first, second]))
        {
            var pending = transaction.Sets.Count(first);
            await writer.Sets.AddAsync(second, "changed");
            (await transaction.CommitAsync()).Should().BeFalse();
            pending.Status.Should().Be(RespirePendingStatus.Aborted);
        }
    }

    /// <summary>Checks malformed numeric arguments return server errors without breaking the connection.</summary>
    [Test]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task MalformedNumericArgumentsReturnServerErrors(int protocol, bool fake)
    {
        await using var server = fake ? new RespireFakeServer() : null;
        var options = (server?.CreateOptions() ?? RespireOptions.Parse(fixture.ConnectionString))
            with { Protocol = (RespProtocol)protocol };
        await using var client = await RespireClient.ConnectAsync(options);
        foreach (var command in new[] { "SDIFFCARD", "SUNIONCARD" })
        {
            foreach (var invalid in new[] { "bad", "9223372036854775808" })
            {
                Func<Task> invalidCount = async () => { using var reply = await client.ExecuteAsync(command, invalid, "key"); };
                Func<Task> invalidLimit = async () => { using var reply = await client.ExecuteAsync(command, "1", "key", "LIMIT", invalid); };
                await invalidCount.Should().ThrowAsync<RespireServerException>();
                await invalidLimit.Should().ThrowAsync<RespireServerException>();
                using var pong = await client.ExecuteAsync("PING");
                pong.AsString().Should().Be("PONG");
            }
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
