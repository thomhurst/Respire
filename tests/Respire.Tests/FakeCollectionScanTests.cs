using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class FakeCollectionScanTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task TypedScansReturnMembersFieldsValuesAndScores(int protocol)
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // Each scenario owns its isolated fake client.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespireProtocol)protocol });
#pragma warning restore RESP010
        var prefixed = client.WithKeyPrefix("tenant:");
        for (var i = 0; i < 30; i++)
        {
            await prefixed.Sets.AddAsync("set", $"member:{i}");
            await prefixed.Hashes.SetAsync("hash", $"member:{i}", $"value:{i}");
            await prefixed.SortedSets.AddAsync("sorted", $"member:{i}", i + 0.5);
        }
        var members = new List<string>();
        await foreach (var member in prefixed.Sets.ScanAsync("set", "member:1?", countHint: 1)) members.Add(member);
        await Assert.That(members).IsEquivalentTo(Enumerable.Range(10, 10).Select(i => $"member:{i}"));
        var entries = new List<KeyValuePair<string, string>>();
        await foreach (var entry in prefixed.Hashes.ScanAsync("hash", countHint: 1)) entries.Add(entry);
        await Assert.That(entries).IsEquivalentTo(Enumerable.Range(0, 30)
            .Select(i => new KeyValuePair<string, string>($"member:{i}", $"value:{i}")));
        var scores = new List<RespireSortedSetEntry>();
        await foreach (var entry in prefixed.SortedSets.ScanAsync("sorted", "member:1?", countHint: 1)) scores.Add(entry);
        await Assert.That(scores).IsEquivalentTo(Enumerable.Range(10, 10).Select(i => new RespireSortedSetEntry($"member:{i}", i + 0.5)));
        var fields = new List<string>();
        await foreach (var field in prefixed.Hashes.ScanFieldsAsync("hash", countHint: 1)) fields.Add(field);
        await Assert.That(fields).IsEquivalentTo(Enumerable.Range(0, 30).Select(i => $"member:{i}"));
    }

    [Test]
    [Arguments("SSCAN", "SADD", "SREM")]
    [Arguments("HSCAN", "HSET", "HDEL")]
    [Arguments("ZSCAN", "ZADD", "ZREM")]
    public async Task DeletingReturnedEntriesDoesNotSkipSurvivors(string command, string add, string remove)
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // Each scenario owns its isolated fake client.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
#pragma warning restore RESP010
        for (var i = 0; i < 100; i++)
        {
            RespireValue[] args = command switch
            {
                "HSCAN" => ["key", $"member:{i}", $"value:{i}"],
                "ZSCAN" => ["key", i, $"member:{i}"],
                _ => ["key", $"member:{i}"],
            };
            using var added = await client.ExecuteAsync(add, args);
        }
        using var empty = await client.ExecuteAsync(command, "key", 0, "MATCH", "absent:*", "COUNT", 1);
        await Assert.That(empty[0].AsString()).IsNotEqualTo("0");
        await Assert.That(empty[1].Count).IsEqualTo(0);
        var seen = new List<string>();
        var cursor = "0";
        var pages = 0;
        do
        {
            using var page = await client.ExecuteAsync(command, "key", cursor, "COUNT", 1);
            cursor = page[0].AsString();
            for (var i = 0; i < page[1].Count; i += command == "SSCAN" ? 1 : 2)
            {
                var member = page[1][i].AsString();
                seen.Add(member);
                using var removed = await client.ExecuteAsync(remove, "key", member);
            }
            if (++pages > 256) throw new InvalidOperationException("Collection scan failed to terminate.");
        } while (cursor != "0");
        await Assert.That(pages).IsGreaterThan(1);
        await Assert.That(seen).IsEquivalentTo(Enumerable.Range(0, 100).Select(i => $"member:{i}"));
    }

    [Test]
    [Arguments("SSCAN")]
    [Arguments("HSCAN")]
    [Arguments("ZSCAN")]
    public async Task MissingExpiredWrongTypeAndInvalidOptionsLeaveConnectionUsable(string command)
    {
        var clock = new RespireFakeClock();
        await using var server = new RespireFakeServer(clock);
#pragma warning disable RESP010 // Each scenario owns its isolated fake client.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Connections = 1 });
#pragma warning restore RESP010
        using var missing = await client.ExecuteAsync(command, "key", 0);
        await Assert.That(missing[0].AsString()).IsEqualTo("0");
        await Assert.That(missing[1].Count).IsEqualTo(0);
        await client.SetAsync("key", "wrong type");
        var wrongType = await Assert.That(async () => { using var reply = await client.ExecuteAsync(command, "key", 0); })
            .Throws<RespireServerException>();
        await Assert.That(wrongType!.Message).Contains("WRONGTYPE");
        using var expiry = await client.ExecuteAsync("PEXPIRE", "key", 1);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        using var expired = await client.ExecuteAsync(command, "key", 0);
        await Assert.That(expired[0].AsString()).IsEqualTo("0");
        await Assert.That(expired[1].Count).IsEqualTo(0);
        foreach (var args in new RespireValue[][]
        {
            [], ["key"], ["key", "invalid"], ["key", "18446744073709551616"],
            ["key", 0, "COUNT", 0], ["key", 0, "COUNT", -1], ["key", 0, "COUNT", "bad"],
            ["key", 0, "MATCH"], ["key", 0, "COUNT"], ["key", 0, "UNKNOWN", 1],
        })
        {
            await Assert.That(async () => { using var reply = await client.ExecuteAsync(command, args); })
                .Throws<RespireServerException>();
        }
        using var end = await client.ExecuteAsync(command, "key", "18446744073709551615");
        await Assert.That(end[0].AsString()).IsEqualTo("0");
        if (command != "HSCAN")
        {
            await Assert.That(async () => { using var reply = await client.ExecuteAsync(command, "key", 0, "NOVALUES"); })
                .Throws<RespireServerException>();
        }
        using var ping = await client.ExecuteAsync("PING");
        await Assert.That(ping.AsString()).IsEqualTo("PONG");
    }

    [Test]
    [Arguments("SSCAN", "SADD")]
    [Arguments("HSCAN", "HSET")]
    [Arguments("ZSCAN", "ZADD")]
    public async Task ExpensiveGlobsFailExplicitlyWithoutChangingData(string command, string add)
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // Each scenario owns its isolated fake client.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Connections = 1 });
#pragma warning restore RESP010
        var member = new string('a', 2000);
        RespireValue[] args = command switch
        {
            "HSCAN" => ["key", member, "value"],
            "ZSCAN" => ["key", 1, member],
            _ => ["key", member],
        };
        using var added = await client.ExecuteAsync(add, args);
        var error = await Assert.That(async () =>
        {
            using var reply = await client.ExecuteAsync(command, "key", 0, "MATCH", "*" + new string('a', 1000) + "b");
        }).Throws<RespireServerException>();
        await Assert.That(error!.Message).Contains($"{command} glob exceeded its work limit");
        using var all = await client.ExecuteAsync(command, "key", 0, "COUNT", long.MaxValue);
        await Assert.That(all[1].Count).IsEqualTo(command == "SSCAN" ? 1 : 2);
        await Assert.That(all[1][0].AsString()).IsEqualTo(member);
    }
}
