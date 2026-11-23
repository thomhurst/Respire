using StackExchange.Redis;
using TUnit.Core;
using Assert = Xunit.Assert;

namespace Respire.StackExchangeCompat.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class SubscriptionTeardownTests(RedisTestContainer fixture)
{
    [Test]
    [Arguments(2, false)]
    [Arguments(2, true)]
    [Arguments(3, false)]
    [Arguments(3, true)]
    public async Task RejectedUnsubscribePreservesHandlerAndCanBeRetried(int protocol, bool specifyHandler)
    {
        var hook = new RejectNthUnsubscribe(1);
        await using var native = await RespireClient.ConnectAsync(Options(protocol, hook));
        await using var connection = RespireConnectionMultiplexer.Wrap(native);
        using var control = await ConnectionMultiplexer.ConnectAsync(fixture.IsolatedStackExchangeConnectionString);
        var subscriber = connection.GetSubscriber();
        var publisher = control.GetSubscriber();
        var channel = RedisChannel.Literal($"unsubscribe-{Guid.NewGuid():N}");
        var delivered = new TaskCompletionSource<RedisValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<RedisChannel, RedisValue> handler = (_, value) => delivered.TrySetResult(value);
        await subscriber.SubscribeAsync(channel, handler);

        var target = specifyHandler ? handler : null;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => subscriber.UnsubscribeAsync(channel, target));
        Assert.Same(hook.Rejection, error);
        Assert.Equal(1, await publisher.PublishAsync(channel, "after rejection"));
        Assert.Equal("after rejection", (string?)await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        await subscriber.UnsubscribeAsync(channel, target);
        Assert.Equal(0, await publisher.PublishAsync(channel, "after retry"));
        await connection.CloseAsync();
        Assert.Equal(0, await publisher.PublishAsync(channel, "after close"));
        Assert.True(native.IsConnected);
    }

    [Test]
    [Arguments(2, 1, false)]
    [Arguments(2, 2, false)]
    [Arguments(3, 1, false)]
    [Arguments(3, 2, false)]
    [Arguments(3, 2, true)]
    public async Task BulkUnsubscribeKeepsRemainingRegistrationsRetryable(int protocol, int rejectAttempt, bool closeToRetry)
    {
        var hook = new RejectNthUnsubscribe(rejectAttempt);
        await using var native = await RespireClient.ConnectAsync(Options(protocol, hook));
        await using var connection = RespireConnectionMultiplexer.Wrap(native);
        using var control = await ConnectionMultiplexer.ConnectAsync(fixture.IsolatedStackExchangeConnectionString);
        var subscriber = connection.GetSubscriber();
        var publisher = control.GetSubscriber();
        var channels = new Dictionary<RedisChannel, TaskCompletionSource<RedisValue>>();

        for (int i = 0; i < 3; i++)
        {
            var channel = RedisChannel.Literal($"unsubscribe-all-{Guid.NewGuid():N}");
            var delivered = new TaskCompletionSource<RedisValue>(TaskCreationOptions.RunContinuationsAsynchronously);
            await subscriber.SubscribeAsync(channel, (_, value) => delivered.TrySetResult(value));
            channels.Add(channel, delivered);
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => subscriber.UnsubscribeAllAsync());
        Assert.Same(hook.Rejection, error);
        int active = 0;

        foreach (var (channel, delivered) in channels)
        {
            if (await publisher.PublishAsync(channel, "after rejection") == 1)
            {
                active++;
                Assert.Equal("after rejection", (string?)await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            }
        }

        Assert.Equal(channels.Count - rejectAttempt + 1, active);

        if (closeToRetry)
        {
            await connection.CloseAsync();
            Assert.True(native.IsConnected);
        }
        else
        {
            await subscriber.UnsubscribeAllAsync();
        }

        foreach (var channel in channels.Keys)
        {
            Assert.Equal(0, await publisher.PublishAsync(channel, "after retry"));
        }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task RejectedCloseDetachesCallbacksAndCanBeRetried(int protocol)
    {
        var hook = new RejectNthUnsubscribe(1);
        await using var native = await RespireClient.ConnectAsync(Options(protocol, hook));
        using var control = await ConnectionMultiplexer.ConnectAsync(fixture.IsolatedStackExchangeConnectionString);
        var connection = RespireConnectionMultiplexer.Wrap(native);
        var subscriber = connection.GetSubscriber();
        var publisher = control.GetSubscriber();
        var channel = RedisChannel.Literal($"close-{Guid.NewGuid():N}");
        var beforeClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var afterClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await subscriber.SubscribeAsync(channel, (_, value) =>
        {
            if (value == "before close")
            {
                beforeClose.TrySetResult();
            }
            else
            {
                afterClose.TrySetResult();
            }
        });

        Assert.Equal(1, await publisher.PublishAsync(channel, "before close"));
        await beforeClose.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var firstClose = connection.CloseAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => firstClose);
        Assert.Same(hook.Rejection, error);
        await native.PingAsync();

        Assert.False(connection.IsConnected);
        Assert.Throws<ObjectDisposedException>(() => connection.GetSubscriber());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => subscriber.SubscribeAsync(channel, (_, _) =>
        {
        }));
        Assert.Equal(1, await publisher.PublishAsync(channel, "after close"));
        await Assert.ThrowsAsync<TimeoutException>(() => afterClose.Task.WaitAsync(TimeSpan.FromSeconds(1)));

        var retry = connection.CloseAsync();
        Assert.NotSame(firstClose, retry);
        Assert.Same(retry, connection.CloseAsync());
        await retry;
        Assert.Same(retry, connection.CloseAsync());
        Assert.Equal(0, await publisher.PublishAsync(channel, "after retry"));
        await native.PingAsync();
    }

