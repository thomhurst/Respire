using FluentAssertions;
using Respire.Protocol;
using Respire.TestSupport;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.PerTestSession)]
public class FakeTransactionPubSubParityTests(ModernRedisTestContainer fixture)
{
    [Test]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    public async Task PublicationWaitsForExecAndDoesNotInvalidateWatch(bool useFake, int protocol)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        var options = Options(fake, protocol);
        // Pub/sub channels span Redis databases, unlike the fixture's isolated keys.
        var channel = $"transaction:{Guid.NewGuid():N}";
        await using var subscriber = await TestRespSession.ConnectAsync(options);
        await using var transaction = await TestRespSession.ConnectAsync(options);
        await using var publisher = await RespireClient.ConnectAsync(options);
        using (var subscribed = await subscriber.CommandAsync("SUBSCRIBE", channel))
            subscribed.AsArray()[0].AsString().Should().Be("subscribe");
        await Text(transaction, "OK", "WATCH", channel);
        await Text(transaction, "OK", "MULTI");
        await Text(transaction, "QUEUED", "PUBLISH", channel, "first");
        await Text(transaction, "QUEUED", "PUBLISH", channel, "second");
        (await publisher.PublishAsync(channel, "before exec")).Should().Be(1);
        await Message(subscriber, channel, "before exec");
        using (var executed = await transaction.CommandAsync("EXEC"))
        {
            executed.Type.Should().Be(RespDataType.Array);
            executed.AsArray().Length.Should().Be(2);
            executed.AsArray()[0].AsInteger().Should().Be(1);
            executed.AsArray()[1].AsInteger().Should().Be(1);
        }
        await Message(subscriber, channel, "first");
        await Message(subscriber, channel, "second");
        await Text(transaction, "PONG", "PING");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SubscribedResp3SessionReceivesExecReplyBeforeSelfPublication(bool useFake)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        await using var session = await TestRespSession.ConnectAsync(Options(fake, 3));
        var channel = $"self:{Guid.NewGuid():N}";
        using (var subscribed = await session.CommandAsync("SUBSCRIBE", channel))
            subscribed.Type.Should().Be(RespDataType.Push);
        await Text(session, "OK", "MULTI");
        await Text(session, "QUEUED", "PUBLISH", channel, "payload");
        await Text(session, "QUEUED", "PING");
        using (var executed = await session.CommandAsync("EXEC"))
        {
            executed.Type.Should().Be(RespDataType.Array);
            executed.AsArray()[0].AsInteger().Should().Be(1);
            executed.AsArray()[1].AsString().Should().Be("PONG");
        }
        await Message(session, channel, "payload");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SelfPublicationFollowsItsCommandReply(bool useFake)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        await using var session = await TestRespSession.ConnectAsync(Options(fake, 3));
        var channel = $"self:{Guid.NewGuid():N}";
        using (var subscribed = await session.CommandAsync("SUBSCRIBE", channel)) { }
        using (var published = await session.CommandAsync("PUBLISH", channel, "payload"))
            published.AsInteger().Should().Be(1);
        await Message(session, channel, "payload");
        await Text(session, "PONG", "PING");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Resp2SubscribedModeRejectsTransactionUntilUnsubscribed(bool useFake)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        await using var session = await TestRespSession.ConnectAsync(Options(fake, 2));
        var channel = $"mode:{Guid.NewGuid():N}";
        using (var subscribed = await session.CommandAsync("SUBSCRIBE", channel)) { }
        using (var rejected = await session.CommandAsync("MULTI"))
            rejected.GetErrorMessage().Should().Contain("Can't execute 'multi'");
        using (var unsubscribed = await session.CommandAsync("UNSUBSCRIBE", channel)) { }
        await Text(session, "OK", "MULTI");
        await Text(session, "QUEUED", "PING");
        using var executed = await session.CommandAsync("EXEC");
        executed.AsArray()[0].AsString().Should().Be("PONG");
    }

    private RespireOptions Options(RespireFakeServer? fake, int protocol)
        => (fake?.CreateOptions() ?? RespireOptions.Parse(fixture.ConnectionString)) with { Protocol = (RespProtocol)protocol };

    private static async Task Text(TestRespSession session, string expected, params string[] arguments)
    {
        using var reply = await session.CommandAsync(arguments);
        reply.AsString().Should().Be(expected);
    }

    private static async Task Message(TestRespSession session, string channel, string payload)
    {
        using var reply = await session.ReadAsync();
        reply.AsArray()[0].AsString().Should().Be("message");
        reply.AsArray()[1].AsString().Should().Be(channel);
        reply.AsArray()[2].AsString().Should().Be(payload);
    }
}
