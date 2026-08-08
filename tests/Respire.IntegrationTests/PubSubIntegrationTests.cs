using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
[NotInParallel("redis-integration")]
public class PubSubIntegrationTests
{
    private readonly RedisTestContainer _fixture;
    private RespireClient _client = null!;

    public PubSubIntegrationTests(RedisTestContainer fixture)
    {
        _fixture = fixture;
    }

    [Before(HookType.Test)]
    public async Task InitializeAsync()
    {
        _client = await RespireClient.ConnectAsync($"{_fixture.Host}:{_fixture.Port}");
    }

    [After(HookType.Test)]
    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
    }

    [Test]
    public async Task PublishSubscribe_Roundtrip()
    {
        await using var subscription = _client.Subscribe("it:chan");
        var firstMessage = ReadFirstAsync(subscription);

        var receivers = await PublishUntilReceiversAsync("it:chan", "hello-integration", 1);

        receivers.Should().BeGreaterThanOrEqualTo(1);
        (await firstMessage.WaitAsync(TimeSpan.FromSeconds(5))).Text.Should().Be("hello-integration");
    }

    [Test]
    public async Task PatternSubscribe_Roundtrip()
    {
        await using var subscription = _client.SubscribePattern("it:p:*");
        var firstMessage = ReadFirstAsync(subscription);

        await PublishUntilReceiversAsync("it:p:orders", "pattern-payload", 1);

        var message = await firstMessage.WaitAsync(TimeSpan.FromSeconds(5));
        message.Channel.Should().Be("it:p:orders");
        message.Pattern.Should().Be("it:p:*");
        message.Text.Should().Be("pattern-payload");
    }

    [Test]
    public async Task Unsubscribe_StopsDelivery()
    {
        var subscription = _client.Subscribe("it:bye");

        var deliveries = 0;
        var reader = Task.Run(async () =>
        {
            await foreach (var _ in subscription)
            {
                Interlocked.Increment(ref deliveries);
            }
        });

        // Wait until the SUBSCRIBE is active, then unsubscribe by disposing the subscription.
        (await PublishUntilReceiversAsync("it:bye", "warm-up", 1)).Should().Be(1);
        await subscription.DisposeAsync();

        // Disposal ends the enumeration; once the reader finishes, nothing can deliver anymore.
        await reader.WaitAsync(TimeSpan.FromSeconds(5));
        var deliveredBeforeUnsubscribe = Volatile.Read(ref deliveries);

        var receivers = await _client.PublishAsync("it:bye", "should-not-arrive");

        receivers.Should().Be(0);
        await Task.Delay(200);
        Volatile.Read(ref deliveries).Should().Be(deliveredBeforeUnsubscribe);
    }

    [Test]
    public async Task Resp3Subscriber_ReceivesPushFrames()
    {
        await using var resp3Client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint(_fixture.Host, _fixture.Port) },
            Protocol = RespProtocol.Resp3,
            Connections = 1,
        });
        await using var subscription = resp3Client.Subscribe("it:resp3");
        var firstMessage = ReadFirstAsync(subscription);

        await PublishUntilReceiversAsync("it:resp3", "resp3-push", 1);

        (await firstMessage.WaitAsync(TimeSpan.FromSeconds(5))).Text.Should().Be("resp3-push");
    }

    [Test]
    public async Task MultipleSubscribers_AllReceive()
    {
        // Each client routes its subscriptions over one dedicated pub/sub connection, so two
        // clients are needed for the server to count two receivers.
        await using var secondClient = await RespireClient.ConnectAsync($"{_fixture.Host}:{_fixture.Port}");
        await using var subscription1 = _client.Subscribe("it:fanout");
        await using var subscription2 = secondClient.Subscribe("it:fanout");
        var firstMessage1 = ReadFirstAsync(subscription1);
        var firstMessage2 = ReadFirstAsync(subscription2);

        var receivers = await PublishUntilReceiversAsync("it:fanout", "to-everyone", 2);

        receivers.Should().Be(2);
        (await firstMessage1.WaitAsync(TimeSpan.FromSeconds(5))).Text.Should().Be("to-everyone");
        (await firstMessage2.WaitAsync(TimeSpan.FromSeconds(5))).Text.Should().Be("to-everyone");
    }

    /// <summary>Starts consuming the subscription and completes with the first message received.</summary>
    private static Task<RespireMessage> ReadFirstAsync(RespireSubscription subscription)
        => Task.Run(async () =>
        {
            await foreach (var message in subscription)
            {
                return message;
            }

            throw new InvalidOperationException("The subscription ended before a message arrived.");
        });

    /// <summary>
    /// SUBSCRIBE is sent when enumeration starts, so publish in a loop until the server reports
    /// the expected receiver count — PUBLISH's return value is the readiness signal.
    /// </summary>
    private async Task<long> PublishUntilReceiversAsync(string channel, string payload, long expectedReceivers)
    {
        long receivers = -1;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            receivers = await _client.PublishAsync(channel, payload);
            if (receivers == expectedReceivers)
            {
                return receivers;
            }

            await Task.Delay(50);
        }

        return receivers;
    }
}
