using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class FakeHashServerTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task HashMutationsKeepKeyExpiryAndExpiredHashesCanChangeType(int protocol)
    {
        var clock = new RespireFakeClock();
        await using var server = new RespireFakeServer(clock);
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
        await client.Hashes.SetAsync("hash", "counter", 1);
        await Number(client, "PEXPIRE", "hash", 1000);
        await client.Hashes.SetAsync("hash", "other", "value");
        await Number(client, "HSETNX", "hash", "new", "value");
        await client.Hashes.IncrementAsync("hash", "counter");
        await client.Hashes.RemoveAsync("hash", "new");
        await Assert.That(await Number(client, "PTTL", "hash")).IsEqualTo(1000);
        clock.Advance(TimeSpan.FromMilliseconds(999));
        await Assert.That(await client.Hashes.GetStringAsync("hash", "counter")).IsEqualTo("2");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await Assert.That(await client.Hashes.CountAsync("hash")).IsEqualTo(0);
        await Assert.That(await Number(client, "PTTL", "hash")).IsEqualTo(-2);
        await client.SetAsync("hash", "string now");
        await Assert.That(await client.GetStringAsync("hash")).IsEqualTo("string now");
        await client.Keys.DeleteAsync("hash");
        await client.Hashes.SetAsync("hash", "last", "value");
        await Number(client, "PEXPIRE", "hash", 1000);
        await client.Hashes.RemoveAsync("hash", "last");
        await client.Hashes.SetAsync("hash", "new", "persistent");
        await Assert.That(await Number(client, "PTTL", "hash")).IsEqualTo(-1);
    }

    [Test]
    public async Task ConcurrentHashIncrementsAreAtomicAndDoNotCreateKeysOnInvalidNumbers()
    {
        await using var server = new RespireFakeServer();
        await using var first = await RespireClient.ConnectAsync(server.CreateOptions());
        await using var second = await RespireClient.ConnectAsync(server.CreateOptions());
        var results = await Task.WhenAll(Enumerable.Range(0, 200).Select(async index =>
            await (index % 2 == 0 ? first : second).Hashes.IncrementAsync("hash", "counter")));
        await Assert.That(results.Order()).IsEquivalentTo(Enumerable.Range(1, 200).Select(value => (long)value), CollectionOrdering.Matching);
        foreach (var invalid in new[] { "9223372036854775808", "01", "not-a-number" })
        {
            await Assert.That(async () => { using var ignored = await first.ExecuteAsync("HINCRBY", "absent", "field", invalid); })
                .Throws<RespireServerException>();
            await Assert.That(await first.ExistsAsync("absent")).IsFalse();
        }
        await first.Hashes.SetAsync("hash", "counter", "01");
        await Assert.That(async () => await first.Hashes.IncrementAsync("hash", "counter")).Throws<RespireServerException>();
        await Assert.That(await first.Hashes.GetStringAsync("hash", "counter")).IsEqualTo("01");
    }

    [Test]
    public async Task UnsupportedHashVariantsFailWithoutChangingData()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        await client.Hashes.SetAsync("hash", "field", "value");
        foreach (var (command, arguments) in new (string, RespireValue[])[]
        {
            ("HINCRBYFLOAT", ["hash", "field", 1]), ("HRANDFIELD", ["hash"]),
            ("HSCAN", ["hash", 0]), ("HEXPIRE", ["hash", 1, "FIELDS", 1, "field"]),
        })
        {
            var error = await Assert.That(async () => { using var ignored = await client.ExecuteAsync(command, arguments); })
                .Throws<RespireServerException>();
            await Assert.That(error!.Message).Contains(command);
            await Assert.That(await client.Hashes.GetStringAsync("hash", "field")).IsEqualTo("value");
        }
    }

    private static async Task<long> Number(IRespireClient client, string command, params RespireValue[] args)
    {
        using var result = await client.ExecuteAsync(command, args);
        return result.AsInteger();
    }
}
