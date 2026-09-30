using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class FakeListServerTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task MutationsPreserveExpiryAndEmptyListsLoseTheirKey(int protocol)
    {
        var clock = new RespireFakeClock();
        await using var server = new RespireFakeServer(clock);
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
        await client.Lists.RightPushAsync("list", "a", "b", "c");
        await Number(client, "PEXPIRE", "list", 1000);
        await client.Lists.LeftPushAsync("list", "first");
        await client.Lists.RightPushIfExistsAsync("list", "last");
        await client.Lists.SetAsync("list", -1, "changed");
        await client.Lists.InsertAfterAsync("list", "a", "inserted");
        await Number(client, "LREM", "list", 1, "inserted");
        using (var trim = await client.ExecuteAsync("LTRIM", "list", 1, -1)) { }
        await client.Lists.LeftPopAsync("list");
        // Redis 7.2+ rejects the rank whose magnitude cannot fit in a signed long.
        await Assert.That(async () => { using var ignored = await client.ExecuteAsync("LPOS", "list", "b", "RANK", long.MinValue); })
            .Throws<RespireServerException>();
        await Assert.That(await Number(client, "PTTL", "list")).IsEqualTo(1000);
        clock.Advance(TimeSpan.FromMilliseconds(999));
        await Assert.That(await client.Lists.CountAsync("list")).IsEqualTo(3);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await Assert.That(await client.Lists.CountAsync("list")).IsEqualTo(0);
        await client.SetAsync("list", "new type");
        await Assert.That(await client.GetStringAsync("list")).IsEqualTo("new type");
        await client.Keys.DeleteAsync("list");
        foreach (var deletion in new[] { "LPOP", "RPOP", "LREM", "LTRIM" })
        {
            await client.Lists.RightPushAsync("list", "last");
            await Number(client, "PEXPIRE", "list", 1000);
            RespireValue[] args = deletion switch
            {
                "LREM" => ["list", 0, "last"],
                "LTRIM" => ["list", 1, 0],
                _ => ["list"],
            };
            using (var removed = await client.ExecuteAsync(deletion, args)) { }
            await Assert.That(await client.ExistsAsync("list")).IsFalse();
            await client.Lists.RightPushAsync("list", "persistent");
            await Assert.That(await Number(client, "PTTL", "list")).IsEqualTo(-1);
            await client.Keys.DeleteAsync("list");
        }
    }

    [Test]
    public async Task ConcurrentPushesAndPopsOwnEachElementExactlyOnce()
    {
        await using var server = new RespireFakeServer();
        await using var first = await RespireClient.ConnectAsync(server.CreateOptions());
        await using var second = await RespireClient.ConnectAsync(server.CreateOptions());
        var counts = await Task.WhenAll(Enumerable.Range(1, 100).Select(async value =>
            await (value % 2 == 0 ? first : second).Lists.RightPushAsync("list", value)));
        await Assert.That(counts.Order()).IsEquivalentTo(Enumerable.Range(1, 100).Select(value => (long)value), CollectionOrdering.Matching);
        var popped = await Task.WhenAll(Enumerable.Range(1, 100).Select(async value =>
            await (value % 2 == 0 ? first : second).Lists.LeftPopAsync<int>("list")));
        await Assert.That(popped.Order()).IsEquivalentTo(Enumerable.Range(1, 100), CollectionOrdering.Matching);
        await Assert.That(await first.ExistsAsync("list")).IsFalse();
    }

    [Test]
    public async Task UnsupportedBlockingAndMultiKeyListCommandsFailWithoutMutation()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        await client.Lists.RightPushAsync("list", "value");
        foreach (var (command, args) in new (string, RespireValue[])[]
        {
            ("BLPOP", ["list", 1]), ("BRPOP", ["list", 1]), ("LMPOP", [1, "list", "LEFT"]),
            ("BLMPOP", [1, 1, "list", "RIGHT"]), ("LMOVE", ["list", "other", "LEFT", "RIGHT"]),
            ("BLMOVE", ["list", "other", "LEFT", "RIGHT", 1]), ("RPOPLPUSH", ["list", "other"]),
            ("BRPOPLPUSH", ["list", "other", 1]),
        })
        {
            var error = await Assert.That(async () => { using var ignored = await client.ExecuteAsync(command, args); })
                .Throws<RespireServerException>();
            await Assert.That(error!.Message).Contains(command);
            await Assert.That(await client.Lists.IndexAsync("list", 0)).IsEqualTo("value");
            await Assert.That(await client.ExistsAsync("other")).IsFalse();
        }
    }

    private static async Task<long> Number(IRespireClient client, string command, params RespireValue[] args)
    {
        using var result = await client.ExecuteAsync(command, args);
        return result.AsInteger();
    }
}
