using System.Threading.Channels;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using ZiggyCreatures.Caching.Fusion.Backplane;

namespace Respire.FusionCache.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class BackplaneLifetimeTests(RedisTestContainer fixture)
{
    [Test]
    public async Task ConcurrentTeardownWaitsForAnAlreadyRunningCallback()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = false;
        await using var backplane = new RespireFusionCacheBackplane(client);
        await backplane.SubscribeAsync(Options(async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            finished = true;
        }));
        await backplane.PublishAsync(Message(), new());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var unsubscribe = backplane.UnsubscribeAsync().AsTask();
        Task dispose = Task.CompletedTask;
        try
        {
            await Assert.That(async () => await backplane.SubscribeAsync(Options(_ => default)))
                .ThrowsExactly<InvalidOperationException>();
            dispose = backplane.DisposeAsync().AsTask();
            await Assert.That(unsubscribe.IsCompleted).IsFalse();
            await Assert.That(dispose.IsCompleted).IsFalse();
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(unsubscribe, dispose).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(finished).IsTrue();
        await client.SetAsync(Guid.NewGuid().ToString(), "shared-client-survives");
    }

    [Test]
    public async Task CallbackCanUnsubscribeItselfWithoutDeadlock()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var backplane = new RespireFusionCacheBackplane(client);
        await backplane.SubscribeAsync(Options(async _ =>
        {
            await backplane.UnsubscribeAsync();
            completed.TrySetResult();
        }));
        await backplane.PublishAsync(Message(), new());
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await backplane.DisposeAsync();
    }

    [Test]
    public async Task ClientDisposalEndsConsumerAndLaterBackplaneDisposalRemainsSafe()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        await using var backplane = new RespireFusionCacheBackplane(client);
        await backplane.SubscribeAsync(Options(_ => default));
        await client.DisposeAsync();
        await backplane.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await backplane.DisposeAsync();
    }

    [Test]
    public async Task ConnectionCallbackFailureDoesNotPreventIncomingMessages()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var received = Channel.CreateUnbounded<string>();
        await using var backplane = new RespireFusionCacheBackplane(client);
        var channel = "fusion:" + Guid.NewGuid();
        await backplane.SubscribeAsync(new("test", "instance", channel,
            _ => throw new InvalidOperationException("controlled connection failure"),
            message => received.Writer.TryWrite(message.CacheKey!), null, null));
        await backplane.PublishAsync(Message(), new());
        await Assert.That(await received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo("key");
    }

    [Test]
    public async Task ValidationAndPreCanceledPublishDoNotRequireConnection()
    {
        await using var client = RespireClient.Create(new RespireOptions { Endpoints = [new("127.0.0.1", 1)] });
        await using var backplane = new RespireFusionCacheBackplane(client);
        await Assert.That(() => new RespireFusionCacheBackplane(null!)).ThrowsExactly<ArgumentNullException>();
        await Assert.That(async () => await backplane.SubscribeAsync(null!)).ThrowsExactly<ArgumentNullException>();
        await Assert.That(async () => await backplane.SubscribeAsync(new("test", "instance", null, _ => { }, _ => { }, null, null)))
            .ThrowsExactly<ArgumentNullException>();
        await Assert.That(async () => await backplane.SubscribeAsync(new("test", "instance", "channel", null, null, null, null)))
            .ThrowsExactly<ArgumentException>();
        await Assert.That(async () => await backplane.PublishAsync(new(), new())).ThrowsExactly<ArgumentException>();
        await Assert.That(async () => await backplane.PublishAsync(Message(), new())).ThrowsExactly<InvalidOperationException>();
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.That(async () => await backplane.PublishAsync(Message(), new(), canceled.Token)).Throws<OperationCanceledException>();
    }

    private static BackplaneSubscriptionOptions Options(Func<BackplaneMessage, ValueTask> receive)
        => new("test", "instance", "fusion:" + Guid.NewGuid(), _ => { }, _ => { }, _ => default, receive);
    private static BackplaneMessage Message() => BackplaneMessage.CreateForEntrySet("source", "key", DateTime.UtcNow.Ticks);
}
