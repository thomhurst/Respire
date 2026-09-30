using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class FakeSortedSetParityTests(RedisTestContainer fixture)
{
    // The shared fixture puts each TUnit test instance in its own Redis database.
    // Root clients are required here because prefixed views reject raw ExecuteAsync.
    [Test]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    public async Task LegacyLexAndIntersectionErrorsMatchRedis(bool useFake, int protocol)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        await using var client = await Connect(fake, protocol);
        await client.SortedSets.AddAsync("key", ("a", 1));
        foreach (var key in new[] { "key", "missing" })
        {
            foreach (var command in new[] { "ZRANGEBYLEX", "ZREVRANGEBYLEX" })
            {
                Func<Task> scoredLex = async () =>
                {
                    using var ignored = await client.ExecuteAsync(command, key, "-", "+", "WITHSCORES");
                };
                (await scoredLex.Should().ThrowAsync<RespireServerException>()).Which.Message
                    .Should().Be("ERR syntax error, WITHSCORES not supported in combination with BYLEX");
            }
            Func<Task> missingArguments = async () =>
            {
                using var ignored = await client.ExecuteAsync("ZINTERCARD", 2, key);
            };
            (await missingArguments.Should().ThrowAsync<RespireServerException>()).Which.Message
                .Should().Be("ERR syntax error");
        }
        (await client.SortedSets.ScoreAsync("key", "a")).Should().Be(1);
    }

    [Test]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    public async Task AddOptionsValidateAtomicallyAndPreserveExpiry(bool useFake, int protocol)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        await using var client = await Connect(fake, protocol);
        (await Number(client, "ZADD", "key", "XX", 1, "a")).Should().Be(0);
        (await client.ExistsAsync("key")).Should().BeFalse();
        (await Number(client, "ZADD", "key", "CH", 1, "a", 2, "a", 2, "a")).Should().Be(2);
        (await Number(client, "ZADD", "key", "NX", 9, "a", 3, "b")).Should().Be(1);
        await Number(client, "PEXPIRE", "key", 60_000);
        (await Number(client, "ZADD", "key", "XX", "GT", "CH", 1, "a", 4, "b", 5, "missing")).Should().Be(1);
        (await Number(client, "ZADD", "key", "LT", "CH", 1, "a", 9, "b", 6, "c")).Should().Be(2);
        using (var result = await client.ExecuteAsync("ZADD", "key", "INCR", "GT", -1, "a")) result.IsNull.Should().BeTrue();
        using (var result = await client.ExecuteAsync("ZADD", "key", "NX", "INCR", 1, "a")) result.IsNull.Should().BeTrue();
        using (var result = await client.ExecuteAsync("ZADD", "key", "INCR", 2, "a")) result.AsDouble().Should().Be(3);
        (await client.SortedSets.ScoresManyAsync("key", "a", "b", "c", "missing")).Should().Equal(3, 4, 6, null);
        foreach (var args in new RespireValue[][]
        {
            ["key", "NX", "XX", 1, "a"], ["key", "NX", "GT", 1, "a"], ["key", "GT", "LT", 1, "a"],
            ["key", "INCR", 1, "a", 1, "b"], ["key", 9, "a", "NaN", "b"],
            ["key", 9, "a", "1e400", "b"], ["key", 9, "a", "1e-400", "b"],
            ["key", "0x1p-1075", "a"], ["key", "0x1p1024", "a"], ["key", "0x1p+", "a"],
            ["key", " 1", "a"], ["key", "1 ", "a"], ["key", 1], ["key", "XX"],
        })
        {
            Func<Task> invalid = async () => { using var ignored = await client.ExecuteAsync("ZADD", args); };
            await invalid.Should().ThrowAsync<RespireServerException>();
            (await client.SortedSets.ScoresManyAsync("key", "a", "b", "c")).Should().Equal(3, 4, 6);
            (await Number(client, "PTTL", "key")).Should().BeInRange(30_000, 60_000);
        }
        await Number(client, "ZADD", "numeric", "+inf", "inf", "-inf", "negative", "0x1.8p+2", "hex", "5e-324", "tiny");
        (await client.SortedSets.ScoreAsync("numeric", "hex")).Should().Be(6);
        (await client.SortedSets.ScoreAsync("numeric", "tiny")).Should().Be(double.Epsilon);
        await Number(client, "ZADD", "hex-rounding", "0x1.00000000000008p0", "even-low", "0x1.00000000000018p0", "even-high", "0x1.8p-1075", "subnormal");
        (await client.SortedSets.ScoresManyAsync("hex-rounding", "even-low", "even-high", "subnormal"))
            .Should().Equal(1, Math.BitIncrement(Math.BitIncrement(1d)), double.Epsilon);
        Func<Task> nan = async () => { using var ignored = await client.ExecuteAsync("ZINCRBY", "numeric", "-inf", "inf"); };
        await nan.Should().ThrowAsync<RespireServerException>().WithMessage("*resulting score*NaN*");
        (await client.SortedSets.ScoreAsync("numeric", "inf")).Should().Be(double.PositiveInfinity);
        await Number(client, "ZADD", "overflow", double.MaxValue, "a");
        (await client.SortedSets.IncrementAsync("overflow", "a", double.MaxValue)).Should().Be(double.PositiveInfinity);
        await Number(client, "ZADD", "zero", "-0", "member");
        var initialZero = (await client.SortedSets.ScoreAsync("zero", "member"))!.Value;
        (await Number(client, "ZADD", "zero", "CH", "+0", "member")).Should().Be(0);
        var unchangedZero = (await client.SortedSets.ScoreAsync("zero", "member"))!.Value;
        BitConverter.DoubleToInt64Bits(unchangedZero).Should().Be(BitConverter.DoubleToInt64Bits(initialZero));
    }

    [Test]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    public async Task RangesRanksAndPopsPreserveBinaryOrderAndReplyShape(bool useFake, int protocol)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        await using var client = await Connect(fake, protocol);
        byte[] binaryKey = [0, 255, .. Guid.NewGuid().ToByteArray()];
        await Number(client, "ZADD", binaryKey, 0, new byte[] { 255 }, 0, new byte[] { 0 }, 0, "", -1, "low", 1, "high");
        (await Members(client, "ZRANGE", binaryKey, 0, -1)).Should().Equal("6C6F77", "", "00", "FF", "68696768");
        // Redis 7.x uses -1 as the no-LIMIT sentinel, even when explicitly supplied.
        (await Members(client, "ZRANGE", binaryKey, 0, -1, "LIMIT", 0, -1)).Should().Equal("6C6F77", "", "00", "FF", "68696768");
        (await Members(client, "ZRANGE", binaryKey, 0, -1, "LIMIT", 2, -1)).Should().Equal("6C6F77", "", "00", "FF", "68696768");
        (await Members(client, "ZRANGE", binaryKey, 0, -1, "LIMIT", 0, 1, "LIMIT", 0, -1)).Should().Equal("6C6F77", "", "00", "FF", "68696768");
        (await Members(client, "ZRANGE", binaryKey, -3, -1, "REV")).Should().Equal("00", "", "6C6F77");
        (await Members(client, "ZREVRANGE", binaryKey, long.MinValue, long.MaxValue)).Should().Equal("68696768", "FF", "00", "", "6C6F77");
        (await Members(client, "ZRANGE", binaryKey, 0, long.MinValue)).Should().BeEmpty();
        (await Number(client, "ZRANK", binaryKey, new byte[] { 255 })).Should().Be(3);
        (await Number(client, "ZREVRANK", binaryKey, new byte[] { 255 })).Should().Be(1);
        using (var missing = await client.ExecuteAsync("ZRANK", binaryKey, "missing")) missing.IsNull.Should().BeTrue();
        (await Members(client, "ZRANGE", binaryKey, "(0", "+inf", "BYSCORE")).Should().Equal("68696768");
        (await Members(client, "ZREVRANGEBYSCORE", binaryKey, "+inf", "-inf", "LIMIT", 1, 2)).Should().Equal("FF", "00");
        (await Members(client, "ZRANGEBYSCORE", binaryKey, "-inf", "+inf", "LIMIT", -1, 2)).Should().BeEmpty();
        (await Members(client, "ZRANGEBYSCORE", binaryKey, "-inf", "+inf", "LIMIT", 3, -2)).Should().Equal("FF", "68696768");
        (await Number(client, "ZCOUNT", binaryKey, "(0", "+inf")).Should().Be(1);
        using (var scored = await client.ExecuteAsync("ZRANGE", binaryKey, 0, 0, "WITHSCORES"))
        {
            scored.Count.Should().Be(protocol == 3 ? 1 : 2);
            var entry = protocol == 3 ? scored[0] : scored;
            entry[0].AsString().Should().Be("low");
            entry[1].AsDouble().Should().Be(-1);
        }
        using (var pop = await client.ExecuteAsync("ZPOPMIN", binaryKey))
        {
            pop.Count.Should().Be(2); // An omitted count stays flat even in RESP3.
            pop[0].AsString().Should().Be("low");
            pop[1].AsDouble().Should().Be(-1);
        }
        using (var pop = await client.ExecuteAsync("ZPOPMAX", binaryKey, 2))
        {
            pop.Count.Should().Be(protocol == 3 ? 2 : 4);
            (protocol == 3 ? pop[0][0] : pop[0]).AsString().Should().Be("high");
        }
        (await Number(client, "ZREMRANGEBYRANK", binaryKey, long.MinValue, long.MaxValue)).Should().Be(2);
        (await client.ExistsAsync(binaryKey)).Should().BeFalse();
        using (var pop = await client.ExecuteAsync("ZPOPMIN", binaryKey, 0)) pop.Count.Should().Be(0);
        using (var scores = await client.ExecuteAsync("ZMSCORE", binaryKey, "a", "b")) scores.All(value => value.IsNull).Should().BeTrue();
        await Number(client, "ZADD", "lex", 0, "", 0, new byte[] { 0 }, 0, "a", 0, new byte[] { 255 });
        (await Members(client, "ZRANGE", "lex", "[", new byte[] { (byte)'[', 255 }, "BYLEX")).Should().Equal("", "00", "61", "FF");
        (await Members(client, "ZREVRANGEBYLEX", "lex", "+", "-", "LIMIT", 1, 2)).Should().Equal("61", "00");
        (await Number(client, "ZLEXCOUNT", "lex", "[", "(a")).Should().Be(2);
        (await Number(client, "ZREMRANGEBYLEX", "lex", "[", "(a")).Should().Be(2);
        (await Number(client, "ZREMRANGEBYSCORE", "lex", "-inf", "+inf")).Should().Be(2);
        (await client.ExistsAsync("lex")).Should().BeFalse();
    }

    [Test]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    public async Task InvalidCommandsAndWrongTypesLeaveStateUnchanged(bool useFake, int protocol)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        await using var client = await Connect(fake, protocol);
        await client.SortedSets.AddAsync("key", ("a", 1), ("b", 2));
        foreach (var (command, args) in new (string, RespireValue[])[]
        {
            ("ZRANGE", ["key", 0, -1, "LIMIT", 0, 1]), ("ZRANGE", ["key", "-", "+", "BYLEX", "WITHSCORES"]),
            ("ZRANGE", ["key", 0, -1, "LIMIT", 0, -2]),
            ("ZRANGE", ["key", 0, -1, "LIMIT", 0, -1, "LIMIT", 0, 1]),
            ("ZRANGE", ["key", 0, -1, "REV", "REV"]), ("ZRANGE", ["key", 0, -1, "BYSCORE", "BYLEX"]),
            ("ZRANGE", ["key", 0, -1, "LIMIT", 1]), ("ZRANGE", ["key", "01", -1]),
            ("ZRANGEBYSCORE", ["key", "NaN", "+inf"]), ("ZCOUNT", ["key", "bad", 1]),
            ("ZRANGEBYLEX", ["key", "bad", "+"]), ("ZLEXCOUNT", ["key", "[a", "++"]),
            ("ZPOPMIN", ["key", -1]), ("ZPOPMAX", ["key", "01"]), ("ZREM", ["key"]),
            ("ZREMRANGEBYRANK", ["key", "bad", 0]), ("ZREMRANGEBYSCORE", ["key", "-inf", "NaN"]),
            ("ZREMRANGEBYLEX", ["key", "-", "bad"]), ("ZINTERCARD", [0, "key"]),
            ("ZINTERCARD", [2, "key"]), ("ZINTERCARD", [1, "key", "LIMIT", -1]),
            ("ZINTERCARD", [1, "key", "LIMIT"]), ("ZINTERCARD", [1, "key", "WEIGHTS", 2]),
        })
        {
            Func<Task> invalid = async () => { using var ignored = await client.ExecuteAsync(command, args); };
            await invalid.Should().ThrowAsync<RespireServerException>();
            (await client.SortedSets.RangeWithScoresAsync("key")).Should().Equal(new SortedSetEntry("a", 1), new SortedSetEntry("b", 2));
        }
        (await Number(client, "ZINTERCARD", 2, "key", "key", "LIMIT", 1, "limit", 0)).Should().Be(2);
        await client.Sets.AddAsync("set", "a", "other");
        (await Number(client, "ZINTERCARD", 2, "key", "set")).Should().Be(1);
        await client.Hashes.SetAsync("hash", "field", "value");
        foreach (var otherType in new[] { "set", "hash" })
        {
            Func<Task> invalid = async () => { using var ignored = await client.ExecuteAsync("ZADD", otherType, 1, "a"); };
            (await invalid.Should().ThrowAsync<RespireServerException>()).Which.Code.Should().Be("WRONGTYPE");
        }
        (await client.Sets.MembersAsync("set")).Should().BeEquivalentTo("a", "other");
        (await client.Hashes.GetStringAsync("hash", "field")).Should().Be("value");
        await client.SetAsync("wrong", "value");
        foreach (var (command, args) in new (string, RespireValue[])[]
        {
            ("ZADD", ["wrong", 1, "a"]), ("ZINCRBY", ["wrong", 1, "a"]), ("ZREM", ["wrong", "a"]),
            ("ZCARD", ["wrong"]), ("ZSCORE", ["wrong", "a"]), ("ZMSCORE", ["wrong", "a"]),
            ("ZRANK", ["wrong", "a"]), ("ZREVRANK", ["wrong", "a"]), ("ZCOUNT", ["wrong", 0, 1]),
            ("ZRANGE", ["wrong", 0, -1]), ("ZRANGEBYLEX", ["wrong", "-", "+"]), ("ZPOPMIN", ["wrong", 0]),
            ("ZREMRANGEBYRANK", ["wrong", 0, -1]), ("ZREMRANGEBYSCORE", ["wrong", 0, 1]),
            ("ZREMRANGEBYLEX", ["wrong", "-", "+"]), ("ZINTERCARD", [2, "missing", "wrong"]),
        })
        {
            Func<Task> wrong = async () => { using var ignored = await client.ExecuteAsync(command, args); };
            (await wrong.Should().ThrowAsync<RespireServerException>()).Which.Code.Should().Be("WRONGTYPE");
        }
        (await client.GetStringAsync("wrong")).Should().Be("value");
        using (var missing = await client.ExecuteAsync("MGET", "key")) missing[0].IsNull.Should().BeTrue();
        await client.SetAsync("key", "replacement");
        (await client.GetStringAsync("key")).Should().Be("replacement");
    }

    [Test]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    public async Task TypedScoredReadsAndPopsWorkInPrefixedBatches(bool useFake, int protocol)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        await using var root = await Connect(fake, protocol);
        var client = root.WithKeyPrefix(Guid.NewGuid().ToString("N") + ":");
        await client.SortedSets.AddAsync("key", (10, 1), (20, 2), (30, 3));
        using var batch = client.CreateBatch();
        var entries = batch.SortedSets.RangeWithScores<int>("key");
        var first = batch.SortedSets.Pop<int>("key");
        var last = batch.SortedSets.PopMany<int>("key", 2, descending: true);
        await batch.ExecuteAsync();
        entries.Result.Should().Equal(new SortedSetEntry<int>(10, 1), new SortedSetEntry<int>(20, 2), new SortedSetEntry<int>(30, 3));
        first.Result.Should().Be(new SortedSetEntry<int>(10, 1));
        last.Result.Should().Equal(new SortedSetEntry<int>(30, 3), new SortedSetEntry<int>(20, 2));
        (await client.SortedSets.PopAsync<int>("key")).Should().BeNull();
        var json = System.Text.Json.JsonSerializer.Serialize(new Payload(7, "seven"));
        await client.SortedSets.AddAsync("json", json, 7);
        (await client.SortedSets.PopAsync<Payload>("json"))!.Value.Member.Should().Be(new Payload(7, "seven"));
        await client.SortedSets.AddAsync("invalid", "not-an-integer", 1);
        Func<Task> invalid = async () => { await client.SortedSets.PopAsync<int>("invalid"); };
        await invalid.Should().ThrowAsync<FormatException>();
    }

    private sealed record Payload(int Number, string Name);

    private ValueTask<RespireClient> Connect(RespireFakeServer? fake, int protocol)
        => RespireClient.ConnectAsync((fake?.CreateOptions() ?? RespireOptions.Parse(fixture.ConnectionString))
            with { Protocol = (RespProtocol)protocol });

    private static async Task<long> Number(IRespireClient client, string command, params RespireValue[] args)
    {
        using var result = await client.ExecuteAsync(command, args);
        return result.AsInteger();
    }

    private static async Task<string[]> Members(IRespireClient client, string command, params RespireValue[] args)
    {
        using var result = await client.ExecuteAsync(command, args);
        return result.Select(value => Convert.ToHexString(value.AsBytes())).ToArray();
    }
}
