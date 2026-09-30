using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class FakeSetServerTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task MutationsPreserveTtlWhileStoreAndLastRemovalReplaceIt(int protocol)
    {
        var clock = new RespireFakeClock();
        await using var server = new RespireFakeServer(clock);
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
        await client.Sets.AddAsync("source", "move", "last");
        await client.Sets.AddAsync("destination", "existing");
        await Number(client, "PEXPIRE", "source", 1000);
        await Number(client, "PEXPIRE", "destination", 2000);
        await client.SetAsync("wrong", "string");
        await Number(client, "PEXPIRE", "wrong", 3000);
        await Assert.That(async () => await client.Sets.MoveAsync("source", "wrong", "move")).Throws<RespireServerException>();
        await Assert.That(async () => await client.Sets.IntersectStoreAsync("destination", "source", "wrong")).Throws<RespireServerException>();
        await Assert.That(await Number(client, "PTTL", "source")).IsEqualTo(1000);
        await Assert.That(await Number(client, "PTTL", "destination")).IsEqualTo(2000);
        await Assert.That(await Number(client, "PTTL", "wrong")).IsEqualTo(3000);
        await client.Sets.AddAsync("source", "temporary");
        await client.Sets.RemoveAsync("source", "temporary");
        await client.Sets.MoveAsync("source", "source", "move");
        await client.Sets.MoveAsync("source", "destination", "move");
        await Assert.That(await Number(client, "PTTL", "source")).IsEqualTo(1000);
        await Assert.That(await Number(client, "PTTL", "destination")).IsEqualTo(2000);
        await client.Sets.MoveAsync("source", "new", "last");
        await Assert.That(await Number(client, "PTTL", "source")).IsEqualTo(-2);
        await Assert.That(await Number(client, "PTTL", "new")).IsEqualTo(-1);
        await client.Sets.UnionStoreAsync("destination", "destination", "new");
        await Assert.That(await Number(client, "PTTL", "destination")).IsEqualTo(-1);
        await Number(client, "PEXPIRE", "new", 1000);
        await client.Sets.RemoveAsync("new", "last");
        await client.Sets.AddAsync("new", "replacement");
        await Assert.That(await Number(client, "PTTL", "new")).IsEqualTo(-1);
        await Number(client, "PEXPIRE", "new", 1000);
        clock.Advance(TimeSpan.FromMilliseconds(1000));
        await Assert.That(await client.Sets.CountAsync("new")).IsEqualTo(0);
        await client.SetAsync("new", "string after expiry");
        await Assert.That(await client.GetStringAsync("new")).IsEqualTo("string after expiry");
    }

    [Test]
    public async Task ConcurrentDuplicatesAndMovesAreAtomic()
    {
        await using var server = new RespireFakeServer();
        await using var first = await RespireClient.ConnectAsync(server.CreateOptions());
        await using var second = await RespireClient.ConnectAsync(server.CreateOptions());
        var added = await Task.WhenAll(Enumerable.Range(0, 200).Select(async index =>
            await (index < 100 ? first : second).Sets.AddAsync("source", index % 100)));
        await Assert.That(added.Sum()).IsEqualTo(100);
        var moved = await Task.WhenAll(Enumerable.Range(0, 200).Select(async index =>
            await (index < 100 ? first : second).Sets.MoveAsync("source", "destination", index % 100)));
        await Assert.That(moved.Count(value => value)).IsEqualTo(100);
        await Assert.That(await first.ExistsAsync("source")).IsFalse();
        await Assert.That(await first.Sets.CountAsync("destination")).IsEqualTo(100);
        var removed = await Task.WhenAll(Enumerable.Range(0, 200).Select(async index =>
            await (index < 100 ? first : second).Sets.RemoveAsync("destination", index % 100)));
        await Assert.That(removed.Sum()).IsEqualTo(100);
        await Assert.That(await first.ExistsAsync("destination")).IsFalse();
    }

    [Test]
    public async Task UnsupportedSamplingAndScanningDoNotMutateSets()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        await client.Sets.AddAsync("set", "member");
        foreach (var (command, args) in new (string, RespireValue[])[]
        {
            ("SPOP", ["set"]), ("SRANDMEMBER", ["set"]), ("SSCAN", ["set", 0]),
        })
        {
            var error = await Assert.That(async () => { using var ignored = await client.ExecuteAsync(command, args); })
                .Throws<RespireServerException>();
            await Assert.That(error!.Message).Contains(command);
            await Assert.That(await client.Sets.CountAsync("set")).IsEqualTo(1);
        }
    }

    private static async Task<long> Number(IRespireClient client, string command, params RespireValue[] args)
    {
        using var result = await client.ExecuteAsync(command, args);
        return result.AsInteger();
    }
}
