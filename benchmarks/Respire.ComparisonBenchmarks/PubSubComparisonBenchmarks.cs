using BenchmarkDotNet.Attributes;
using DotNet.Testcontainers.Containers;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Respire.ComparisonBenchmarks;

/// <summary>One publication through delivery to a single consuming subscriber.</summary>
[MemoryDiagnoser]
public class PubSubComparisonBenchmarks
{
    private IContainer? _redisContainer;
    private RespireClient _respire = null!;
    private RespireSubscription _subscription = null!;
    private IAsyncEnumerator<RespireMessage> _messages = null!;
    private ConnectionMultiplexer _stackExchange = null!;
    private ISubscriber _subscriber = null!;
    private ChannelMessageQueue _queue = null!;
    private readonly string _respireChannel = $"respire:benchmark:pubsub:{Guid.NewGuid():N}";
    private readonly RedisChannel _stackExchangeChannel = RedisChannel.Literal($"respire:benchmark:pubsub:{Guid.NewGuid():N}");
    private readonly string _payload = "Hello, subscriber!";

    [GlobalSetup]
    public async Task Setup()
    {
        var host = Environment.GetEnvironmentVariable("REDIS_HOST");
        var port = int.TryParse(Environment.GetEnvironmentVariable("REDIS_PORT"), out var configuredPort)
            ? configuredPort : 6379;
        if (string.IsNullOrEmpty(host))
        {
            _redisContainer = new RedisBuilder("redis:8.10").Build();
            await _redisContainer.StartAsync();
            host = "localhost";
            port = _redisContainer.GetMappedPublicPort(6379);
        }

        // Use RESP3 on both clients so the 3.4 dedicated pub/sub connection is exercised.
        _respire = await RespireClient.ConnectAsync(
            RespireOptions.Parse($"{host}:{port}") with { Protocol = RespProtocol.Resp3 });
        var options = ConfigurationOptions.Parse($"{host}:{port}");
        options.Protocol = RedisProtocol.Resp3;
        _stackExchange = await ConnectionMultiplexer.ConnectAsync(options);
        _subscriber = _stackExchange.GetSubscriber();
        _subscription = await _respire.SubscribeAsync(_respireChannel);
        _messages = _subscription.GetAsyncEnumerator();
        _queue = await _subscriber.SubscribeAsync(_stackExchangeChannel);

        // Verify delivery and drain both queues before timing. A setup failure is bounded.
        if (await Respire_PublishAndConsume().WaitAsync(TimeSpan.FromSeconds(10)) != _payload ||
            await StackExchange_PublishAndConsume().WaitAsync(TimeSpan.FromSeconds(10)) != _payload)
        {
            throw new InvalidOperationException("Pub/sub did not deliver the expected payload.");
        }
    }

    [Benchmark(Baseline = true)]
    public async Task<string?> StackExchange_PublishAndConsume()
    {
        await _subscriber.PublishAsync(_stackExchangeChannel, _payload);
        var message = await _queue.ReadAsync();
        return message.Message;
    }

    [Benchmark]
    public async Task<string?> Respire_PublishAndConsume()
    {
        await _respire.PublishAsync(_respireChannel, _payload);
        if (!await _messages.MoveNextAsync())
        {
            throw new InvalidOperationException("Subscription ended before delivery.");
        }
        return _messages.Current.Text;
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _messages.DisposeAsync();
        await _subscription.DisposeAsync();
        await _queue.UnsubscribeAsync();
        await _respire.DisposeAsync();
        await _stackExchange.DisposeAsync();
        if (_redisContainer is not null)
        {
            await _redisContainer.DisposeAsync();
        }
    }
}
