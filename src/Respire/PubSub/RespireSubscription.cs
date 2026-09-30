using Respire.Internal;

namespace Respire;

/// <summary>The Redis pub/sub command family used by a subscription.</summary>
public enum SubscriptionKind
{
    /// <summary>A regular channel subscription created with SUBSCRIBE.</summary>
    Channel,

    /// <summary>A glob-pattern subscription created with PSUBSCRIBE.</summary>
    Pattern,

    /// <summary>A sharded channel subscription created with SSUBSCRIBE.</summary>
    Sharded,
}

/// <summary>Why a subscription stopped accepting and yielding messages.</summary>
public enum RespireSubscriptionEndReason
{
    /// <summary>The subscription was explicitly disposed.</summary>
    Disposed,

    /// <summary>The client that owns the subscription was disposed.</summary>
    ClientDisposed,

    /// <summary>The owning client's configured pub/sub reconnect attempt limit was exhausted.</summary>
    ReconnectExhausted,
}

/// <summary>
/// Per-subscription buffer settings. Null values inherit the corresponding
/// <see cref="RespireOptions"/> setting.
/// </summary>
/// <param name="BufferSize">Buffered messages before the overflow policy applies.</param>
/// <param name="Overflow">Policy used when the subscription consumer falls behind.</param>
public readonly record struct RespireSubscriptionOptions(
    int? BufferSize = null,
    SubscriptionOverflow? Overflow = null)
{
    internal (int BufferSize, SubscriptionOverflow Overflow) Resolve(RespireOptions defaults)
    {
        if (BufferSize is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(BufferSize), BufferSize, "Must be at least one.");
        }

        if (Overflow is { } overflow && !Enum.IsDefined(overflow))
        {
            throw new ArgumentOutOfRangeException(nameof(Overflow), Overflow, null);
        }

        return (BufferSize ?? defaults.SubscriptionBufferSize, Overflow ?? defaults.SubscriptionOverflow);
    }
}

/// <summary>
/// An active subscription, consumed as an async stream:
/// <code>
/// await using var sub = await client.SubscribeAsync("news");
/// await foreach (var message in sub.WithCancellation(token)) { … }
/// </code>
/// The subscription is already live when <c>SubscribeAsync</c> returns, so a publish issued right
/// after it reaches this subscriber and messages are buffered until enumeration starts. Disposing
/// unsubscribes. If the pub/sub connection drops, Respire reconnects and resubscribes
/// automatically. A configured reconnect limit ends live subscriptions when exhausted; inspect
/// <see cref="Completion"/> for <see cref="RespireSubscriptionEndReason.ReconnectExhausted"/>.
/// Delivery-gap markers report reconnects and local buffer discards; lost messages
/// cannot be replayed by Redis pub/sub. Only one enumerator may be active at a time; dispose it before starting
/// another.
/// </summary>
public sealed class RespireSubscription : IAsyncEnumerable<RespireMessage>, IAsyncDisposable
{
    private readonly SubscriptionHub _hub;
    private readonly TaskCompletionSource<RespireSubscriptionEndReason> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SubscriptionOverflow _overflow;
    private long _droppedMessages;
    private int _disposed;
    private int _enumerating;

    internal RespireSubscription(
        SubscriptionHub hub,
        SubscriptionKind kind,
        RespireChannel[] names,
        int bufferSize,
        SubscriptionOverflow overflow)
    {
        _hub = hub;
        Kind = kind;
        Names = names;
        Targets = Array.AsReadOnly(names);
        _overflow = overflow;
        Buffer = new SubscriptionBuffer(bufferSize, overflow);
    }

    /// <summary>The Redis pub/sub command family used by this subscription.</summary>
    public SubscriptionKind Kind { get; }

    /// <summary>The channels or patterns covered by this subscription. The collection is immutable.</summary>
    public IReadOnlyList<RespireChannel> Targets { get; }

    /// <summary>Whether this subscription ended through disposal or exhausted reconnect attempts.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>The number of messages discarded by this subscription's overflow policy.</summary>
    public long DroppedMessages => Interlocked.Read(ref _droppedMessages);

    /// <summary>
    /// Completes with the reason this subscription ended. Enumeration may finish before explicit
    /// unsubscription completes; await this task when the distinction matters.
    /// </summary>
    public Task<RespireSubscriptionEndReason> Completion => _completion.Task;

    internal RespireChannel[] Names { get; }

    internal SubscriptionBuffer Buffer { get; }

    /// <summary>Reports each detected gap, independently of enumeration.</summary>
    /// <remarks>Runs synchronously on the receive path. Handlers must not block or wait for Redis operations.
    /// Handler exceptions are logged and do not stop delivery. Stream markers can coalesce adjacent events.</remarks>
    public event Action<RespireSubscriptionGap>? DeliveryGap;

    internal void NotifyDrop(RespireSubscriptionGap gap)
    {
        Interlocked.Increment(ref _droppedMessages);
        RespireTelemetry.RecordSubscriptionMessageDropped(Kind, _overflow);
        NotifyGap(gap);
    }

    internal void NotifyGap(RespireSubscriptionGap gap)
    {
        RespireTelemetry.RecordSubscriptionGap(Kind, gap.Reason);
        var handlers = DeliveryGap;
        if (handlers is null) return;
        foreach (Action<RespireSubscriptionGap> handler in handlers.GetInvocationList())
        {
            try { handler(gap); }
            catch (Exception ex) { _hub.LogGapHandlerFailure(ex); }
        }
    }

    /// <inheritdoc/>
    public IAsyncEnumerator<RespireMessage> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (Interlocked.CompareExchange(ref _enumerating, 1, 0) != 0)
        {
            throw new InvalidOperationException("A RespireSubscription can only have one active enumerator.");
        }

        try
        {
            return new SubscriptionEnumerator(
                this,
                Buffer.ReadAllAsync(cancellationToken).GetAsyncEnumerator(cancellationToken));
        }
        catch
        {
            Volatile.Write(ref _enumerating, 0);
            throw;
        }
    }

    /// <summary>Unsubscribes (when this was the channel's last subscription) and ends enumeration.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Buffer.Complete();
        try
        {
            await _hub.RemoveAsync(this).ConfigureAwait(false);
            _completion.TrySetResult(RespireSubscriptionEndReason.Disposed);
        }
        catch (Exception ex)
        {
            _completion.TrySetException(ex);
            throw;
        }
    }

    internal void CompleteFromClientDisposal()
        => CompleteFromOwner(RespireSubscriptionEndReason.ClientDisposed);

    internal void CompleteFromReconnectExhaustion()
        => CompleteFromOwner(RespireSubscriptionEndReason.ReconnectExhausted);

    private void CompleteFromOwner(RespireSubscriptionEndReason reason)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Buffer.Complete();
        _completion.TrySetResult(reason);
    }

    private sealed class SubscriptionEnumerator(
        RespireSubscription subscription,
        IAsyncEnumerator<RespireMessage> inner) : IAsyncEnumerator<RespireMessage>
    {
        private RespireSubscription? _subscription = subscription;

        public RespireMessage Current => inner.Current;

        public ValueTask<bool> MoveNextAsync() => inner.MoveNextAsync();

        public async ValueTask DisposeAsync()
        {
            var owner = Interlocked.Exchange(ref _subscription, null);
            if (owner is null)
            {
                return;
            }

            try
            {
                await inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref owner._enumerating, 0);
            }
        }
    }
}
