using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Respire.Internal;

namespace Respire;

internal enum SubscriptionKind
{
    Channel,
    Pattern,
    Sharded,
}

/// <summary>
/// An active subscription, consumed as an async stream:
/// <code>
/// await using var sub = await client.SubscribeAsync("news");
/// await foreach (var message in sub.WithCancellation(token)) { … }
/// </code>
/// <c>SubscribeAsync</c> returns once the server has acknowledged the SUBSCRIBE, so a publish
/// issued right after it reaches this subscriber; the lazy <c>Subscribe</c> overloads send it when
/// enumeration starts instead. Disposing unsubscribes. If the pub/sub connection drops, Respire
/// reconnects and resubscribes automatically — the stream just keeps going (messages published
/// while disconnected are lost, as with any Redis pub/sub).
/// </summary>
public sealed class RespireSubscription : IAsyncEnumerable<RespireMessage>, IAsyncDisposable
{
    private readonly SubscriptionHub _hub;
    private int _activated;
    private int _disposed;

    internal RespireSubscription(SubscriptionHub hub, SubscriptionKind kind, string[] names, Channel<RespireMessage> buffer)
    {
        _hub = hub;
        Kind = kind;
        Names = names;
        Buffer = buffer;
    }

    internal SubscriptionKind Kind { get; }

    internal string[] Names { get; }

    internal Channel<RespireMessage> Buffer { get; }

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal bool TryMarkActivated() => Interlocked.CompareExchange(ref _activated, 1, 0) == 0;

    /// <summary>Failed activations roll back so the next enumeration attempt subscribes again.</summary>
    internal void ResetActivation() => Volatile.Write(ref _activated, 0);

    public IAsyncEnumerator<RespireMessage> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        => EnumerateAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);

    private async IAsyncEnumerable<RespireMessage> EnumerateAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _hub.ActivateAsync(this, cancellationToken).ConfigureAwait(false);

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

        await _hub.RemoveAsync(this).ConfigureAwait(false);
    }
}
