using Microsoft.Extensions.Logging;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Backplane;

namespace Respire.FusionCache;

/// <summary>A FusionCache backplane using the caller-owned Respire client's regular pub/sub channels.</summary>
/// <remarks>Use one instance per FusionCache instance. Ordered delivery gaps notify FusionCache of reconnection.
/// Redis pub/sub cannot replay lost messages. Disposal never disposes the shared client.</remarks>
public sealed partial class RespireFusionCacheBackplane : IFusionCacheBackplane, IAsyncDisposable, IDisposable
{
    private static readonly AsyncLocal<SubscriptionState?> CurrentCallback = new();
    private readonly IRespireClient _client;
    private readonly RespireSubscriptionOptions _subscriptionOptions;
    private readonly ILogger<RespireFusionCacheBackplane>? _logger;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private SubscriptionState? _retiring;
    private SubscriptionState? _state;
    private bool _disposed;

    /// <summary>Creates a backplane without connecting or taking ownership of <paramref name="client"/>.</summary>
    public RespireFusionCacheBackplane(IRespireClient client, RespireSubscriptionOptions subscriptionOptions = default,
        ILogger<RespireFusionCacheBackplane>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _subscriptionOptions = subscriptionOptions;
        _logger = logger;
    }

    /// <inheritdoc/>
    public void Subscribe(BackplaneSubscriptionOptions options)
        => SubscribeCoreAsync(options, synchronousCallbacks: true).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc/>
    public ValueTask SubscribeAsync(BackplaneSubscriptionOptions options)
        => SubscribeCoreAsync(options, synchronousCallbacks: false);

    private async ValueTask SubscribeCoreAsync(BackplaneSubscriptionOptions options, bool synchronousCallbacks)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(options.ChannelName);
        if (options.ConnectHandler is null && options.ConnectHandlerAsync is null)
            throw new ArgumentException("A connection handler is required.", nameof(options));
        if (options.IncomingMessageHandler is null && options.IncomingMessageHandlerAsync is null)
            throw new ArgumentException("An incoming message handler is required.", nameof(options));

