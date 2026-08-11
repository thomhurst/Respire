using System.Runtime.CompilerServices;
using System.Threading.Channels;
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
/// automatically — the stream just keeps going (messages published while disconnected are lost, as
/// with any Redis pub/sub). Only one enumerator may be active at a time; dispose it before starting
/// another.
/// </summary>
public sealed class RespireSubscription : IAsyncEnumerable<RespireMessage>, IAsyncDisposable
{
    private readonly SubscriptionHub _hub;
    private readonly TaskCompletionSource<RespireSubscriptionEndReason> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _droppedMessages;
    private int _disposed;
    private int _enumerating;

    internal RespireSubscription(
        SubscriptionHub hub,
        SubscriptionKind kind,
        string[] names,
        int bufferSize,
        SubscriptionOverflow overflow)
    {
        _hub = hub;
        Kind = kind;
        Names = names;
        Targets = Array.AsReadOnly(names);
        Buffer = Channel.CreateBounded<RespireMessage>(new BoundedChannelOptions(bufferSize)
        {
            FullMode = overflow == SubscriptionOverflow.DropOldest
                ? BoundedChannelFullMode.DropOldest
                : BoundedChannelFullMode.DropWrite,
            SingleWriter = true,
            SingleReader = true,
        }, _ =>
        {
            Interlocked.Increment(ref _droppedMessages);
            RespireTelemetry.RecordSubscriptionMessageDropped(kind, overflow);
        });
    }

    /// <summary>The Redis pub/sub command family used by this subscription.</summary>
    public SubscriptionKind Kind { get; }

    /// <summary>The channels or patterns covered by this subscription. The collection is immutable.</summary>
    public IReadOnlyList<string> Targets { get; }

    /// <summary>The channels or patterns covered by this subscription. The collection is immutable.</summary>
    [Obsolete("Use Targets; subscriptions may cover channel patterns.")]
    public IReadOnlyList<string> Channels => Targets;

    /// <summary>Whether this subscription has ended because it or its owning client was disposed.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>The number of messages discarded by this subscription's overflow policy.</summary>
    public long DroppedMessages => Interlocked.Read(ref _droppedMessages);

    /// <summary>
    /// Completes with the reason this subscription ended. Enumeration may finish before explicit
    /// unsubscription completes; await this task when the distinction matters.
    /// </summary>
    public Task<RespireSubscriptionEndReason> Completion => _completion.Task;

    internal string[] Names { get; }

    internal Channel<RespireMessage> Buffer { get; }

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
                EnumerateAsync(cancellationToken).GetAsyncEnumerator(cancellationToken));
        }
        catch
        {
            Volatile.Write(ref _enumerating, 0);
            throw;
        }
    }

    private async IAsyncEnumerable<RespireMessage> EnumerateAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await Buffer.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (Buffer.Reader.TryRead(out var message))
            {
                yield return message;
            }
        }
    }

    /// <summary>Unsubscribes (when this was the channel's last subscription) and ends enumeration.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Buffer.Writer.TryComplete();
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
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Buffer.Writer.TryComplete();
        _completion.TrySetResult(RespireSubscriptionEndReason.ClientDisposed);
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
