using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
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
        _client = await RespireClient.ConnectAsync(_fixture.ConnectionString);
    }

    [After(HookType.Test)]
    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
    }

    [Test]
    public async Task PublishSubscribe_Roundtrip()
    {
        var channel = IsolatedChannel("it:chan");
        await using var subscription = _client.Subscribe(channel);
        var firstMessage = ReadFirstAsync(subscription);

        var receivers = await PublishUntilReceiversAsync(channel, "hello-integration", 1);

        receivers.Should().BeGreaterThanOrEqualTo(1);
        (await firstMessage.WaitAsync(TimeSpan.FromSeconds(5))).Text.Should().Be("hello-integration");
    }

    [Test]
    public async Task PatternSubscribe_Roundtrip()
    {
        var pattern = IsolatedChannel("it:p:*");
        var channel = IsolatedChannel("it:p:orders");
        await using var subscription = _client.SubscribePattern(pattern);
        var firstMessage = ReadFirstAsync(subscription);

        await PublishUntilReceiversAsync(channel, "pattern-payload", 1);

        var message = await firstMessage.WaitAsync(TimeSpan.FromSeconds(5));
        message.Channel.Should().Be(channel);
        message.Pattern.Should().Be(pattern);
        message.Text.Should().Be("pattern-payload");
    }

    [Test]
    public async Task ShardedPublishSubscribe_Roundtrip()
    {
        var channel = IsolatedChannel("it:shard");
        await using var subscription = _client.SubscribeSharded(channel);
        var firstMessage = ReadFirstAsync(subscription);

        var receivers = await PublishUntilReceiversAsync(channel, "sharded-payload", 1, sharded: true);

        receivers.Should().Be(1);
        var message = await firstMessage.WaitAsync(TimeSpan.FromSeconds(5));
        message.Channel.Should().Be(channel);
        message.Pattern.Should().BeNull();
        message.Text.Should().Be("sharded-payload");
    }

    [Test]
    public async Task Unsubscribe_StopsDelivery()
    {
        var channel = IsolatedChannel("it:bye");
        var subscription = _client.Subscribe(channel);

        var deliveries = 0;
        var reader = Task.Run(async () =>
        {
            await foreach (var _ in subscription)
            {
                Interlocked.Increment(ref deliveries);
            }
        });

        // Wait until the SUBSCRIBE is active, then unsubscribe by disposing the subscription.
        (await PublishUntilReceiversAsync(channel, "warm-up", 1)).Should().Be(1);
        await subscription.DisposeAsync();

        // Disposal ends the enumeration; once the reader finishes, nothing can deliver anymore.
        await reader.WaitAsync(TimeSpan.FromSeconds(5));
        var deliveredBeforeUnsubscribe = Volatile.Read(ref deliveries);

        var receivers = await _client.PublishAsync(channel, "should-not-arrive");

        receivers.Should().Be(0);
        await Task.Delay(200);
        Volatile.Read(ref deliveries).Should().Be(deliveredBeforeUnsubscribe);
    }

    [Test]
    public async Task Resp3Subscriber_ReceivesPushFrames()
    {
        var channel = IsolatedChannel("it:resp3");
        await using var resp3Client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint(_fixture.Host, _fixture.Port) },
            Database = _fixture.Database,
            Protocol = RespProtocol.Resp3,
            Connections = 1,
        });
        await using var subscription = resp3Client.Subscribe(channel);
        var firstMessage = ReadFirstAsync(subscription);

        await PublishUntilReceiversAsync(channel, "resp3-push", 1);

        (await firstMessage.WaitAsync(TimeSpan.FromSeconds(5))).Text.Should().Be("resp3-push");
    }

    [Test]
    public async Task MultipleSubscribers_AllReceive()
    {
        var channel = IsolatedChannel("it:fanout");
        // Each client routes its subscriptions over one dedicated pub/sub connection, so two
        // clients are needed for the server to count two receivers.
        await using var secondClient = await RespireClient.ConnectAsync(_fixture.ConnectionString);
        await using var subscription1 = _client.Subscribe(channel);
        await using var subscription2 = secondClient.Subscribe(channel);
        var firstMessage1 = ReadFirstAsync(subscription1);
        var firstMessage2 = ReadFirstAsync(subscription2);

        var receivers = await PublishUntilReceiversAsync(channel, "to-everyone", 2);

        receivers.Should().Be(2);
        (await firstMessage1.WaitAsync(TimeSpan.FromSeconds(5))).Text.Should().Be("to-everyone");
        (await firstMessage2.WaitAsync(TimeSpan.FromSeconds(5))).Text.Should().Be("to-everyone");
    }

    [Test]
    public async Task SubscriberConnectionDeath_ReconnectsResubscribesAndReportsState()
    {
        var channel = IsolatedChannel("it:reconnect");
        var clientName = $"respire-pubsub-{Guid.NewGuid():N}";
        await using var resilientClient = await RespireClient.ConnectAsync(
            RespireOptions.Parse(_fixture.ConnectionString) with { ClientName = clientName });
        var states = new List<RespireConnectionState>();
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        resilientClient.ConnectionStateChanged += state =>
        {
            lock (states)
            {
                states.Add(state);
            }

            if (state == RespireConnectionState.Connected)
            {
                connected.TrySetResult();
            }
        };

        await using var subscription = resilientClient.Subscribe(channel);
        var firstMessage = ReadFirstAsync(subscription);
        await PublishUntilReceiversAsync(channel, "before-disconnect", 1);
        (await firstMessage.WaitAsync(TimeSpan.FromSeconds(5))).Text.Should().Be("before-disconnect");

        using var clientList = await _client.ExecuteAsync("CLIENT", "LIST");
        var subscriberLine = clientList.AsString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.Contains($"name={clientName}", StringComparison.Ordinal)
                && line.Contains("cmd=subscribe", StringComparison.Ordinal));
        var subscriberId = subscriberLine.Split(' ')
            .Single(field => field.StartsWith("id=", StringComparison.Ordinal))[3..];

        using (var killed = await _client.ExecuteAsync("CLIENT", "KILL", "ID", subscriberId))
        {
            killed.AsInteger().Should().Be(1);
        }

        var reconnectedMessage = ReadFirstAsync(subscription);
        await PublishUntilReceiversAsync(channel, "after-reconnect", 1);

        (await reconnectedMessage.WaitAsync(TimeSpan.FromSeconds(5))).Text.Should().Be("after-reconnect");
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lock (states)
        {
            states.Should().ContainInOrder(
                RespireConnectionState.Reconnecting,
                RespireConnectionState.Connected);
        }
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

    private static string IsolatedChannel(string channel)
        => $"{TestContext.Current!.Isolation.GetIsolatedPrefix()}{channel}";

    /// <summary>
    /// SUBSCRIBE is sent when enumeration starts, so publish in a loop until the server reports
    /// the expected receiver count — PUBLISH's return value is the readiness signal.
    /// </summary>
    private async Task<long> PublishUntilReceiversAsync(
        string channel, string payload, long expectedReceivers, bool sharded = false)
    {
        long receivers = -1;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            receivers = sharded
                ? await _client.PublishShardedAsync(channel, payload)
                : await _client.PublishAsync(channel, payload);
            if (receivers == expectedReceivers)
            {
                return receivers;
            }

            await Task.Delay(50);
        }

        return receivers;
    }
}
