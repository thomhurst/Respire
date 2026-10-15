using Respire.Testing;
using Respire.TestSupport;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class FakeKeyspaceServerTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task TimeUsesInjectedClockWithMicrosecondPrecision(int protocol)
    {
        var start = DateTimeOffset.FromUnixTimeSeconds(123).AddTicks(4567897);
        var clock = new RespireFakeClock(start);
        await using var server = new RespireFakeServer(clock);
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespireProtocol)protocol });
        using var reply = await client.ExecuteAsync("TIME");
        await Assert.That(reply.Count).IsEqualTo(2);
        await Assert.That(reply[0].AsString()).IsEqualTo("123");
        await Assert.That(reply[1].AsString()).IsEqualTo("456789");
        await Assert.That(await client.Server.TimeAsync()).IsEqualTo(start.AddTicks(-7));
        clock.Advance(TimeSpan.FromTicks(7_000_003));
        await Assert.That(await client.Server.TimeAsync()).IsEqualTo(DateTimeOffset.FromUnixTimeSeconds(124).AddTicks(1567900));
    }

    [Test]
    public async Task TransactionSamplesTimeOnceAtExecution()
    {
        var clock = new AdvancingClock();
        await using var server = new RespireFakeServer(clock);
        await using var session = await TestRespSession.ConnectAsync(server.CreateOptions());
        using var multi = await session.CommandAsync("MULTI");
        using var queuedFirst = await session.CommandAsync("TIME");
        using var queuedSecond = await session.CommandAsync("TIME");
        var beforeExec = clock.Seconds;
        using var executed = await session.CommandAsync("EXEC");
        var first = executed.AsArray()[0].AsArray()[0].AsString();
        var second = executed.AsArray()[1].AsArray()[0].AsString();
        await Assert.That(first).IsEqualTo((beforeExec + 1).ToString());
        await Assert.That(second).IsEqualTo(first);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task EnumerationIncludesAllTypesAndExcludesExpiredKeys(int protocol)
    {
        var clock = new RespireFakeClock();
        await using var server = new RespireFakeServer(clock);
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespireProtocol)protocol });
        await client.SetAsync("test:string", "value");
        using var hash = await client.ExecuteAsync("HSET", "test:hash", "f", "v");
        using var list = await client.ExecuteAsync("LPUSH", "test:list", "v");
        using var set = await client.ExecuteAsync("SADD", "test:set", "v");
        using var sortedSet = await client.ExecuteAsync("ZADD", "test:zset", 1, "v");
        using var stream = await client.ExecuteAsync("XADD", "test:stream", "1-0", "f", "v");
        using var array = await client.ExecuteAsync("ARSET", "test:array", 0, "v");
        using var expiring = await client.ExecuteAsync("SET", "test:expired", "v", "PX", 1);
        await client.SetAsync("unrelated", "v");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        string[] expected = ["test:string", "test:hash", "test:list", "test:set", "test:zset", "test:stream", "test:array"];
        using var keys = await client.ExecuteAsync("KEYS", "test:*");
        await Assert.That(Enumerable.Range(0, keys.Count).Select(i => keys[i].AsString())).IsEquivalentTo(expected);
        var scanned = new List<string>();
        await foreach (var key in client.Keys.ScanAsync("test:*", countHint: 1)) scanned.Add(key);
        await Assert.That(scanned).IsEquivalentTo(expected);
    }

    [Test]
    [Arguments("a?", "ab", true)]
    [Arguments("a*", "a", true)]
    [Arguments("a[b-d]", "ac", true)]
    [Arguments("a[^b-d]", "ac", false)]
    [Arguments("a[^b-d]", "az", true)]
    [Arguments("a\\*", "a*", true)]
    [Arguments("a\\?", "ab", false)]
    [Arguments("A*", "ab", false)]
    [Arguments(@"[\a-z]", "a", true)]
    [Arguments(@"[\a-z]", "-", true)]
    [Arguments(@"[\a-z]", "z", true)]
    [Arguments(@"[\a-z]", "b", false)]
    [Arguments(@"[^\a-z]", "b", true)]
    [Arguments(@"[^\a-z]", "-", false)]
    [Arguments("", "", true)]
    public async Task KeysAndScanUseByteGlobPatterns(string pattern, string key, bool matches)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        await client.SetAsync(key, "value");
        using var keys = await client.ExecuteAsync("KEYS", pattern);
        using var scan = await client.ExecuteAsync("SCAN", 0, "MATCH", pattern, "COUNT", 1000);
        await Assert.That(keys.Count).IsEqualTo(matches ? 1 : 0);
        await Assert.That(scan[0].AsString()).IsEqualTo("0");
        await Assert.That(scan[1].Count).IsEqualTo(matches ? 1 : 0);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task BinaryPatternsAndReturnedKeysPreserveBytes(int protocol)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespireProtocol)protocol });
        byte[] key = [0, 255, 128, 42];
        byte[] pattern = [0, 255, (byte)'?', (byte)'\\', (byte)'*'];
        using var set = await client.ExecuteAsync("SET", key, "value");
        using var keys = await client.ExecuteAsync("KEYS", pattern);
        using var scan = await client.ExecuteAsync("SCAN", 0, "MATCH", pattern);
        await Assert.That(keys[0].AsBytes()).IsEquivalentTo(key);
        await Assert.That(scan[1][0].AsBytes()).IsEquivalentTo(key);
    }

    [Test]
    public async Task ScanCanDeleteEachPageWithoutSkippingRemainingKeys()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        var expected = Enumerable.Range(0, 100).Select(i => $"cleanup:{i}").ToArray();
        foreach (var key in expected) await client.SetAsync(key, "value");
        var seen = new List<string>();
        var cursor = "0";
        var pages = 0;
        do
        {
            using var page = await client.ExecuteAsync("SCAN", cursor, "COUNT", 1);
            cursor = page[0].AsString();
            for (var i = 0; i < page[1].Count; i++)
            {
                var key = page[1][i].AsString();
                seen.Add(key);
                using var deleted = await client.ExecuteAsync("DEL", key);
            }
            if (++pages > 256) throw new InvalidOperationException("SCAN failed to terminate.");
        } while (cursor != "0");
        await Assert.That(pages).IsGreaterThan(1);
        await Assert.That(seen).IsEquivalentTo(expected);
        using var remaining = await client.ExecuteAsync("KEYS", "*");
        await Assert.That(remaining.Count).IsEqualTo(0);
    }

    [Test]
    public async Task NonmatchingScanPagesStillAdvanceAndOptionsUseLastValue()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        for (var i = 0; i < 100; i++) await client.SetAsync($"key:{i}", "value");
        using var empty = await client.ExecuteAsync("SCAN", 0, "MATCH", "absent:*", "COUNT", 1);
        await Assert.That(empty[0].AsString()).IsNotEqualTo("0");
        await Assert.That(empty[1].Count).IsEqualTo(0);
        using var all = await client.ExecuteAsync("SCAN", 0, "match", "absent:*", "MATCH", "*", "count", 1, "COUNT", long.MaxValue);
        await Assert.That(all[0].AsString()).IsEqualTo("0");
        await Assert.That(all[1].Count).IsEqualTo(100);
    }

    [Test]
    public async Task EmptyAndPrefixedScansWorkAndLargeCursorsTerminate()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        using var empty = await client.ExecuteAsync("SCAN", 0);
        await Assert.That(empty[0].AsString()).IsEqualTo("0");
        await Assert.That(empty[1].Count).IsEqualTo(0);
        var prefixed = client.WithKeyPrefix("tenant:");
        await prefixed.SetAsync("job", "v");
        await client.SetAsync("outside", "v");
        var found = new List<string>();
        await foreach (var key in prefixed.Keys.ScanAsync("j*")) found.Add(key);
        await Assert.That(found).IsEquivalentTo(new[] { "job" });
        using var end = await client.ExecuteAsync("SCAN", "18446744073709551615");
        await Assert.That(end[0].AsString()).IsEqualTo("0");
        await Assert.That(end[1].Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("KEYS")]
    [Arguments("SCAN")]
    public async Task ExpensiveGlobsFailExplicitlyWithoutBreakingTheConnection(string command)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Connections = 1 });
        await client.SetAsync(new string('a', 2000), "v");
        var pattern = "*" + new string('a', 1000) + "b";
        RespireValue[] args = command == "KEYS" ? [pattern] : [0, "MATCH", pattern];
        var error = await Assert.That(async () => { using var reply = await client.ExecuteAsync(command, args); })
            .Throws<RespireServerException>();
        await Assert.That(error!.Message).Contains("glob exceeded its work limit");
        using var keys = await client.ExecuteAsync("KEYS", "*");
        await Assert.That(keys.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("TIME", "extra")]
    [Arguments("KEYS")]
    [Arguments("KEYS", "*", "extra")]
    [Arguments("SCAN")]
    [Arguments("SCAN", "invalid")]
    [Arguments("SCAN", "18446744073709551616")]
    [Arguments("SCAN", "0", "COUNT", "0")]
    [Arguments("SCAN", "0", "COUNT", "-1")]
    [Arguments("SCAN", "0", "COUNT", "bad")]
    [Arguments("SCAN", "0", "MATCH")]
    [Arguments("SCAN", "0", "COUNT")]
    [Arguments("SCAN", "0", "TYPE", "string")]
    public async Task InvalidArgumentsLeaveConnectionUsable(string command, params string[] args)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Connections = 1 });
        await Assert.That(async () => { using var reply = await client.ExecuteAsync(command, args.Select(a => (RespireValue)a).ToArray()); })
            .Throws<RespireServerException>();
        using var ping = await client.ExecuteAsync("PING");
        await Assert.That(ping.AsString()).IsEqualTo("PONG");
    }

    private sealed class AdvancingClock : TimeProvider
    {
        public long Seconds { get; private set; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(++Seconds);
    }
}
