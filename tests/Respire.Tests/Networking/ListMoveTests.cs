using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ListMoveTests
{
    [Test]
    [Arguments((RespireListSide)42, RespireListSide.Right, "from")]
    [Arguments(RespireListSide.Left, (RespireListSide)42, "to")]
    public async Task MoveAsync_RejectsInvalidSideBeforeSending(RespireListSide from, RespireListSide to, string parameterName)
    {
        await using var server = new FakeRespServer(2, "$4\r\ntail\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var immediate = await Assert.That(async () => await client.Lists.MoveAsync("source", "destination", from, to))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        var blocking = await Assert.That(
                async () => await client.Lists.MoveAsync("source", "destination", TimeSpan.FromSeconds(1), from, to))
            .ThrowsExactly<ArgumentOutOfRangeException>();

        await Assert.That(immediate!.ParamName).IsEqualTo(parameterName);
        await Assert.That(blocking!.ParamName).IsEqualTo(parameterName);
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    [Arguments((RespireListSide)42, RespireListSide.Right, "from")]
    [Arguments(RespireListSide.Left, (RespireListSide)42, "to")]
    public async Task Move_RejectsInvalidSideBeforeQueuing(RespireListSide from, RespireListSide to, string parameterName)
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();

        var batched = await Assert.That(() => batch.Lists.Move("source", "destination", from, to))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        var transacted = await Assert.That(() => transaction.Lists.Move("source", "destination", from, to))
            .ThrowsExactly<ArgumentOutOfRangeException>();

        await Assert.That(batched!.ParamName).IsEqualTo(parameterName);
        await Assert.That(transacted!.ParamName).IsEqualTo(parameterName);
        await Assert.That(batch.Count).IsEqualTo(0);
        await Assert.That(transaction.Count).IsEqualTo(0);
    }
}
