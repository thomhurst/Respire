using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;

namespace Respire.FastClient;

/// <summary>
/// Receives one pub/sub message. Runs on the connection's receive loop: the message value is
/// only valid for the duration of the callback (copy what you need), and the handler must not
/// block.
/// </summary>
public delegate void RespireMessageHandler(string channel, in RespireValue message);

/// <summary>
/// Pub/sub subscriber on its own dedicated connection — Redis restricts a subscribed RESP2
/// connection to SUBSCRIBE-family commands, so subscriptions never share a connection with
/// regular traffic. Publish from a regular <see cref="RespireClient"/>.
/// </summary>
/// <remarks>
/// Message frames (message/pmessage) are routed to registered handlers by channel or pattern;
/// subscribe/unsubscribe confirmations complete the awaiting command in FIFO order like any
/// other reply.
/// </remarks>
public sealed class RespireSubscriber : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, RespireMessageHandler> _channelHandlers = new();
    private readonly ConcurrentDictionary<string, RespireMessageHandler> _patternHandlers = new();
    private RespireConnection _connection = null!;

    public bool IsConnected => _connection.IsConnected;

    private RespireSubscriber()
    {
    }

    public static async Task<RespireSubscriber> CreateAsync(
        string host,
        int port = 6379,
        RespireConnectionOptions? options = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        // The subscriber exists before its connection so OnPush can be passed as a method
        // group; pushes cannot arrive before the first SUBSCRIBE, by which point _connection
        // is assigned (and OnPush never touches it anyway).
        var subscriber = new RespireSubscriber();
        options = (options ?? RespireConnectionOptions.Default) with { PushHandler = subscriber.OnPush };
        subscriber._connection = await RespireConnection.ConnectAsync(
            host, port, options, logger, cancellationToken).ConfigureAwait(false);
        return subscriber;
    }

    /// <summary>Registers the handler and awaits the server's subscribe confirmation.</summary>
    public ValueTask SubscribeAsync(string channel, RespireMessageHandler handler, CancellationToken cancellationToken = default)
        => SubscribeCoreAsync(_channelHandlers, CommandPrefixes.Subscribe, channel, handler, cancellationToken);

    public ValueTask UnsubscribeAsync(string channel, CancellationToken cancellationToken = default)
        => UnsubscribeCoreAsync(_channelHandlers, CommandPrefixes.Unsubscribe, channel, cancellationToken);

    /// <summary>Registers a glob-pattern handler (PSUBSCRIBE) and awaits confirmation.</summary>
    public ValueTask PSubscribeAsync(string pattern, RespireMessageHandler handler, CancellationToken cancellationToken = default)
        => SubscribeCoreAsync(_patternHandlers, CommandPrefixes.PSubscribe, pattern, handler, cancellationToken);

    public ValueTask PUnsubscribeAsync(string pattern, CancellationToken cancellationToken = default)
        => UnsubscribeCoreAsync(_patternHandlers, CommandPrefixes.PUnsubscribe, pattern, cancellationToken);

    private async ValueTask SubscribeCoreAsync(
        ConcurrentDictionary<string, RespireMessageHandler> handlers, byte[] prefix, string key,
        RespireMessageHandler handler, CancellationToken cancellationToken)
    {
        handlers[key] = handler;
        var reply = await _connection.SendAsync(
            new SingleValueCommand(prefix, key), cancellationToken).ConfigureAwait(false);
        if (reply.IsError)
        {
            handlers.TryRemove(key, out _);
        }

        reply.ThrowIfError();
        reply.Dispose();
    }

    private async ValueTask UnsubscribeCoreAsync(
        ConcurrentDictionary<string, RespireMessageHandler> handlers, byte[] prefix, string key,
        CancellationToken cancellationToken)
    {
        // Capture the handler this unsubscribe is retiring. If a concurrent SubscribeAsync
        // for the same key installs a newer handler while the confirmation is in flight, the
        // server ends up subscribed (its SUBSCRIBE is queued after our UNSUBSCRIBE), so only
        // the captured handler may be removed — a blanket TryRemove would silently drop all
        // messages for the re-subscribed channel.
        handlers.TryGetValue(key, out var retiring);

        var reply = await _connection.SendAsync(
            new SingleValueCommand(prefix, key), cancellationToken).ConfigureAwait(false);

        if (retiring is not null)
        {
            handlers.TryRemove(new KeyValuePair<string, RespireMessageHandler>(key, retiring));
        }

        reply.ThrowIfError();
        reply.Dispose();
    }

    private void OnPush(in RespireValue value)
    {
        var elements = value.AsArray();
        if (elements.Length < 3)
        {
            return;
        }

        var kind = elements[0].AsSpan();
        if (kind.SequenceEqual("message"u8) || kind.SequenceEqual("smessage"u8))
        {
            var channel = elements[1].AsString();
            if (_channelHandlers.TryGetValue(channel, out var handler))
            {
                handler(channel, in elements[2]);
            }
        }
        else if (kind.SequenceEqual("pmessage"u8) && elements.Length >= 4)
        {
            var pattern = elements[1].AsString();
            if (_patternHandlers.TryGetValue(pattern, out var handler))
            {
                handler(elements[2].AsString(), in elements[3]);
            }
        }
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
