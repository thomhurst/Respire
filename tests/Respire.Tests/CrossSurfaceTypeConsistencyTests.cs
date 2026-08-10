using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class CrossSurfaceTypeConsistencyTests
{
    [Test]
    public async Task UnconditionalWrites_DoNotExposeMeaninglessBooleanResults()
    {
        var rename = typeof(IKeyCommands).GetMethod(nameof(IKeyCommands.RenameAsync))!;
        var setManyReturnTypes = typeof(IStringCommands).GetMethods()
            .Where(method => method.Name == nameof(IStringCommands.SetManyAsync))
            .Select(method => method.ReturnType)
            .ToArray();

        await Assert.That(rename.ReturnType).IsEqualTo(typeof(ValueTask));
        await Assert.That(setManyReturnTypes).IsEquivalentTo([typeof(ValueTask), typeof(ValueTask)]);

        await using var server = new FakeRespServer(FakeRespServer.OkReply, FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await client.Keys.RenameAsync("old", "new");
        await client.Strings.SetManyAsync(("key", "value"));

        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["RENAME old new", "MSET key value"]);
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