    [Test]
    public async Task CloseRetriesRemainingSubscriptionsAfterPartialTeardown()
    {
        var hook = new RejectNthUnsubscribe(2);
        await using var native = await RespireClient.ConnectAsync(Options(3, hook));
        using var control = await ConnectionMultiplexer.ConnectAsync(fixture.IsolatedStackExchangeConnectionString);
        var connection = RespireConnectionMultiplexer.Wrap(native);
        var subscriber = connection.GetSubscriber();
        var publisher = control.GetSubscriber();
        RedisChannel[] channels = [.. Enumerable.Range(0, 3)
            .Select(_ => RedisChannel.Literal($"close-partial-{Guid.NewGuid():N}"))];

        foreach (var channel in channels)
        {
            await subscriber.SubscribeAsync(channel, static (_, _) =>
            {
            });
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => connection.CloseAsync());
        Assert.Same(hook.Rejection, error);
        long active = 0;

        foreach (var channel in channels)
        {
            active += await publisher.PublishAsync(channel, "after rejection");
        }

        Assert.Equal(2, active);
        await connection.DisposeAsync();

        foreach (var channel in channels)
        {
            Assert.Equal(0, await publisher.PublishAsync(channel, "after retry"));
        }

        await native.PingAsync();
    }

    private RespireOptions Options(int protocol, RespireCommandHook hook)
    {
        return RespireOptions.Parse(fixture.IsolatedConnectionString) with
        {
            Connections = 1,
            Protocol = protocol == 2 ? RespireProtocol.Resp2 : RespireProtocol.Resp3,
            CommandHook = hook,
        };
    }

    private sealed class RejectNthUnsubscribe(int rejectAttempt) : RespireCommandHook
    {
        private int _attempts;

        internal InvalidOperationException Rejection
        {
            get;
        } = new("Unsubscribe rejected by policy.");

        public override object? OnExecuting(RespireCommandContext context)
        {
            if (context.Command == "UNSUBSCRIBE" && Interlocked.Increment(ref _attempts) == rejectAttempt)
            {
                throw Rejection;
            }

            return null;
        }

        public override void OnCompleted(RespireCommandContext context, object? state, RespireCommandOutcome outcome, Exception? error)
        {
        }
    }
}
