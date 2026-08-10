using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ListCountPopTests
{
    [Test]
    public async Task CountPops_SendCommandsAndParseStringAndTypedArrays()
    {
        await using var server = new FakeRespServer(
            "*2\r\n$3\r\none\r\n$3\r\ntwo\r\n"u8.ToArray(),
            "*1\r\n$5\r\nthree\r\n"u8.ToArray(),
            "*2\r\n$1\r\n1\r\n$1\r\n2\r\n"u8.ToArray(),
            "*1\r\n$1\r\n3\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var left = await client.Lists.LeftPopManyAsync("jobs", 2);
        var right = await client.Lists.RightPopManyAsync("jobs", 1);
        var typedLeft = await client.Lists.LeftPopManyAsync<int>("numbers", 2);
        var typedRight = await client.Lists.RightPopManyAsync<int>("numbers", 1);

        await Assert.That(left).IsEquivalentTo(new[] { "one", "two" });
        await Assert.That(right).IsEquivalentTo(new[] { "three" });
        await Assert.That(typedLeft).IsEquivalentTo(new[] { 1, 2 });
        await Assert.That(typedRight).IsEquivalentTo(new[] { 3 });
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "LPOP jobs 2",
            "RPOP jobs 1",
            "LPOP numbers 2",
            "RPOP numbers 1",
        });
    }

    [Test]
    public async Task DeferredCountPops_SendCommandsAndParseStringAndTypedArrays()
    {
        await using var server = new FakeRespServer(
            "*2\r\n$3\r\none\r\n$3\r\ntwo\r\n"u8.ToArray(),
            "*1\r\n$5\r\nthree\r\n"u8.ToArray(),
            "*2\r\n$1\r\n1\r\n$1\r\n2\r\n"u8.ToArray(),
            "*1\r\n$1\r\n3\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var batch = client.CreateBatch();

        var left = batch.Lists.LeftPopMany("jobs", 2);
        var right = batch.Lists.RightPopMany("jobs", 1);
        var typedLeft = batch.Lists.LeftPopMany<int>("numbers", 2);
        var typedRight = batch.Lists.RightPopMany<int>("numbers", 1);
        await batch.ExecuteAsync();

        await Assert.That(left.Result).IsEquivalentTo(new[] { "one", "two" });
        await Assert.That(right.Result).IsEquivalentTo(new[] { "three" });
        await Assert.That(typedLeft.Result).IsEquivalentTo(new[] { 1, 2 });
        await Assert.That(typedRight.Result).IsEquivalentTo(new[] { 3 });
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "LPOP jobs 2",
            "RPOP jobs 1",
            "LPOP numbers 2",
            "RPOP numbers 1",
        });
    }

    [Test]
    public async Task CountPops_RejectNegativeCountsBeforeSending()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(async () => await client.Lists.LeftPopManyAsync("jobs", -1))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Lists.RightPopManyAsync<int>("jobs", -1))
            .ThrowsExactly<ArgumentOutOfRangeException>();

        var batch = client.CreateBatch();
        await Assert.That(() => batch.Lists.LeftPopMany("jobs", -1))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(() => batch.Lists.RightPopMany<int>("jobs", -1))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }
}
