using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.PerTestSession)]
public class StringLcsIndexIntegrationTests(ModernRedisTestContainer fixture)
{
    [Test]
    [Arguments("immediate", 2)]
    [Arguments("batch", 2)]
    [Arguments("transaction", 2)]
    [Arguments("immediate", 3)]
    [Arguments("batch", 3)]
    [Arguments("transaction", 3)]
    public async Task RangesFilteringAndTotalLength_AgreeAcrossExecutionModes(string mode, int protocol)
    {
        await using var root = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString)
            with { Protocol = (RespProtocol)protocol });
        var client = root.WithKeyPrefix($"lcs:{Guid.NewGuid():N}:");
        await client.SetAsync("first", "ohmytext");
        await client.SetAsync("second", "mynewtext");
        var plain = await Index(client, mode, "first", "second");
        plain.Length.Should().Be(6);
        plain.Matches.Should().Equal(new RespireLcsMatch(new(4, 7), new(5, 8), null),
            new RespireLcsMatch(new(2, 3), new(0, 1), null));
        var withLengths = await Index(client, mode, "first", "second", new() { IncludeMatchLength = true });
        withLengths.Matches.Should().Equal(new RespireLcsMatch(new(4, 7), new(5, 8), 4),
            new RespireLcsMatch(new(2, 3), new(0, 1), 2));
        var filtered = await Index(client, mode, "first", "second",
            new() { MinimumMatchLength = 4, IncludeMatchLength = true });
        filtered.Length.Should().Be(6);
        filtered.Matches.Should().Equal(new RespireLcsMatch(new(4, 7), new(5, 8), 4));
        var absent = await Index(client, mode, "first", "missing");
        absent.Length.Should().Be(0);
        absent.Matches.Should().BeEmpty();
        var allFiltered = await Index(client, mode, "first", "second", new() { MinimumMatchLength = 7 });
        allFiltered.Length.Should().Be(6);
        allFiltered.Matches.Should().BeEmpty();
        (await client.Strings.LcsAsync("first", "second")).Should().Be("mytext");
        (await client.Strings.LcsLengthAsync("first", "second")).Should().Be(6);
        await client.DeleteAsync("first", "second");
    }

    private static async Task<RespireLcsIndexResult> Index(IRespireClient client, string mode,
        RespireKey first, RespireKey second, RespireLcsOptions? options = null)
    {
        if (mode == "immediate") return await client.Strings.LcsIndexAsync(first, second, options);
        if (mode == "batch")
        {
            using var batch = client.CreateBatch();
            var pending = batch.Strings.LcsIndex(first, second, options);
            await batch.ExecuteAsync();
            return pending.Result;
        }
        await using var transaction = client.CreateTransaction();
        var result = transaction.Strings.LcsIndex(first, second, options);
        await transaction.CommitAsync();
        return result.Result;
    }
}
