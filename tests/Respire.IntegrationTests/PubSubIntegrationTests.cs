using FluentAssertions;
using Respire.FastClient;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestFixture>(Shared = SharedType.Keyed)]
[NotInParallel("redis-integration")]
public class PubSubIntegrationTests
{
    private readonly RedisTestFixture _fixture;
    private RespireClient _client = null!;

    public PubSubIntegrationTests(RedisTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Before(HookType.Test)]
    public async Task InitializeAsync()
    {
        _client = await RespireClient.CreateAsync(_fixture.Host, _fixture.Port);
    }

    [After(HookType.Test)]
    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
    }

    [Test]
    public async Task PublishSubscribe_Roundtrip()
    {
        await using var subscriber = await _client.CreateSubscriberAsync();

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await subscriber.SubscribeAsync("it:chan", (string _, in RespireValue message) =>
            received.TrySetResult(message.AsString()));

        var receivers = await _client.PublishAsync("it:chan", "hello-integration");

        receivers.Should().BeGreaterThanOrEqualTo(1);
        (await received.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be("hello-integration");
    }

    [Test]
    public async Task PatternSubscribe_Roundtrip()
    {
        await using var subscriber = await _client.CreateSubscriberAsync();

        var received = new TaskCompletionSource<(string Channel, string Payload)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await subscriber.PSubscribeAsync("it:p:*", (string channel, in RespireValue message) =>
            received.TrySetResult((channel, message.AsString())));

        await _client.PublishAsync("it:p:orders", "pattern-payload");

        var (channel, payload) = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        channel.Should().Be("it:p:orders");
        payload.Should().Be("pattern-payload");
    }

    [Test]
    public async Task Unsubscribe_StopsDelivery()
    {
        await using var subscriber = await _client.CreateSubscriberAsync();

        var deliveries = 0;
        await subscriber.SubscribeAsync("it:bye", (string _, in RespireValue _) =>
            Interlocked.Increment(ref deliveries));
        await subscriber.UnsubscribeAsync("it:bye");

        var receivers = await _client.PublishAsync("it:bye", "should-not-arrive");

        receivers.Should().Be(0);
        await Task.Delay(200);
        deliveries.Should().Be(0);
    }

    [Test]
    public async Task Resp3Subscriber_ReceivesPushFrames()
    {
        await using var subscriber = await RespireSubscriber.CreateAsync(
            _fixture.Host, _fixture.Port, new RespireConnectionOptions { UseResp3 = true });

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await subscriber.SubscribeAsync("it:resp3", (string _, in RespireValue message) =>
            received.TrySetResult(message.AsString()));

        await _client.PublishAsync("it:resp3", "resp3-push");

        (await received.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be("resp3-push");
    }

    [Test]
    public async Task MultipleSubscribers_AllReceive()
    {
        await using var subscriber1 = await _client.CreateSubscriberAsync();
        await using var subscriber2 = await _client.CreateSubscriberAsync();

        var received1 = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var received2 = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await subscriber1.SubscribeAsync("it:fanout", (string _, in RespireValue m) => received1.TrySetResult(m.AsString()));
        await subscriber2.SubscribeAsync("it:fanout", (string _, in RespireValue m) => received2.TrySetResult(m.AsString()));

        var receivers = await _client.PublishAsync("it:fanout", "to-everyone");

        receivers.Should().Be(2);
        (await received1.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be("to-everyone");
        (await received2.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be("to-everyone");
    }
}
