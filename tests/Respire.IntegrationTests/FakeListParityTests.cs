using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class FakeListParityTests(RedisTestContainer fixture)
{
    // ConnectionString selects TestContext.Current.Isolation.UniqueId as the Redis
    // database for each invocation, including each protocol row. Raw ExecuteAsync
    // deliberately uses an unprefixed client because prefixed views reject raw commands.
    [Test]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    public async Task PushPopRangesAndRemovalPreserveOrderAndBinaryData(bool useFake, int protocol)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        var options = fake?.CreateOptions() ?? RespireOptions.Parse(fixture.ConnectionString);
        await using var client = await RespireClient.ConnectAsync(options with { Protocol = (RespProtocol)protocol });
        byte[] binary = [255, 0, 128];
        byte[] binaryKey = [0, 255, .. Guid.NewGuid().ToByteArray()];
        await Number(client, "RPUSH", binaryKey, binary);
        using (var owned = await client.ExecuteAsync("LINDEX", binaryKey, 0)) owned.AsBytes()[0] = 0;
        using (var popped = await client.ExecuteAsync("RPOP", binaryKey)) popped.AsBytes().Should().Equal(binary);
        (await Number(client, "LPUSHX", "missing", binary)).Should().Be(0);
        (await Number(client, "RPUSHX", "missing", binary)).Should().Be(0);
        using (var missing = await client.ExecuteAsync("LPOP", "missing", 0)) missing.IsNull.Should().BeTrue();
        using (var missing = await client.ExecuteAsync("RPOP", "missing")) missing.IsNull.Should().BeTrue();
        (await Number(client, "RPUSH", "list", "first", binary, "", binary)).Should().Be(4);
        (await Number(client, "LPUSH", "list", "one", "two")).Should().Be(6);
        (await Number(client, "LLEN", "list")).Should().Be(6);
        using (var type = await client.ExecuteAsync("TYPE", "list")) type.AsString().Should().Be("list");
        (await Read(client, "LRANGE", "list", 0, 2)).Should().Equal("74776F", "6F6E65", "6669727374");
        (await Read(client, "LRANGE", "list", -3, -1)).Should().Equal("FF0080", "", "FF0080");
        (await Read(client, "LRANGE", "list", long.MinValue, long.MaxValue)).Length.Should().Be(6);
        (await Read(client, "LRANGE", "list", 0, long.MinValue)).Should().BeEmpty();
        using (var index = await client.ExecuteAsync("LINDEX", "list", -1)) index.AsBytes().Should().Equal(binary);
        using (var index = await client.ExecuteAsync("LINDEX", "list", long.MinValue)) index.IsNull.Should().BeTrue();
        using (var index = await client.ExecuteAsync("LINDEX", "missing", "invalid")) index.IsNull.Should().BeTrue();
        (await Read(client, "LPOP", "list", 0)).Should().BeEmpty();
        (await Number(client, "LLEN", "list")).Should().Be(6);
        (await Read(client, "RPOP", "list", 2)).Should().Equal("FF0080", "");
        (await Read(client, "LPOP", "list", 2)).Should().Equal("74776F", "6F6E65");
        using (var trimmed = await client.ExecuteAsync("LTRIM", "list", -1, -1)) trimmed.AsString().Should().Be("OK");
        (await Read(client, "LRANGE", "list", 0, -1)).Should().Equal("FF0080");
        (await Number(client, "RPUSH", "list", "gap", binary, binary)).Should().Be(4);
        (await Number(client, "LREM", "list", -1, binary)).Should().Be(1);
        (await Read(client, "LRANGE", "list", 0, -1)).Should().Equal("FF0080", "676170", "FF0080");
        (await Number(client, "LREM", "list", 1, binary)).Should().Be(1);
        (await Number(client, "LREM", "list", 0, binary)).Should().Be(1);
        (await Read(client, "RPOP", "list", long.MaxValue)).Should().Equal("676170");
        (await client.ExistsAsync("list")).Should().BeFalse();
        await client.Lists.RightPushAsync("list", "x", "x", "x");
        (await Number(client, "LREM", "list", long.MinValue, "x")).Should().Be(3);
        (await client.ExistsAsync("list")).Should().BeFalse();
        await client.Lists.RightPushAsync("list", "x");
        using (var trimmed = await client.ExecuteAsync("LTRIM", "list", 2, 1)) trimmed.AsString().Should().Be("OK");
        (await client.ExistsAsync("list")).Should().BeFalse();
        using (var trimmed = await client.ExecuteAsync("LTRIM", "missing", 0, -1)) trimmed.AsString().Should().Be("OK");
    }

    [Test]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    public async Task ErrorsAndSearchOptionsDoNotMutateLists(bool useFake, int protocol)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        var options = fake?.CreateOptions() ?? RespireOptions.Parse(fixture.ConnectionString);
        await using var client = await RespireClient.ConnectAsync(options with { Protocol = (RespProtocol)protocol });
        await client.Lists.RightPushAsync("list", "a", "b", "a", "a");
        await Number(client, "PEXPIRE", "list", 60_000);
        foreach (var (command, args) in new (string, RespireValue[])[]
        {
            ("LPUSH", ["list"]), ("RPUSHX", ["list"]), ("LPOP", ["list", -1]),
            ("RPOP", ["list", "01"]), ("LPOP", ["list", 1, 2]), ("LLEN", ["list", 1]),
            ("LRANGE", ["list", "bad", -1]), ("LTRIM", ["list", 0, "bad"]),
            ("LINDEX", ["list", "01"]), ("LSET", ["list", 99, "x"]),
            ("LSET", ["list", "9223372036854775808", "x"]), ("LREM", ["list", "+1", "a"]),
            ("LINSERT", ["list", "AROUND", "a", "x"]), ("LPOS", ["list", "a", "RANK", 0]),
            ("LPOS", ["list", "a", "RANK", "-9223372036854775809"]), ("LPOS", ["list", "a", "COUNT", -1]),
            ("LPOS", ["list", "a", "MAXLEN", -1]), ("LPOS", ["list", "a", "COUNT"]),
            ("LPOS", ["list", "a", "UNKNOWN", 1]),
            ("LPOS", ["list", "a", "RANK", 0, "RANK", 1]),
            ("LPOS", ["list", "a", "COUNT", -1, "COUNT", 1]),
            ("LPOS", ["list", "a", "MAXLEN", -1, "MAXLEN", 0]),
        })
        {
            Func<Task> invalid = async () => { using var ignored = await client.ExecuteAsync(command, args); };
            await invalid.Should().ThrowAsync<RespireServerException>();
            (await client.Lists.RangeAsync("list")).Should().Equal("a", "b", "a", "a");
            (await Number(client, "PTTL", "list")).Should().BeInRange(30_000, 60_000);
        }
        using (var found = await client.ExecuteAsync("LPOS", "list", "a", "rank", 2, "RANK", -1)) found.AsInteger().Should().Be(3);
        using (var found = await client.ExecuteAsync("LPOS", "list", "a", "COUNT", 0, "COUNT", 2, "MAXLEN", 0))
            found.Select(value => value.AsInteger()).Should().Equal(0, 2);
        using (var absent = await client.ExecuteAsync("LPOS", "missing", "a", "COUNT", 0)) absent.Count.Should().Be(0);
        using (var absent = await client.ExecuteAsync("LPOS", "list", "a", "RANK", long.MaxValue)) absent.IsNull.Should().BeTrue();
        (await Number(client, "LINSERT", "list", "AFTER", "a", "inserted")).Should().Be(5);
        (await client.Lists.RangeAsync("list")).Should().Equal("a", "inserted", "b", "a", "a");
        foreach (var valueType in new[] { "string", "hash" })
        {
            await client.Keys.DeleteAsync("wrong");
            if (valueType == "string") await client.SetAsync("wrong", "value");
            else await client.Hashes.SetAsync("wrong", "field", "value");
            foreach (var (command, args) in new (string, RespireValue[])[]
            {
                ("LPUSH", ["wrong", "x"]), ("RPUSH", ["wrong", "x"]), ("LPUSHX", ["wrong", "x"]),
                ("RPUSHX", ["wrong", "x"]), ("LPOP", ["wrong"]), ("RPOP", ["wrong", 0]),
                ("LLEN", ["wrong"]), ("LRANGE", ["wrong", 0, -1]), ("LINDEX", ["wrong", 0]),
                ("LSET", ["wrong", 0, "x"]), ("LTRIM", ["wrong", 0, -1]), ("LREM", ["wrong", 0, "x"]),
                ("LINSERT", ["wrong", "BEFORE", "x", "y"]), ("LPOS", ["wrong", "x"]),
            })
            {
                Func<Task> wrongType = async () => { using var ignored = await client.ExecuteAsync(command, args); };
                var error = await wrongType.Should().ThrowAsync<RespireServerException>();
                error.Which.Code.Should().Be("WRONGTYPE");
            }
            if (valueType == "string") (await client.GetStringAsync("wrong")).Should().Be("value");
            else (await client.Hashes.GetStringAsync("wrong", "field")).Should().Be("value");
        }
        Func<Task> get = async () => { await client.GetStringAsync("list"); };
        await get.Should().ThrowAsync<RespireServerException>().WithMessage("WRONGTYPE*");
        using (var mixed = await client.ExecuteAsync("MGET", "list")) mixed[0].IsNull.Should().BeTrue();
        await client.SetAsync("list", "replacement");
        (await client.GetStringAsync("list")).Should().Be("replacement");
    }

    private static async Task<long> Number(IRespireClient client, string command, params RespireValue[] args)
    {
        using var result = await client.ExecuteAsync(command, args);
        return result.AsInteger();
    }

    private static async Task<string[]> Read(IRespireClient client, string command, params RespireValue[] args)
    {
        using var result = await client.ExecuteAsync(command, args);
        return result.Select(value => Convert.ToHexString(value.AsBytes())).ToArray();
    }
}
