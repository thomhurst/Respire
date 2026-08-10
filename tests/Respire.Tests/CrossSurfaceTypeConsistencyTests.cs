using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class CrossSurfaceTypeConsistencyTests
{
    [Test]
    public async Task ImmediateRename_ReturnsConfirmationLikeDeferredRename()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var confirmed = await client.Keys.RenameAsync("old", "new");

        await Assert.That(confirmed).IsTrue();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["RENAME old new"]);
    }

    [Test]
    public async Task ImmediateWriteConfirmations_RejectUnexpectedReplies()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply, FakeRespServer.PongReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(async () => await client.Keys.RenameAsync("old", "new"))
            .ThrowsExactly<RespireException>();
        await Assert.That(async () => await client.Strings.SetManyAsync(("key", "value")))
            .ThrowsExactly<RespireException>();
    }

    [Test]
    public async Task StreamEntries_AreValueTypesLikeOtherResultEntries()
        => await Assert.That(typeof(RespireStreamEntry).IsValueType).IsTrue();
}
