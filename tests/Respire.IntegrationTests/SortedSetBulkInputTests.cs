using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class SortedSetBulkInputTests(RedisTestContainer fixture)
{
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task BulkAdd_PreservesBinaryMembersAndScores(int mode)
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var key = $"sorted:binary:{Guid.NewGuid():N}";
        byte[] first = [0xff, 0x00, 0x80];
        byte[] second = [0xfe, 0x00, 0x80];
        byte[] padded = [0x42, 0xff, 0x00, 0x80, 0x43];
        (RespireValue Member, double Score)[] entries =
        [
            (padded.AsMemory(1, 3), -10.5),
            (second, 1.25),
            (ReadOnlyMemory<byte>.Empty, 0),
            ("東京", 3.75),
            (42, 4.5),
        ];

        long added;
        if (mode == 0)
        {
            using var cancellation = new CancellationTokenSource();
            added = await client.SortedSets.AddAsync(key, entries, cancellation.Token);
        }
        else if (mode == 1)
        {
            var batch = client.CreateBatch();
            var pending = batch.SortedSets.Add(key, entries);
            await batch.ExecuteAsync();
            added = pending.Result;
        }
        else
        {
            var transaction = client.CreateTransaction();
            var pending = transaction.SortedSets.Add(key, entries);
            await transaction.CommitAsync();
            added = pending.Result;
        }

        added.Should().Be(5);
        (await client.SortedSets.CountAsync(key)).Should().Be(5);
        (await client.SortedSets.ScoresManyAsync(key, first, second, "", "東京", 42))
            .Should().Equal(-10.5, 1.25, 0, 3.75, 4.5);

        var results = await client.SortedSets.RangeWithScoresAsync<byte[]>(key);
        results.Select(entry => entry.Score).Should().Equal(-10.5, 0, 1.25, 3.75, 4.5);
        results[0].Member.Should().Equal(first);
        results[1].Member.Should().BeEmpty();
        results[2].Member.Should().Equal(second);
        results[3].Member.Should().Equal("東京"u8.ToArray());
        results[4].Member.Should().Equal("42"u8.ToArray());

        // A single tuple must bind to the bulk params overload, including binary updates.
        (await client.SortedSets.AddAsync(key, (first, 9.5))).Should().Be(0);
        (await client.SortedSets.ScoreAsync(key, first)).Should().Be(9.5);
    }
}
