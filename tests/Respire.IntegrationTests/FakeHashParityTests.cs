using FluentAssertions;
using Respire.Protocol;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class FakeHashParityTests(RedisTestContainer fixture)
{
    [Test]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    public async Task HashCommandsPreserveBinaryFieldsCountsAndReplyShapes(bool useFake, int protocol)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        await using var client = await Connect(fake, protocol);
        byte[] key = [0, 255, .. Guid.NewGuid().ToByteArray()];
        byte[] field = [0, 255, 128];
        byte[] value = [255, 0, 13, 10];
        (await Number(client, "HSET", key, field, "old", field, value, "", "")).Should().Be(2);
        (await Number(client, "HLEN", key)).Should().Be(2);
        (await Number(client, "HEXISTS", key, field)).Should().Be(1);
        (await Number(client, "HSTRLEN", key, field)).Should().Be(4);
        (await Number(client, "HSTRLEN", key, "absent")).Should().Be(0);
        using (var owned = await client.ExecuteAsync("HGET", key, field))
        {
            var copy = owned.AsBytes();
            copy[0] = 0;
        }
        using (var read = await client.ExecuteAsync("HMGET", key, field, "absent", field, ""))
        {
            read.Count.Should().Be(4);
            read[0].AsBytes().Should().Equal(value);
            read[1].IsNull.Should().BeTrue();
            read[2].AsBytes().Should().Equal(value);
            read[3].AsBytes().Should().BeEmpty();
        }
        using (var all = await client.ExecuteAsync("HGETALL", key))
        {
            all.Type.Should().Be(protocol == 3 ? RespDataType.Map : RespDataType.Array);
            all.Count.Should().Be(4);
            var pairs = Enumerable.Range(0, all.Count / 2)
                .ToDictionary(index => Convert.ToHexString(all[index * 2].AsBytes()), index => all[index * 2 + 1].AsBytes());
            pairs[Convert.ToHexString(field)].Should().Equal(value);
            pairs[""].Should().BeEmpty();
        }
        using (var fields = await client.ExecuteAsync("HKEYS", key))
            fields.Select(item => Convert.ToHexString(item.AsBytes())).Should().BeEquivalentTo(Convert.ToHexString(field), "");
        using (var values = await client.ExecuteAsync("HVALS", key))
            values.Select(item => Convert.ToHexString(item.AsBytes())).Should().BeEquivalentTo(Convert.ToHexString(value), "");
        (await Number(client, "HSETNX", key, field, "ignored")).Should().Be(0);
        (await Number(client, "HSETNX", key, "new", "added")).Should().Be(1);
        using (var legacy = await client.ExecuteAsync("HMSET", key, "new", "updated"))
            legacy.AsString().Should().Be("OK");
        (await Number(client, "HDEL", key, field, field, "new", "")).Should().Be(3);
        using (var type = await client.ExecuteAsync("TYPE", key)) type.AsString().Should().Be("none");
        using (var empty = await client.ExecuteAsync("HGETALL", key))
        {
            empty.Type.Should().Be(protocol == 3 ? RespDataType.Map : RespDataType.Array);
            empty.Count.Should().Be(0);
        }
        using (var absent = await client.ExecuteAsync("HGET", key, field)) absent.IsNull.Should().BeTrue();
        using (var missing = await client.ExecuteAsync("HMGET", key, field, field, ""))
        {
            missing.Count.Should().Be(3);
            missing.Select(item => item.IsNull).Should().OnlyContain(isNull => isNull);
        }
        (await Number(client, "HLEN", key)).Should().Be(0);
        (await Number(client, "HEXISTS", key, field)).Should().Be(0);
        (await Number(client, "HDEL", key, field)).Should().Be(0);
    }

    [Test]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    public async Task WrongTypeErrorsPreserveHashAndStringState(bool useFake, int protocol)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        await using var client = await Connect(fake, protocol);
        var key = Guid.NewGuid().ToString("N");
        await client.Hashes.SetAsync(key, "field", "value");
        await Number(client, "PEXPIRE", key, 60_000);
        foreach (var (command, arguments) in new (string, RespireValue[])[]
        {
            ("GET", [key]), ("GETDEL", [key]), ("GETSET", [key, "changed"]),
            ("GETEX", [key, "PERSIST"]), ("GETEX", [key, "PX", 1]),
            ("STRLEN", [key]), ("APPEND", [key, "changed"]), ("INCR", [key]),
            ("DECR", [key]), ("INCRBY", [key, 1]), ("DECRBY", [key, 1]),
            ("SET", [key, "changed", "GET"]), ("SET", [key, "changed", "NX", "GET"]),
        })
        {
            Func<Task> run = async () => { using var ignored = await client.ExecuteAsync(command, arguments); };
            (await run.Should().ThrowAsync<RespireServerException>()).Which.Code.Should().Be("WRONGTYPE");
            (await client.Hashes.GetStringAsync(key, "field")).Should().Be("value");
            (await Number(client, "PTTL", key)).Should().BeInRange(30_000, 60_000);
        }
        using (var read = await client.ExecuteAsync("MGET", key, key + ":missing"))
            read.Select(item => item.IsNull).Should().OnlyContain(isNull => isNull);
        using (var nx = await client.ExecuteAsync("SET", key, "ignored", "NX")) nx.IsNull.Should().BeTrue();
        (await Number(client, "MSETNX", key, "ignored", key + ":other", "ignored")).Should().Be(0);
        using (var overwritten = await client.ExecuteAsync("SET", key, "string", "KEEPTTL")) overwritten.AsString().Should().Be("OK");
        (await Number(client, "PTTL", key)).Should().BeInRange(30_000, 60_000);
        foreach (var (command, arguments) in new (string, RespireValue[])[]
        {
            ("HSET", [key, "field", "value"]), ("HSETNX", [key, "field", "value"]),
            ("HMSET", [key, "field", "value"]), ("HGET", [key, "field"]),
            ("HMGET", [key, "field"]), ("HGETALL", [key]), ("HDEL", [key, "field"]),
            ("HEXISTS", [key, "field"]), ("HLEN", [key]), ("HKEYS", [key]),
            ("HVALS", [key]), ("HSTRLEN", [key, "field"]), ("HINCRBY", [key, "field", 1]),
        })
        {
            Func<Task> run = async () => { using var ignored = await client.ExecuteAsync(command, arguments); };
            (await run.Should().ThrowAsync<RespireServerException>()).Which.Code.Should().Be("WRONGTYPE");
            (await client.GetStringAsync(key)).Should().Be("string");
        }
        using (var deleted = await client.ExecuteAsync("DEL", key)) deleted.AsInteger().Should().Be(1);
        await client.Hashes.SetAsync(key, "field", "value");
        await Number(client, "PEXPIRE", key, 60_000);
        using var replaced = await client.ExecuteAsync("MSET", key, "replacement");
        (await client.GetStringAsync(key)).Should().Be("replacement");
        (await Number(client, "PTTL", key)).Should().Be(-1);
    }

    [Test]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    public async Task IntegerAndArityFailuresAreAtomic(bool useFake, int protocol)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        await using var client = await Connect(fake, protocol);
        var key = Guid.NewGuid().ToString("N");
        (await Number(client, "HINCRBY", key, "count", -2)).Should().Be(-2);
        foreach (var invalid in new[] { "+1", "01", "-0", " 1", "1.0", "9223372036854775808" })
        {
            Func<Task> increment = async () => { using var ignored = await client.ExecuteAsync("HINCRBY", key, "count", invalid); };
            await increment.Should().ThrowAsync<RespireServerException>();
            (await client.Hashes.GetStringAsync(key, "count")).Should().Be("-2");
        }
        await client.Hashes.SetAsync(key, "count", long.MaxValue);
        Func<Task> overflow = async () => { using var ignored = await client.ExecuteAsync("HINCRBY", key, "count", 1); };
        await overflow.Should().ThrowAsync<RespireServerException>();
        (await client.Hashes.GetStringAsync(key, "count")).Should().Be(long.MaxValue.ToString());
        await client.Hashes.SetAsync(key, "minimum", long.MinValue);
        Func<Task> underflow = async () => { using var ignored = await client.ExecuteAsync("HINCRBY", key, "minimum", -1); };
        await underflow.Should().ThrowAsync<RespireServerException>();
        (await client.Hashes.GetStringAsync(key, "minimum")).Should().Be(long.MinValue.ToString());
        foreach (var (command, arguments) in new (string, RespireValue[])[]
        {
            ("HSET", [key, "count", "changed", "no-value"]), ("HMSET", [key, "count", "changed", "no-value"]),
            ("HGET", [key]), ("HDEL", [key]), ("HMGET", [key]), ("HLEN", [key, "extra"]),
        })
        {
            Func<Task> invalid = async () => { using var ignored = await client.ExecuteAsync(command, arguments); };
            await invalid.Should().ThrowAsync<RespireServerException>().WithMessage("*wrong number of arguments*");
            (await client.Hashes.GetStringAsync(key, "count")).Should().Be(long.MaxValue.ToString());
        }
        using var batch = client.CreateBatch();
        var changed = batch.Hashes.Set(key, "count", 10);
        var incremented = batch.Hashes.Increment(key, "count", 2L);
        var read = batch.Hashes.GetString(key, "count");
        await batch.ExecuteAsync();
        changed.Result.Should().BeFalse();
        incremented.Result.Should().Be(12);
        read.Result.Should().Be("12");
    }

    private ValueTask<RespireClient> Connect(RespireFakeServer? fake, int protocol)
        => RespireClient.ConnectAsync((fake?.CreateOptions() ?? RespireOptions.Parse(fixture.ConnectionString))
            with { Protocol = (RespProtocol)protocol, Connections = 1 });

    private static async Task<long> Number(IRespireClient client, string command, params RespireValue[] args)
    {
        using var result = await client.ExecuteAsync(command, args);
        return result.AsInteger();
    }
}
