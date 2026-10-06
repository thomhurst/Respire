using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Backplane;
using ZiggyCreatures.Caching.Fusion.Backplane.StackExchangeRedis;
using Fusion = ZiggyCreatures.Caching.Fusion.FusionCache;

namespace Respire.FusionCache.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class BackplaneTests(RedisTestContainer fixture)
{
    [Test]
    [Arguments(2, false)]
    [Arguments(2, true)]
    [Arguments(3, false)]
    [Arguments(3, true)]
    public async Task LiteralChannelsWireMessagesCancellationAndReusableLifetime(int protocol, bool synchronous)
    {
        await using var client = await ConnectAsync(protocol);
        var channel = Name() + "*";
        var listener = new Listener(channel);
        var logger = new RecordingLogger();
        await using var backplane = new RespireFusionCacheBackplane(client.WithKeyPrefix("tenant:"), logger: logger);
        await SubscribeAsync(backplane, listener.Options, synchronous);
        await Assert.That(await ReadAsync(listener.Connections)).IsFalse();
        await Assert.That(async () => await backplane.SubscribeAsync(listener.Options)).ThrowsExactly<InvalidOperationException>();
        await using var wire = await client.SubscribeAsync(channel);
        await using var reader = wire.GetAsyncEnumerator();
        var original = BackplaneMessage.CreateForEntrySet("source", "key-é", DateTime.UtcNow.Ticks);
        if (synchronous) backplane.Publish(original, new());
        else await backplane.PublishAsync(original, new());
        await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var decoded = BackplaneMessage.FromByteArray(reader.Current.Payload.ToArray());
        await Assert.That(decoded.CacheKey).IsEqualTo(original.CacheKey);
        await Assert.That(decoded.Timestamp).IsEqualTo(original.Timestamp);
        await Assert.That((await ReadAsync(listener.Messages)).Action).IsEqualTo(BackplaneMessageAction.EntrySet);

        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.That(async () => await backplane.PublishAsync(original, new(), canceled.Token)).Throws<OperationCanceledException>();
        await client.PublishAsync(channel, new byte[] { 0 });
        await client.PublishAsync(channel + "suffix", BackplaneMessage.ToByteArray(original));
        var next = BackplaneMessage.CreateForEntryRemove("source", "next", DateTime.UtcNow.Ticks);
        await client.PublishAsync(channel, BackplaneMessage.ToByteArray(next));
        await Assert.That((await ReadAsync(listener.Messages)).CacheKey).IsEqualTo("next");
        await Assert.That(listener.Messages.Reader.TryRead(out _)).IsFalse();
        await Assert.That(logger.Events.Contains(2)).IsTrue();
        await Assert.That(synchronous ? listener.SyncMessages : listener.AsyncMessages).IsEqualTo(2);
        await Assert.That(synchronous ? listener.AsyncMessages : listener.SyncMessages).IsEqualTo(0);

        if (synchronous) backplane.Unsubscribe();
        else await backplane.UnsubscribeAsync();
        await Assert.That(async () => await backplane.PublishAsync(original, new())).ThrowsExactly<InvalidOperationException>();
        await SubscribeAsync(backplane, listener.Options, synchronous);
        await Assert.That(await ReadAsync(listener.Connections)).IsFalse();
        await backplane.DisposeAsync();
        await backplane.DisposeAsync();
        await Assert.That(async () => await backplane.SubscribeAsync(listener.Options)).ThrowsExactly<ObjectDisposedException>();
        await client.SetAsync(Name(), "client-still-owned-by-caller");
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task InteroperatesWithUpstreamRedisBackplaneInBothDirections(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        using var upstreamClient = await ConnectionMultiplexer.ConnectAsync(fixture.StackExchangeConnectionString);
        var channel = Name();
        var ours = new Listener(channel);
        var theirs = new Listener(channel);
        await using var backplane = new RespireFusionCacheBackplane(client);
        var upstream = new RedisBackplane(new RedisBackplaneOptions
        {
            ConnectionMultiplexerFactory = () => Task.FromResult<IConnectionMultiplexer>(upstreamClient),
        });
        await backplane.SubscribeAsync(ours.Options);
        await upstream.SubscribeAsync(theirs.Options);
        try
        {
            await backplane.PublishAsync(BackplaneMessage.CreateForEntryExpire("ours", "one", DateTime.UtcNow.Ticks), new());
            await Assert.That((await ReadAsync(theirs.Messages)).CacheKey).IsEqualTo("one");
            await ReadAsync(ours.Messages);
            await upstream.PublishAsync(BackplaneMessage.CreateForEntryRemove("theirs", "two", DateTime.UtcNow.Ticks), new());
            await Assert.That((await ReadAsync(ours.Messages)).CacheKey).IsEqualTo("two");
        }
        finally { await upstream.UnsubscribeAsync(); }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task OwnedSubscriberReconnectProducesGapBeforeNewMessages(int protocol)
    {
        var clientName = Name();
        await using var client = await ConnectAsync(protocol, clientName);
        var listener = new Listener(Name());
        var logger = new RecordingLogger();
        await using var backplane = new RespireFusionCacheBackplane(client, logger: logger);
        await backplane.SubscribeAsync(listener.Options);
        await Assert.That(await ReadAsync(listener.Connections)).IsFalse();
        var subscriber = (await client.Server.ClientsAsync()).Single(row => row.Name == clientName && row.Flags.Contains('P'));
        await Assert.That(await client.Server.KillClientAsync(subscriber.Id)).IsTrue();
        await Assert.That(await ReadAsync(listener.Connections)).IsTrue();
        await backplane.PublishAsync(BackplaneMessage.CreateForEntrySet("source", "after", DateTime.UtcNow.Ticks), new());
        await Assert.That((await ReadAsync(listener.Messages)).CacheKey).IsEqualTo("after");
        await Assert.That(logger.Events.Contains(1)).IsTrue();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task OverflowNotifiesGapAndCallbackFailureDoesNotStopDelivery(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var channel = Name();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = Channel.CreateUnbounded<string>();
        async ValueTask Receive(BackplaneMessage message)
        {
            if (message.CacheKey == "hold")
            {
                entered.TrySetResult();
                await release.Task;
                throw new InvalidOperationException("controlled callback failure");
            }
            events.Writer.TryWrite(message.CacheKey!);
        }
        var options = new BackplaneSubscriptionOptions("test", "instance", channel, _ => { }, _ => { },
            info => { if (info.IsReconnection) events.Writer.TryWrite("gap"); return default; }, Receive);
        await using var backplane = new RespireFusionCacheBackplane(client, new(BufferSize: 1, Overflow: SubscriptionOverflow.DropOldest));
        await backplane.SubscribeAsync(options);
        try
        {
            await backplane.PublishAsync(BackplaneMessage.CreateForEntrySet("source", "hold", DateTime.UtcNow.Ticks), new());
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var index = 0; index < 8; index++)
                await backplane.PublishAsync(BackplaneMessage.CreateForEntrySet("source", $"next-{index}", DateTime.UtcNow.Ticks), new());
            // This acknowledgement shares the subscriber's ordered receive stream and follows those messages.
            await using var barrier = await client.SubscribeAsync(Name());
        }
        finally { release.TrySetResult(); }
        await Assert.That(await ReadAsync(events)).IsEqualTo("gap");
        await Assert.That(await ReadAsync(events)).IsEqualTo("next-7");
    }

    [Test]
    [Arguments(2, false)]
    [Arguments(2, true)]
    [Arguments(3, false)]
    [Arguments(3, true)]
    public async Task TwoFusionCachesInvalidateSetRemoveExpireAndIsolateOtherNames(int protocol, bool synchronous)
    {
        await using var client = await ConnectAsync(protocol);
        var cacheName = Name();
        await using var firstBackplane = new RespireFusionCacheBackplane(client);
        await using var secondBackplane = new RespireFusionCacheBackplane(client);
        await using var isolatedBackplane = new RespireFusionCacheBackplane(client);
        using var first = CreateCache(cacheName, firstBackplane);
        using var second = CreateCache(cacheName, secondBackplane);
        using var isolated = CreateCache(Name(), isolatedBackplane);
        var privateEntry = new FusionCacheEntryOptions { SkipBackplaneNotifications = true, Duration = TimeSpan.FromMinutes(1) };
        await isolated.SetAsync("key", 99, privateEntry);
        foreach (var action in new[] { "set", "remove", "expire" })
        {
            await second.SetAsync("key", 42, privateEntry);
            if (synchronous)
            {
                if (action == "set") first.Set("key", 1);
                else if (action == "remove") first.Remove("key");
                else first.Expire("key");
            }
            else
            {
                if (action == "set") await first.SetAsync("key", 1);
                else if (action == "remove") await first.RemoveAsync("key");
                else await first.ExpireAsync("key");
            }
            await WaitUntilAsync(async () => await second.GetOrDefaultAsync("key", -1) == -1);
            await Assert.That(await isolated.GetOrDefaultAsync("key", -1)).IsEqualTo(99);
        }
    }

    private static Fusion CreateCache(string name, IFusionCacheBackplane backplane)
    {
        var cache = new Fusion(Options.Create(new FusionCacheOptions { CacheName = name, WaitForInitialBackplaneSubscribe = true }));
        cache.DefaultEntryOptions.AllowBackgroundBackplaneOperations = false;
        cache.SetupBackplane(backplane);
        return cache;
    }

    private ValueTask<RespireClient> ConnectAsync(int protocol, string? name = null)
        => RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with
        {
            Protocol = protocol == 2 ? RespProtocol.Resp2 : RespProtocol.Resp3,
            ClientName = name, Connections = 1, AllowAdmin = true,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
        });

    private static string Name() => "fusion:" + Guid.NewGuid().ToString("N");
    private static Task<T> ReadAsync<T>(Channel<T> channel) => channel.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    private static async Task WaitUntilAsync(Func<ValueTask<bool>> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await predicate()) await Task.Delay(10, timeout.Token);
    }
    private static ValueTask SubscribeAsync(RespireFusionCacheBackplane backplane, BackplaneSubscriptionOptions options, bool synchronous)
    {
        if (!synchronous) return backplane.SubscribeAsync(options);
        backplane.Subscribe(options);
        return default;
    }

    private sealed class Listener
    {
        internal Channel<BackplaneMessage> Messages { get; } = Channel.CreateUnbounded<BackplaneMessage>();
        internal Channel<bool> Connections { get; } = Channel.CreateUnbounded<bool>();
        internal int SyncMessages;
        internal int AsyncMessages;
        internal BackplaneSubscriptionOptions Options { get; }
        internal Listener(string channel)
        {
            Options = new("test", "instance", channel,
                info => Connections.Writer.TryWrite(info.IsReconnection),
                message => { Interlocked.Increment(ref SyncMessages); Messages.Writer.TryWrite(message); },
                info => { Connections.Writer.TryWrite(info.IsReconnection); return default; },
                message => { Interlocked.Increment(ref AsyncMessages); Messages.Writer.TryWrite(message); return default; });
        }
    }

    private sealed class RecordingLogger : ILogger<RespireFusionCacheBackplane>
    {
        internal ConcurrentQueue<int> Events { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Events.Enqueue(eventId.Id);
    }
}
