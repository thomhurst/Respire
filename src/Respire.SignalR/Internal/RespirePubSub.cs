using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace Respire.SignalR.Internal;

internal sealed class RespirePubSub : IAsyncDisposable
{
    private static readonly Meter Meter = new("Respire.SignalR");
    private static readonly Counter<long> Gaps = Meter.CreateCounter<long>("respire.signalr.delivery.gaps");
    private static readonly Counter<long> Dropped = Meter.CreateCounter<long>("respire.signalr.messages.dropped");
    private readonly RespireClient _client;
    private readonly RespireSignalROptions _options;
    private readonly ILogger _logger;
    private readonly string _hub;
    private readonly ConcurrentDictionary<string, ChannelSubscription> _subscriptions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _stopping;
    private int _disposed;

    internal RespirePubSub(RespireClient client, RespireSignalROptions options, ILogger logger, string hub)
    {
        _client = client;
        _options = options;
        _logger = logger;
        _hub = hub;
        _stopping = _lifetime.Token;
        ReceiverCountIsGlobal = options.UseShardedPubSub || client.GetClusterRetirementSnapshot() is null;
    }

    internal CancellationToken Stopping => _stopping;

    // Ordinary Cluster PUBLISH reports only subscribers on the receiving node.
    internal bool ReceiverCountIsGlobal { get; }

    private RespireChannel Channel(string name)
    {
        RespireChannel channel = _options.ChannelPrefix + name;
        return _options.UseShardedPubSub ? RespireChannel.Sharded(channel) : channel;
    }

    internal ValueTask<long> PublishAsync(string channel, byte[] payload, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _client.PublishAsync(Channel(channel), payload, token);
    }

    internal async Task<ChannelSubscription> SubscribeAsync(string channel)
    {
        await _gate.WaitAsync(Stopping).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_subscriptions.TryGetValue(channel, out var existing)) return existing;
            var subscription = await _client.SubscribeAsync(Channel(channel),
                new(_options.SubscriptionBufferSize, SubscriptionOverflow.DropOldest), Stopping).ConfigureAwait(false);
            var receiver = new ChannelSubscription(subscription, channel, this);
            _subscriptions[channel] = receiver;
            return receiver;
        }
        finally { _gate.Release(); }
    }

    internal async Task UnsubscribeAsync(string channel)
    {
        ChannelSubscription? receiver;
        await _gate.WaitAsync(Stopping).ConfigureAwait(false);
        try { _subscriptions.TryRemove(channel, out receiver); }
        finally { _gate.Release(); }
        if (receiver is not null) await receiver.DisposeAsync().ConfigureAwait(false);
    }

    internal async Task ResetAsync()
    {
        foreach (var channel in _subscriptions.Keys)
            await UnsubscribeAsync(channel).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        ChannelSubscription[] receivers;
        try { receivers = _subscriptions.Values.ToArray(); _subscriptions.Clear(); }
        finally { _gate.Release(); }
        await Task.WhenAll(receivers.Select(static async receiver => await receiver.DisposeAsync().ConfigureAwait(false)))
            .ConfigureAwait(false);
        // The shared client belongs to DI or its caller, not to this hub manager.
        _lifetime.Dispose();
    }

    private void ReportGap(string channel, RespireSubscriptionGap gap)
    {
        RedisLog.DeliveryGap(_logger, _options.ChannelPrefix + channel, gap.Reason, gap.DroppedMessages);
        try
        {
            var tags = new System.Diagnostics.TagList { { "signalr.hub", _hub }, { "reason", gap.Reason.ToString() } };
            Gaps.Add(1, tags);
            if (gap.DroppedMessages != 0) Dropped.Add(gap.DroppedMessages, tags);
        }
        catch { /* Diagnostics cannot stop subscription delivery. */ }
    }

    internal readonly record struct ChannelMessage(ReadOnlyMemory<byte> Message, CancellationToken Stopping);

    internal sealed class ChannelSubscription : IAsyncDisposable
    {
        private readonly RespireSubscription _subscription;
        private readonly string _channel;
        private readonly RespirePubSub _owner;
        private readonly CancellationTokenSource _stopping;
        private Task? _consumer;
        private int _disposed;

        internal ChannelSubscription(RespireSubscription subscription, string channel, RespirePubSub owner)
        {
            _subscription = subscription; _channel = channel; _owner = owner;
            _stopping = CancellationTokenSource.CreateLinkedTokenSource(owner.Stopping);
        }

        internal void OnMessage(Action<ChannelMessage> callback)
            => OnMessage(message => { callback(message); return Task.CompletedTask; });

        internal void OnMessage(Func<ChannelMessage, Task> callback)
        {
            if (_consumer is not null) throw new InvalidOperationException("A channel already has a consumer.");
            _consumer = ConsumeAsync(callback);
        }

        private async Task ConsumeAsync(Func<ChannelMessage, Task> callback)
        {
            try
            {
                await foreach (var message in _subscription.WithCancellation(_stopping.Token).ConfigureAwait(false))
                {
                    try
                    {
                        if (message.Kind == RespireMessageKind.Gap) _owner.ReportGap(_channel, message.Gap!);
                        else await callback(new(message.Payload, _stopping.Token)).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { break; }
                    catch (Exception error) { RedisLog.InternalMessageFailed(_owner._logger, error); }
                }
                if (!_stopping.IsCancellationRequested)
                    RedisLog.SubscriptionEnded(_owner._logger, _channel, await _subscription.Completion.ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
            catch (Exception error) { RedisLog.InternalMessageFailed(_owner._logger, error); }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await _stopping.CancelAsync().ConfigureAwait(false);
            await _subscription.DisposeAsync().ConfigureAwait(false);
            if (_consumer is not null) await _consumer.ConfigureAwait(false);
            _stopping.Dispose();
        }
    }

}
