using StackExchange.Redis;
using TUnit.Core;
using Assert = Xunit.Assert;

namespace Respire.StackExchangeCompat.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class ShardedChannelTests(RedisTestContainer redis)
{
    [Test]
    public async Task SubscribeRejectsShardedChannelWithoutRegisteringLiteralSubscription()
    {
        await using var connection = RespireConnectionMultiplexer.Create(RespireOptions.Parse(redis.IsolatedConnectionString));
        var subscriber = connection.GetSubscriber();
        string name = $"events:{Guid.NewGuid():N}";
        var sharded = RedisChannel.Sharded(name);
        Action<RedisChannel, RedisValue> handler = (_, _) =>
        {
        };

        await Assert.ThrowsAsync<NotSupportedException>(() => subscriber.SubscribeAsync(sharded, handler));
        Assert.Throws<NotSupportedException>(() => subscriber.Subscribe(sharded, handler));
        Assert.Equal(0, await subscriber.PublishAsync(RedisChannel.Literal(name), "ordinary"));
    }

    [Test]
    public async Task UnsubscribeRejectsShardedChannelAndKeepsLiteralSubscription()
    {
        await using var connection = RespireConnectionMultiplexer.Create(RespireOptions.Parse(redis.IsolatedConnectionString));
        var subscriber = connection.GetSubscriber();
        string name = $"events:{Guid.NewGuid():N}";
        var literal = RedisChannel.Literal(name);
        var sharded = RedisChannel.Sharded(name);
        var received = new TaskCompletionSource<RedisValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        await subscriber.SubscribeAsync(literal, (_, value) => received.TrySetResult(value));

        await Assert.ThrowsAsync<NotSupportedException>(() => subscriber.UnsubscribeAsync(sharded));
        Assert.Throws<NotSupportedException>(() => subscriber.Unsubscribe(sharded));
        Assert.Equal(1, await subscriber.PublishAsync(literal, "ordinary"));
        Assert.Equal((RedisValue)"ordinary", await received.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await subscriber.UnsubscribeAsync(literal);
        Assert.Equal(0, await subscriber.PublishAsync(literal, "after unsubscribe"));
    }

    [Test]
    public async Task PublishRejectsShardedChannelBeforeSendingOrQueuing()
    {
        await using var connection = RespireConnectionMultiplexer.Create(RespireOptions.Parse(redis.IsolatedConnectionString));
        var subscriber = connection.GetSubscriber();
        var database = connection.GetDatabase();
        var transaction = database.CreateTransaction();
        string name = $"events:{Guid.NewGuid():N}";
        var sharded = RedisChannel.Sharded(name);

        await Assert.ThrowsAsync<NotSupportedException>(() => subscriber.PublishAsync(sharded, "sharded"));
        Assert.Throws<NotSupportedException>(() => subscriber.Publish(sharded, "sharded"));
        await Assert.ThrowsAsync<NotSupportedException>(() => database.PublishAsync(sharded, "sharded"));
        Assert.Throws<NotSupportedException>(() => database.Publish(sharded, "sharded"));
        await Assert.ThrowsAsync<NotSupportedException>(() => transaction.PublishAsync(sharded, "sharded"));

        var literalPublish = transaction.PublishAsync(RedisChannel.Literal(name), "ordinary");
        Assert.True(await transaction.ExecuteAsync());
        Assert.Equal(0, await literalPublish);
    }
}
