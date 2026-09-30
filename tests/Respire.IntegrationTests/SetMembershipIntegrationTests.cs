using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class SetMembershipIntegrationTests(RedisTestContainer fixture)
{
    [Test]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task ContainsMany_PreservesBinaryMembersOrderAndMissingValues(int protocol, bool useFake)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        var options = fake?.CreateOptions() ?? RespireOptions.Parse(fixture.ConnectionString);
        await using var root = await RespireClient.ConnectAsync(options with { Protocol = (RespProtocol)protocol });
        var client = root.WithKeyPrefix(Guid.NewGuid().ToString("N") + ":");
        byte[] binary = [0xff, 0x00, 0x42];
        await client.Sets.AddAsync("members", binary, "text");
        (await client.Sets.ContainsManyAsync("members", binary, "missing", binary, "text"))
            .Should().Equal(true, false, true, true);
        (await client.Sets.ContainsManyAsync("missing", binary, "text")).Should().Equal(false, false);
    }

    [Test]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task Move_TransfersBinaryMemberAndHandlesMissingOrExistingDestination(int protocol, bool useFake)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        var options = fake?.CreateOptions() ?? RespireOptions.Parse(fixture.ConnectionString);
        await using var root = await RespireClient.ConnectAsync(options with { Protocol = (RespProtocol)protocol });
        var client = root.WithKeyPrefix(Guid.NewGuid().ToString("N") + ":");
        byte[] binary = [0xff, 0x00, 0x42];
        await client.Sets.AddAsync("source", binary, "shared");
        await client.Sets.AddAsync("destination", "shared");
        (await client.Sets.MoveAsync("source", "destination", binary)).Should().BeTrue();
        (await client.Sets.MoveAsync("source", "destination", binary)).Should().BeFalse();
        (await client.Sets.MoveAsync("source", "destination", "shared")).Should().BeTrue();
        (await client.Sets.MoveAsync("missing", "destination", "absent")).Should().BeFalse();
        (await client.Sets.ContainsManyAsync("source", binary, "shared")).Should().Equal(false, false);
        (await client.Sets.ContainsManyAsync("destination", binary, "shared")).Should().Equal(true, true);
    }

    [Test]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task IntersectCount_RespectsLimitAndMissingKeys(int protocol, bool useFake)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        var options = fake?.CreateOptions() ?? RespireOptions.Parse(fixture.ConnectionString);
        await using var root = await RespireClient.ConnectAsync(options with { Protocol = (RespProtocol)protocol });
        var client = root.WithKeyPrefix(Guid.NewGuid().ToString("N") + ":");
        await client.Sets.AddAsync("first", "a", "b", "c");
        await client.Sets.AddAsync("second", "b", "c", "d");
        (await client.Sets.IntersectCountAsync("first", "second")).Should().Be(2);
        (await client.Sets.IntersectCountAsync(1, ["first", "second"])).Should().Be(1);
        (await client.Sets.IntersectCountAsync(0, ["first", "second"])).Should().Be(2);
        (await client.Sets.IntersectCountAsync(10, ["first", "second"])).Should().Be(2);
        (await client.Sets.IntersectCountAsync("first", "missing")).Should().Be(0);
        (await client.Sets.IntersectCountAsync("first")).Should().Be(3);
    }

    [Test]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task WrongTypeErrors_ArePreserved(int protocol, bool useFake)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        var options = fake?.CreateOptions() ?? RespireOptions.Parse(fixture.ConnectionString);
        await using var root = await RespireClient.ConnectAsync(options with { Protocol = (RespProtocol)protocol });
        var client = root.WithKeyPrefix(Guid.NewGuid().ToString("N") + ":");
        await client.SetAsync("wrong", "string");
        await client.Sets.AddAsync("set", "member");
        Func<Task>[] commands =
        [
            async () => { await client.Sets.ContainsManyAsync("wrong", "member"); },
            async () => { await client.Sets.MoveAsync("set", "wrong", "member"); },
            async () => { await client.Sets.IntersectCountAsync("set", "wrong"); },
        ];
        foreach (var command in commands)
        {
            var error = await command.Should().ThrowAsync<RespireServerException>();
            error.Which.Code.Should().Be("WRONGTYPE");
        }
        (await client.Sets.ContainsAsync("set", "member")).Should().BeTrue();
    }

    [Test]
    [Arguments(2, false, false)]
    [Arguments(2, false, true)]
    [Arguments(3, false, false)]
    [Arguments(3, false, true)]
    [Arguments(2, true, false)]
    [Arguments(3, true, false)]
    public async Task DeferredCommands_ReturnOrderedResults(int protocol, bool useFake, bool transactional)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        var options = fake?.CreateOptions() ?? RespireOptions.Parse(fixture.ConnectionString);
        await using var root = await RespireClient.ConnectAsync(options with { Protocol = (RespProtocol)protocol });
        var client = root.WithKeyPrefix(Guid.NewGuid().ToString("N") + ":");
        await client.Sets.AddAsync("first", "a", "b", "c");
        await client.Sets.AddAsync("second", "b", "c");
        using var batch = transactional ? null : client.CreateBatch();
        await using var transaction = transactional ? client.CreateTransaction() : null;
        IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
        var members = queue.Sets.ContainsMany("first", "a", "missing", "a");
        var count = queue.Sets.IntersectCount("first", "second");
        var limited = queue.Sets.IntersectCount(1, ["first", "second"]);
        var moved = queue.Sets.Move("first", "second", "a");
        var afterMove = queue.Sets.ContainsMany("second", "a", "b", "c");
        if (transaction is not null)
            await transaction.CommitAsync();
        else
            await batch!.ExecuteAsync();
        members.Result.Should().Equal(true, false, true);
        count.Result.Should().Be(2);
        limited.Result.Should().Be(1);
        moved.Result.Should().BeTrue();
        afterMove.Result.Should().Equal(true, true, true);
    }
}
