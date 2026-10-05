using System.Reflection;
using Respire.OutputCaching;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Caching.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class OutputCacheConcurrencyTests(RedisTestContainer fixture)
{
    [Test]
    public async Task ShorterConcurrentWriteCannotExpireTheLastPublishedValuesTag()
    {
        await using var firstClient = await RespireClient.ConnectAsync(fixture.ConnectionString);
        await using var secondClient = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var options = new RespireOutputCacheOptions
        {
            InstanceName = $"output:{Guid.NewGuid():N}:",
            TimeProvider = clock,
        };
        var delayedClient = DispatchProxy.Create<IRespireClient, PublicationBarrier>();
        var barrier = (PublicationBarrier)delayedClient;
        barrier.Client = firstClient;
        var longer = new RespireOutputCacheStore(delayedClient, options);
        var shorter = new RespireOutputCacheStore(secondClient, options);

        var longWrite = longer.SetAsync("key", new byte[] { 1 }, ["shared"], TimeSpan.FromHours(1)).AsTask();
        try
        {
            // The long writer has registered its tags, but has not published its value yet.
            await barrier.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await shorter.SetAsync("key", new byte[] { 2 }, ["shared"], TimeSpan.FromMinutes(5));
        }
        finally
        {
            barrier.Release.TrySetResult();
            await longWrite.WaitAsync(TimeSpan.FromSeconds(30));
        }

        // Cleanup sees the short deadline as expired while the last-published value is still live.
        clock.Now += TimeSpan.FromMinutes(10);
        await shorter.CollectExpiredTagsAsync();
        await Assert.That((await longer.GetAsync("key"))!.Single()).IsEqualTo((byte)1);
        await shorter.EvictByTagAsync("shared");
        await Assert.That(await longer.GetAsync("key")).IsNull();
    }

    public class PublicationBarrier : DispatchProxy
    {
        public IRespireClient Client { get; set; } = null!;
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IRespireClient.SetAsync) && !targetMethod.IsGenericMethod)
                return PublishAfterReleaseAsync(targetMethod, args);
            return targetMethod.Invoke(Client, args);
        }

        private async ValueTask<bool> PublishAfterReleaseAsync(MethodInfo method, object?[]? args)
        {
            Reached.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return await (ValueTask<bool>)method.Invoke(Client, args)!;
        }
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