        SubscriptionState state;
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_retiring?.Cleanup.IsCompletedSuccessfully == true) _retiring = null;
            if (_state is not null || _retiring is not null)
                throw new InvalidOperationException("Complete unsubscribe before reusing a backplane instance.");
            var subscription = await _client.SubscribeAsync(options.ChannelName, _subscriptionOptions, default).ConfigureAwait(false);
            state = new SubscriptionState(subscription, options, synchronousCallbacks);
            Volatile.Write(ref _state, state);
            state.Consumer = Task.Run(() => ConsumeAsync(state));
        }
        finally { _lifecycle.Release(); }
        await state.Started.Task.ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void Publish(BackplaneMessage message, FusionCacheEntryOptions options, CancellationToken token = default)
        => PublishAsync(message, options, token).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc/>
    public async ValueTask PublishAsync(BackplaneMessage message, FusionCacheEntryOptions options, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(options);
        token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        if (!message.IsValid()) throw new ArgumentException("The backplane message is invalid.", nameof(message));
        var state = Volatile.Read(ref _state) ?? throw new InvalidOperationException("Subscribe before publishing.");
        if (state.Subscription.IsDisposed) throw new InvalidOperationException("The backplane subscription has ended.");
        await _client.PublishAsync(state.Options.ChannelName!, BackplaneMessage.ToByteArray(message), token).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void Unsubscribe() => UnsubscribeAsync().AsTask().GetAwaiter().GetResult();

    /// <inheritdoc/>
    public ValueTask UnsubscribeAsync() => StopAsync(disposing: false);

    /// <inheritdoc/>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => StopAsync(disposing: true);

    private async ValueTask StopAsync(bool disposing)
    {
        SubscriptionState? retiring;
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposing) Volatile.Write(ref _disposed, true);
            if (_retiring?.Cleanup.IsCompletedSuccessfully == true) _retiring = null;
            var state = Interlocked.Exchange(ref _state, null);
            if (state is not null)
            {
                state.BeginStop();
                _retiring = state;
            }
            retiring = _retiring;
        }
        finally { _lifecycle.Release(); }
        // Do not hold the lifecycle gate while an application callback completes or re-enters us.
        // Re-entrant callbacks await transport shutdown only; other callers also join that callback.
        if (retiring is not null)
            await (CurrentCallback.Value == retiring ? retiring.TransportStop : retiring.Cleanup).ConfigureAwait(false);
    }

    private async Task ConsumeAsync(SubscriptionState state)
    {
        CurrentCallback.Value = state;
        try
        {
            if (!state.Stop.IsCancellationRequested)
                await NotifyConnectionAsync(state, reconnect: false).ConfigureAwait(false);
            state.Started.TrySetResult();
            await foreach (var item in state.Subscription.WithCancellation(state.Stop.Token).ConfigureAwait(false))
            {
                if (state.Stop.IsCancellationRequested) break;
                if (item.Kind == RespireMessageKind.Gap)
                {
                    if (_logger is { } gapLogger) LogGap(gapLogger, item.Gap!.Reason);
                    await NotifyConnectionAsync(state, reconnect: true).ConfigureAwait(false);
                    continue;
                }
                try
                {
                    var message = BackplaneMessage.FromByteArray(item.Payload.ToArray());
                    if (!message.IsValid()) throw new FormatException("Invalid FusionCache backplane message.");
                    if (state.SynchronousCallbacks && state.Options.IncomingMessageHandler is { } synchronous)
                        synchronous(message);
                    else if (state.Options.IncomingMessageHandlerAsync is { } asynchronous)
                        await asynchronous(message).ConfigureAwait(false);
                    else state.Options.IncomingMessageHandler!(message);
                }
                catch (Exception error)
                {
                    if (_logger is { } callbackLogger) LogMessageFailure(callbackLogger, error);
                }
            }
            if (!state.Stop.IsCancellationRequested && _logger is { } endLogger)
                LogSubscriptionEnded(endLogger, await state.Subscription.Completion.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (state.Stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (state.Stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (_logger is { } consumerLogger) LogConsumerFailure(consumerLogger, error);
        }
        finally
        {
            state.Started.TrySetResult();
            CurrentCallback.Value = null;
        }
    }

    private async ValueTask NotifyConnectionAsync(SubscriptionState state, bool reconnect)
    {
        try
        {
            var info = new BackplaneConnectionInfo(reconnect);
            if (state.SynchronousCallbacks && state.Options.ConnectHandler is { } synchronous) synchronous(info);
            else if (state.Options.ConnectHandlerAsync is { } asynchronous) await asynchronous(info).ConfigureAwait(false);
            else state.Options.ConnectHandler!(info);
        }
        catch (Exception error)
        {
            if (_logger is { } connectionLogger) LogConnectionFailure(connectionLogger, error);
        }
    }

    [LoggerMessage(1, LogLevel.Warning, "FusionCache backplane delivery gap: {Reason}.")]
    private static partial void LogGap(ILogger logger, RespireSubscriptionGapReason reason);

    [LoggerMessage(2, LogLevel.Warning, "FusionCache backplane message or callback failed; continuing delivery.")]
    private static partial void LogMessageFailure(ILogger logger, Exception error);

    [LoggerMessage(3, LogLevel.Warning, "FusionCache backplane subscription ended: {Reason}.")]
    private static partial void LogSubscriptionEnded(ILogger logger, RespireSubscriptionEndReason reason);

    [LoggerMessage(4, LogLevel.Error, "FusionCache backplane consumer stopped.")]
    private static partial void LogConsumerFailure(ILogger logger, Exception error);

    [LoggerMessage(5, LogLevel.Warning, "FusionCache backplane connection callback failed; continuing delivery.")]
    private static partial void LogConnectionFailure(ILogger logger, Exception error);

    private sealed class SubscriptionState(RespireSubscription subscription, BackplaneSubscriptionOptions options, bool synchronousCallbacks)
    {
        internal RespireSubscription Subscription { get; } = subscription;
        internal BackplaneSubscriptionOptions Options { get; } = options;
        internal bool SynchronousCallbacks { get; } = synchronousCallbacks;
        internal CancellationTokenSource Stop { get; } = new();
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Consumer { get; set; } = Task.CompletedTask;
        internal Task TransportStop { get; private set; } = Task.CompletedTask;
        internal Task Cleanup { get; private set; } = Task.CompletedTask;

        internal void BeginStop()
        {
            TransportStop = StopTransportAsync();
            Cleanup = FinishStopAsync();
        }

        private async Task StopTransportAsync()
        {
            await Stop.CancelAsync().ConfigureAwait(false);
            await Subscription.DisposeAsync().ConfigureAwait(false);
        }

        private async Task FinishStopAsync()
        {
            try { await TransportStop.ConfigureAwait(false); }
            finally
            {
                try { await Consumer.ConfigureAwait(false); }
                finally { Stop.Dispose(); }
            }
        }
    }
}
