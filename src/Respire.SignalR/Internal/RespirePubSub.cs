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
    // Guarded by _gate.
    private Task? _unsubscribeRetry;
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
    internal bool ReceiverCountIsGlobal
    {
        get;
    }

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

            if (_subscriptions.TryGetValue(channel, out var existing))
            {
                if (!existing.UnsubscribeRequested)
                {
                    return existing;
                }

                await UnsubscribeCoreAsync(channel, existing).ConfigureAwait(false);
            }

            var subscription = await _client.SubscribeAsync(Channel(channel),
                new(_options.SubscriptionBufferSize, RespireSubscriptionOverflow.DropOldest), Stopping).ConfigureAwait(false);
            var receiver = new ChannelSubscription(subscription, channel, this);
            _subscriptions[channel] = receiver;
            return receiver;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task UnsubscribeAsync(string channel)
    {
        await _gate.WaitAsync(Stopping).ConfigureAwait(false);
        ChannelSubscription? receiver = null;

        try
        {
            if (_subscriptions.TryGetValue(channel, out receiver))
            {
                await UnsubscribeCoreAsync(channel, receiver).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();

            if (receiver is not null)
            {
                await receiver.StopConsumerAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task UnsubscribeCoreAsync(string channel, ChannelSubscription receiver)
    {
        try
        {
            await receiver.UnsubscribeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (receiver.IsUnsubscribed)
            {
                _subscriptions.TryRemove(channel, out _);
            }
            else if (!Stopping.IsCancellationRequested)
            {
                // A rejected command hook leaves the native subscription live. Keep the
                // handle and retry even if there is no further activity on this channel.
                _unsubscribeRetry ??= RetryUnsubscribesAsync();
            }
        }
    }

    private async Task RetryUnsubscribesAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), Stopping).ConfigureAwait(false);
                await _gate.WaitAsync(Stopping).ConfigureAwait(false);

                try
                {
                    bool stillPending = false;

                    // SubscribeAsync uses the same gate to replace retiring receivers.
                    foreach (var (channel, receiver) in _subscriptions)
                    {
                        Stopping.ThrowIfCancellationRequested();

                        if (!receiver.UnsubscribeRequested)
                        {
                            continue;
                        }

                        try
                        {
                            await UnsubscribeCoreAsync(channel, receiver).ConfigureAwait(false);
                        }
                        catch (Exception error)
                        {
                            RedisLog.UnsubscribeFailed(_logger, channel, error);
                        }

                        stillPending |= !receiver.IsUnsubscribed;
                    }

                    if (!stillPending)
                    {
                        _unsubscribeRetry = null;
                        return;
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (Stopping.IsCancellationRequested)
        {
        }
    }

    internal async Task ResetAsync()
    {
        foreach (var channel in _subscriptions.Keys)
        {
            try
            {
                await UnsubscribeAsync(channel).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (Stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                // Finish resetting the other startup channels and preserve the startup error.
                RedisLog.UnsubscribeFailed(_logger, channel, error);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        ChannelSubscription[] receivers;
        Task unsubscribeRetry;

        try
        {
            unsubscribeRetry = _unsubscribeRetry ?? Task.CompletedTask;
            receivers = _subscriptions.Values.ToArray();
            _subscriptions.Clear();
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            // The shared client belongs to DI or its caller.
            await Task.WhenAll(receivers.Select(static receiver => receiver.DisposeAsync().AsTask()).Append(unsubscribeRetry))
                .ConfigureAwait(false);
        }
        finally
        {
            _lifetime.Dispose();
        }
    }

    private void ReportGap(string channel, RespireSubscriptionGap gap)
    {
        RedisLog.DeliveryGap(_logger, _options.ChannelPrefix + channel, gap.Reason, gap.DroppedMessages);

        try
        {
            var tags = new System.Diagnostics.TagList { { "signalr.hub", _hub }, { "reason", gap.Reason.ToString() } };
            Gaps.Add(1, tags);

            if (gap.DroppedMessages != 0)
            {
                Dropped.Add(gap.DroppedMessages, tags);
            }
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
            _subscription = subscription;
            _channel = channel;
            _owner = owner;
            _stopping = CancellationTokenSource.CreateLinkedTokenSource(owner.Stopping);
        }

        internal bool UnsubscribeRequested
        {
            get; private set;
        }

        internal bool IsUnsubscribed => _subscription.IsDisposed;

        internal ValueTask UnsubscribeAsync()
        {
            UnsubscribeRequested = true;
            return _subscription.DisposeAsync();
        }

        internal void OnMessage(Action<ChannelMessage> callback)
            => OnMessage(message =>
            {
                callback(message);
                return Task.CompletedTask;
            });

        internal void OnMessage(Func<ChannelMessage, Task> callback)
        {
            if (_consumer is not null)
            {
                throw new InvalidOperationException("A channel already has a consumer.");
            }

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
                        if (message.Kind == RespireMessageKind.Gap)
                        {
                            _owner.ReportGap(_channel, message.Gap!);
                        }
                        else
                        {
                            await callback(new(message.Payload, _stopping.Token)).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { break; }
                    catch (Exception error) { RedisLog.InternalMessageFailed(_owner._logger, error); }
                }

                if (!_stopping.IsCancellationRequested)
                {
                    RespireSubscriptionEndReason reason = await _subscription.Completion.ConfigureAwait(false);

                    if (!_stopping.IsCancellationRequested && reason != RespireSubscriptionEndReason.Disposed)
                    {
                        RedisLog.SubscriptionEnded(_owner._logger, _channel, reason);
                    }
                }
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
            catch (Exception error) { RedisLog.InternalMessageFailed(_owner._logger, error); }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await UnsubscribeAsync().ConfigureAwait(false);
            }
            finally
            {
                await StopConsumerAsync().ConfigureAwait(false);
            }
        }

        internal async ValueTask StopConsumerAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _stopping.CancelAsync().ConfigureAwait(false);

            if (_consumer is not null)
            {
                await _consumer.ConfigureAwait(false);
            }

            _stopping.Dispose();
        }
    }

}
