using FluentAssertions;
using Respire.Protocol;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class FakeSetParityTests(RedisTestContainer fixture)
{
    [Test]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    public async Task BinaryMembersCountsAndReplyShapesMatch(bool useFake, int protocol)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        await using var client = await Connect(fake, protocol);
        byte[] key = [0, 255, .. Guid.NewGuid().ToByteArray()];
        byte[] member = [0, 255, 128, 13, 10];
        (await Number(client, "SADD", key, member, member, "")).Should().Be(2);
        (await Number(client, "SADD", key, member)).Should().Be(0);
        (await Number(client, "SCARD", key)).Should().Be(2);
        using (var members = await client.ExecuteAsync("SMEMBERS", key))
        {
            members.Type.Should().Be(protocol == 3 ? RespDataType.Set : RespDataType.Array);
            members.Select(value => Convert.ToHexString(value.AsBytes())).Should().BeEquivalentTo(Convert.ToHexString(member), "");
            foreach (var value in members)
            {
                var copy = value.AsBytes();
                if (copy.Length != 0) copy[0] = 42;
            }
        }
        (await Number(client, "SISMEMBER", key, member)).Should().Be(1);
        using (var membership = await client.ExecuteAsync("SMISMEMBER", key, "missing", member, member, ""))
            membership.Select(value => value.AsInteger()).Should().Equal(0, 1, 1, 1);
        (await Number(client, "SREM", key, member, member, "missing", "")).Should().Be(2);
        using (var type = await client.ExecuteAsync("TYPE", key)) type.AsString().Should().Be("none");
        using (var members = await client.ExecuteAsync("SMEMBERS", key))
        {
            members.Type.Should().Be(protocol == 3 ? RespDataType.Set : RespDataType.Array);
            members.Count.Should().Be(0);
        }
        (await Number(client, "SCARD", key)).Should().Be(0);
        (await Number(client, "SREM", key, member)).Should().Be(0);
    }

    [Test]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    public async Task AlgebraAndStoreAliasingPreserveRedisSemantics(bool useFake, int protocol)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        await using var client = await Connect(fake, protocol);
        var prefix = Guid.NewGuid().ToString("N") + ":";
        var leftKey = prefix + "left";
        var rightKey = prefix + "right";
        var destinationKey = prefix + "destination";
        var missingKey = prefix + "missing";
        await client.Sets.AddAsync(leftKey, "a", "b");
        await client.Sets.AddAsync(rightKey, "b", "c");
        (await client.Sets.IntersectAsync(leftKey, rightKey, leftKey)).Should().BeEquivalentTo("b");
        (await client.Sets.UnionAsync(leftKey, rightKey, missingKey)).Should().BeEquivalentTo("a", "b", "c");
        (await client.Sets.DifferenceAsync(leftKey, rightKey)).Should().BeEquivalentTo("a");
        (await client.Sets.DifferenceAsync(leftKey, leftKey)).Should().BeEmpty();
        (await client.Sets.IntersectAsync(leftKey, missingKey)).Should().BeEmpty();
        (await client.Sets.DifferenceAsync(missingKey, leftKey)).Should().BeEmpty();
        (await client.Sets.UnionAsync(leftKey)).Should().BeEquivalentTo("a", "b");
        await client.SetAsync(destinationKey, "old", expiry: TimeSpan.FromMinutes(1));
        (await client.Sets.UnionStoreAsync(destinationKey, leftKey, rightKey)).Should().Be(3);
        (await Number(client, "PTTL", destinationKey)).Should().Be(-1);
        (await client.Sets.IntersectStoreAsync(rightKey, leftKey, rightKey)).Should().Be(1);
        (await client.Sets.MembersAsync(rightKey)).Should().BeEquivalentTo("b");
        (await client.Sets.DifferenceStoreAsync(leftKey, leftKey, rightKey)).Should().Be(1);
        (await client.Sets.MembersAsync(leftKey)).Should().BeEquivalentTo("a");
        (await client.Sets.UnionStoreAsync(rightKey, rightKey, leftKey)).Should().Be(2);
        (await client.Sets.MembersAsync(rightKey)).Should().BeEquivalentTo("a", "b");
        (await client.Sets.IntersectStoreAsync(destinationKey, leftKey, missingKey)).Should().Be(0);
        (await client.ExistsAsync(destinationKey)).Should().BeFalse();
        (await client.Sets.MoveAsync(rightKey, rightKey, "a")).Should().BeTrue();
        (await client.Sets.MoveAsync(rightKey, rightKey, "absent")).Should().BeFalse();
        (await client.Sets.CountAsync(rightKey)).Should().Be(2);
        using var batch = client.CreateBatch();
        var stored = batch.Sets.UnionStore(destinationKey, leftKey, rightKey);
        var difference = batch.Sets.Difference(rightKey, leftKey);
        var count = batch.Sets.Count(destinationKey);
        await batch.ExecuteAsync();
        stored.Result.Should().Be(2);
        difference.Result.Should().BeEquivalentTo("b");
        count.Result.Should().Be(2);
    }

    [Test]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    public async Task WrongTypesCannotPartiallyMutateDestinations(bool useFake, int protocol)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        await using var client = await Connect(fake, protocol);
        var prefix = Guid.NewGuid().ToString("N") + ":";
        var wrongKey = prefix + "wrong";
        var setKey = prefix + "set";
        var destinationKey = prefix + "destination";
        var missingKey = prefix + "missing";
        await client.SetAsync(wrongKey, "string");
        await client.Sets.AddAsync(setKey, "member");
        await client.Sets.AddAsync(destinationKey, "unchanged");
        foreach (var (command, args) in new (string, RespireValue[])[]
        {
            ("SADD", [wrongKey, "x"]), ("SREM", [wrongKey, "x"]), ("SMEMBERS", [wrongKey]),
            ("SCARD", [wrongKey]), ("SISMEMBER", [wrongKey, "x"]), ("SMISMEMBER", [wrongKey, "x"]),
            ("SMOVE", [setKey, wrongKey, "absent"]), ("SMOVE", [wrongKey, destinationKey, "x"]),
            ("SINTER", [missingKey, wrongKey]), ("SUNION", [setKey, wrongKey]), ("SDIFF", [missingKey, wrongKey]),
            ("SINTERSTORE", [destinationKey, missingKey, wrongKey]),
            ("SUNIONSTORE", [destinationKey, setKey, wrongKey]), ("SDIFFSTORE", [destinationKey, setKey, wrongKey]),
            ("SINTERCARD", [2, missingKey, wrongKey, "LIMIT", 1]),
            ("GETDEL", [setKey]), ("GETEX", [setKey, "PERSIST"]), ("HSET", [setKey, "field", "x"]),
        })
        {
            Func<Task> run = async () => { using var ignored = await client.ExecuteAsync(command, args); };
            (await run.Should().ThrowAsync<RespireServerException>()).Which.Code.Should().Be("WRONGTYPE");
            (await client.Sets.MembersAsync(setKey)).Should().BeEquivalentTo("member");
            (await client.Sets.MembersAsync(destinationKey)).Should().BeEquivalentTo("unchanged");
            (await client.GetStringAsync(wrongKey)).Should().Be("string");
        }
        (await client.Sets.MoveAsync(missingKey, wrongKey, "member")).Should().BeFalse();
        using var missing = await client.ExecuteAsync("MGET", setKey);
        missing[0].IsNull.Should().BeTrue();
    }

    [Test]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    public async Task InvalidCountsAndArityLeaveSetsUnchanged(bool useFake, int protocol)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        await using var client = await Connect(fake, protocol);
        var prefix = Guid.NewGuid().ToString("N") + ":";
        var setKey = prefix + "set";
        var otherKey = prefix + "other";
        await client.Sets.AddAsync(setKey, "a", "b");
        foreach (var args in new RespireValue[][]
        {
            [0, setKey], [-1, setKey], [2, setKey], [long.MaxValue, setKey],
            ["not-a-number", setKey], [1, setKey, "LIMIT"], [1, setKey, "LIMIT", -1],
            [1, setKey, "LIMIT", "01"], [1, setKey, "UNKNOWN", 1], [1, setKey, "LIMIT", 1, "extra"],
        })
        {
            Func<Task> run = async () => { using var ignored = await client.ExecuteAsync("SINTERCARD", args); };
            (await run.Should().ThrowAsync<RespireServerException>()).Which.Code.Should().Be("ERR");
        }
        (await Number(client, "SINTERCARD", 1, setKey, "limit", 1, "LIMIT", 0)).Should().Be(2);
        foreach (var (command, args) in new (string, RespireValue[])[]
        {
            ("SADD", [setKey]), ("SREM", [setKey]), ("SMEMBERS", [setKey, "extra"]), ("SCARD", []),
            ("SISMEMBER", [setKey]), ("SMISMEMBER", [setKey]), ("SMOVE", [setKey, otherKey]),
            ("SINTER", []), ("SUNION", []), ("SDIFF", []), ("SINTERSTORE", [setKey]),
            ("SUNIONSTORE", [setKey]), ("SDIFFSTORE", [setKey]), ("SINTERCARD", [1]),
        })
        {
            Func<Task> run = async () => { using var ignored = await client.ExecuteAsync(command, args); };
            await run.Should().ThrowAsync<RespireServerException>().WithMessage("*wrong number of arguments*");
        }
        (await client.Sets.MembersAsync(setKey)).Should().BeEquivalentTo("a", "b");
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
