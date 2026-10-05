using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class FakeSortedSetServerTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task MutationsKeepExpiryUntilTheLastMemberIsRemoved(int protocol)
    {
        var clock = new RespireFakeClock();
        await using var server = new RespireFakeServer(clock);
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
        await client.SortedSets.AddAsync("key", ("a", 1), ("b", 2));
        using (var expiry = await client.ExecuteAsync("PEXPIRE", "key", 1000)) { }
        await client.SortedSets.AddAsync("key", "a", 3);
        await client.SortedSets.IncrementAsync("key", "b", 2);
        await client.SortedSets.RemoveAsync("key", "a");
        await Assert.That(await Number(client, "PTTL", "key")).IsEqualTo(1000);
        clock.Advance(TimeSpan.FromMilliseconds(999));
        await Assert.That(await client.SortedSets.CountAsync("key")).IsEqualTo(1);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await Assert.That(await client.SortedSets.CountAsync("key")).IsEqualTo(0);
        foreach (var command in new[] { "ZREM", "ZPOPMIN", "ZPOPMAX", "ZREMRANGEBYRANK", "ZREMRANGEBYSCORE", "ZREMRANGEBYLEX" })
        {
            await client.SortedSets.AddAsync("key", "a", 1);
            using (var expiry = await client.ExecuteAsync("PEXPIRE", "key", 1000)) { }
            RespireValue[] args = command switch
            {
                "ZREM" => ["key", "a"],
                "ZPOPMIN" or "ZPOPMAX" => ["key"],
                "ZREMRANGEBYRANK" => ["key", 0, -1],
                "ZREMRANGEBYSCORE" => ["key", "-inf", "+inf"],
                _ => ["key", "-", "+"],
            };
            using (var removed = await client.ExecuteAsync(command, args)) { }
            await Assert.That(await client.ExistsAsync("key")).IsFalse();
            await client.SortedSets.AddAsync("key", "a", 1);
            await Assert.That(await Number(client, "PTTL", "key")).IsEqualTo(-1);
            await client.Keys.DeleteAsync("key");
        }
    }

    [Test]
    public async Task ConcurrentIncrementsAndPopsHaveSingleOwners()
    {
        await using var server = new RespireFakeServer();
        await using var first = await RespireClient.ConnectAsync(server.CreateOptions());
        await using var second = await RespireClient.ConnectAsync(server.CreateOptions());
        var increments = await Task.WhenAll(Enumerable.Range(0, 100).Select(index =>
            (index % 2 == 0 ? first : second).SortedSets.IncrementAsync("counter", "member", 1).AsTask()));
        await Assert.That(increments.Order()).IsEquivalentTo(Enumerable.Range(1, 100).Select(value => (double)value));
        await first.SortedSets.AddAsync("pops", Enumerable.Range(0, 100).Select(value => ((RespireValue)value, (double)value)).ToArray());
        var pops = await Task.WhenAll(Enumerable.Range(0, 100).Select(index =>
            (index % 2 == 0 ? first : second).SortedSets.PopAsync<int>("pops").AsTask()));
        await Assert.That(pops.Select(entry => entry!.Value.Member).Order()).IsEquivalentTo(Enumerable.Range(0, 100));
        await Assert.That(await first.ExistsAsync("pops")).IsFalse();
    }

    [Test]
    public async Task UnsupportedSortedSetVariantsFailWithoutChangingMembers()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        await client.SortedSets.AddAsync("key", "member", 1);
        foreach (var (command, args) in new (string, RespireValue[])[]
        {
            ("ZRANDMEMBER", ["key"]), ("ZSCAN", ["key", 0]), ("ZMPOP", [1, "key", "MIN"]),
            ("BZMPOP", [0, 1, "key", "MIN"]), ("BZPOPMIN", ["key", 0]), ("BZPOPMAX", ["key", 0]),
            ("ZUNION", [1, "key"]), ("ZINTER", [1, "key"]), ("ZDIFF", [1, "key"]),
            ("ZUNIONSTORE", ["destination", 1, "key"]), ("ZINTERSTORE", ["destination", 1, "key"]),
            ("ZDIFFSTORE", ["destination", 1, "key"]), ("ZRANGESTORE", ["destination", "key", 0, -1]),
        })
        {
            var error = await Assert.That(async () => { using var ignored = await client.ExecuteAsync(command, args); }).Throws<RespireServerException>();
            await Assert.That(error!.Message).Contains(command);
        }
        await Assert.That(async () => { using var ignored = await client.ExecuteAsync("ZRANK", "key", "member", "INVALID"); }).Throws<RespireServerException>();
        await Assert.That(await client.SortedSets.ScoreAsync("key", "member")).IsEqualTo(1);
        await Assert.That(await client.ExistsAsync("destination")).IsFalse();
    }

    private static async Task<long> Number(IRespireClient client, string command, params RespireValue[] args)
    {
        using var result = await client.ExecuteAsync(command, args);
        return result.AsInteger();
    }
}
