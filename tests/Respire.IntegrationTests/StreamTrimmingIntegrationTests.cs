using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class StreamTrimmingIntegrationTests(RedisTestContainer fixture)
{
    [Test]
    [Arguments(2, 0, false, false)]
    [Arguments(2, 0, false, true)]
    [Arguments(2, 0, true, false)]
    [Arguments(2, 0, true, true)]
    [Arguments(2, 1, false, false)]
    [Arguments(2, 1, false, true)]
    [Arguments(2, 1, true, false)]
    [Arguments(2, 1, true, true)]
    [Arguments(2, 2, false, false)]
    [Arguments(2, 2, false, true)]
    [Arguments(2, 2, true, false)]
    [Arguments(2, 2, true, true)]
    [Arguments(3, 0, false, false)]
    [Arguments(3, 0, false, true)]
    [Arguments(3, 0, true, false)]
    [Arguments(3, 0, true, true)]
    [Arguments(3, 1, false, false)]
    [Arguments(3, 1, false, true)]
    [Arguments(3, 1, true, false)]
    [Arguments(3, 1, true, true)]
    [Arguments(3, 2, false, false)]
    [Arguments(3, 2, false, true)]
    [Arguments(3, 2, true, false)]
    [Arguments(3, 2, true, true)]
    public async Task ExactAndApproximateTrimmingRoundTrip(int protocol, int surface, bool minId, bool approximate)
    {
        await using var client = await RespireClient.ConnectAsync(
            RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });
        var prefix = $"trim:{Guid.NewGuid():N}:";
        var view = client.WithKeyPrefix(prefix);
        var key = $"events:{minId}:{approximate}";
        try
        {
            // More than one macro node makes approximate eviction observable without depending on its exact size.
            using (var seed = view.CreateBatch())
            {
                for (var id = 1; id <= 300; id++)
                    _ = seed.Streams.Add(key, new StreamAddOptions { Id = $"{id}-0" }, ("value", id));
                await seed.ExecuteAsync();
            }
            var trim = new StreamTrimOptions { MaxLength = minId ? null : 150,
                MinId = minId ? new RespireStreamId("151-0") : (RespireStreamId?)null, Approximate = approximate,
                Limit = approximate ? 0 : null };
            var add = new StreamAddOptions { Id = "301-0", MaxLength = minId ? null : 200,
                MinId = minId ? new RespireStreamId("101-0") : (RespireStreamId?)null, ApproximateTrim = approximate,
                Limit = approximate ? 1000 : null };
            long removed, countBeforeTrim;
            if (surface == 0)
            {
                (await view.Streams.AddAsync(key, add, ("value", 301))).Should().Be((RespireStreamId)"301-0");
                countBeforeTrim = await view.Streams.CountAsync(key);
                removed = await view.Streams.TrimAsync(key, trim);
            }
            else
            {
                using var batch = surface == 1 ? view.CreateBatch() : null;
                await using var transaction = surface == 2 ? view.CreateTransaction() : null;
                IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
                var added = queue.Streams.Add(key, add, ("value", 301));
                var count = queue.Streams.Count(key);
                var trimmed = queue.Streams.Trim(key, trim);
                if (transaction is not null) await transaction.CommitAsync();
                else await batch!.ExecuteAsync();
                added.Result.Should().Be((RespireStreamId)"301-0");
                removed = trimmed.Result;
                countBeforeTrim = count.Result;
            }
            var entries = await view.Streams.RangeAsync(key);
            removed.Should().Be(countBeforeTrim - entries.Length);
            if (approximate)
            {
                entries.Length.Should().BeInRange(minId ? 151 : 150, 301);
                // XADD has already removed at least one complete node.
                entries.Length.Should().BeLessThan(301);
            }
            else
            {
                entries.Length.Should().Be(minId ? 151 : 150);
                entries[0].Id.Should().Be((RespireStreamId)(minId ? "151-0" : "152-0"));
                removed.Should().Be(50);
            }
            entries[^1].Id.Should().Be((RespireStreamId)"301-0");
            (await client.Keys.ExistsAsync(prefix + key)).Should().BeTrue();
            (await client.Keys.ExistsAsync(prefix + prefix + key)).Should().BeFalse();
        }
        finally
        {
            await view.Keys.DeleteAsync(key);
        }
    }
}
