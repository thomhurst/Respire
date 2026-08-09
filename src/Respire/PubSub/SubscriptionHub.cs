using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;

namespace Respire.Internal;

/// <summary>
/// Owns a client's single dedicated pub/sub connection (created on first subscription) and
/// routes incoming messages to subscription buffers. If the connection dies, reconnects with
/// backoff and resubscribes everything that is still subscribed — enumerators never notice
/// beyond the gap in messages.
/// </summary>
internal sealed class SubscriptionHub(ClientCore core) : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Utf8RouteDictionary<List<RespireSubscription>>[] _routes =
        [new(), new(), new()];
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private RespireConnection? _connection;
    private volatile bool _disposed;

    public RespireSubscription CreateSubscription(SubscriptionKind kind, string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (kind == SubscriptionKind.Sharded && core.Cluster is not null)
        {
            throw new NotSupportedException(
                "Sharded pub/sub across Redis Cluster nodes is not supported yet. " +
                "Regular SUBSCRIBE and PSUBSCRIBE remain cluster-wide.");
        }

        if (names.Length == 0)
        {
            throw new ArgumentException("At least one channel is required.", nameof(names));
        }

        var buffer = Channel.CreateBounded<RespireMessage>(new BoundedChannelOptions(core.Options.SubscriptionBufferSize)
        {
            FullMode = core.Options.SubscriptionOverflow == SubscriptionOverflow.DropOldest
                ? BoundedChannelFullMode.DropOldest
                : BoundedChannelFullMode.DropWrite,
            SingleWriter = true,
            SingleReader = false,
        });

        // Defensive copy (unsubscription must look up the names that were registered, not
        // whatever the caller later wrote into their array), deduplicated so one published
        // message is never delivered twice through duplicate route entries.
        return new RespireSubscription(this, kind, [.. names.Distinct(StringComparer.Ordinal)], buffer);
    }

    /// <summary>Registers the subscription's routes and sends SUBSCRIBE; idempotent per subscription.</summary>
    public async ValueTask ActivateAsync(RespireSubscription subscription, CancellationToken cancellationToken)
    {
        if (!subscription.TryMarkActivated())
        {
            return;
        }

        var routed = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var connection = await EnsureConnectionAsync(cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                // Re-checked under the routing gate: disposal snapshots and completes routed
                // subscriptions under this same lock, so an activation that loses the race must
                // not register routes (and subscribe server-side) on a disposed hub.
                ObjectDisposedException.ThrowIf(_disposed, this);
                var routes = Routes(subscription.Kind);
                routed = true;
                foreach (var name in subscription.Names)
                {
                    if (!routes.TryGetValue(name, out var list))
                    {
                        list = [];
                        routes.Add(name, list);
                    }

                    list.Add(subscription);
                }
            }

            foreach (var name in subscription.Names)
            {
                await SendControlAsync(
                        connection, SubscribeVerb(subscription.Kind), SubscribeOperation(subscription.Kind), name,
                        cancellationToken, instrument: true)
                    .ConfigureAwait(false);
            }

            // A DisposeAsync racing this activation may have run before the routes existed —
            // its removal saw nothing to undo. Re-check and take the registration back out
            // (including the server-side subscription) so a disposed subscription cannot
            // linger routed with a completed buffer.
            if (subscription.IsDisposed)
            {
                await ReleaseRoutesAsync(subscription).ConfigureAwait(false);
            }
        }
        catch
        {
            // Roll back completely so a later enumeration attempt subscribes from scratch.
            if (routed)
            {
                lock (_gate)
                {
                    var routes = Routes(subscription.Kind);
                    foreach (var name in subscription.Names)
                    {
                        if (routes.TryGetValue(name, out var list))
                        {
                            list.Remove(subscription);
                            if (list.Count == 0)
                            {
                                routes.Remove(name);
                            }
                        }
                    }
                }
            }

            subscription.ResetActivation();
            throw;
        }
    }

    /// <summary>Unregisters the subscription, unsubscribing channels it was the last consumer of.</summary>
    public async ValueTask RemoveAsync(RespireSubscription subscription)
    {
        subscription.Buffer.Writer.TryComplete();
        await ReleaseRoutesAsync(subscription).ConfigureAwait(false);
    }

    /// <summary>Removes the subscription's routes and unsubscribes channels left without consumers.</summary>
    private async ValueTask ReleaseRoutesAsync(RespireSubscription subscription)
    {
        var releasedRoutes = new List<(SubscriptionKind Kind, string Name)>();
        lock (_gate)
        {
            var routes = Routes(subscription.Kind);
            foreach (var name in subscription.Names)
            {
                if (routes.TryGetValue(name, out var list))
                {
                    list.Remove(subscription);
                    if (list.Count == 0)
                    {
                        routes.Remove(name);
                    }
                }

                // Released = no consumer remains, regardless of who removed the route. A
                // disposal racing activation can strip the route before activation's SUBSCRIBE
                // even goes out; deriving the unsubscribe list from "route absent" (rather
                // than "this call removed the last entry") makes the rollback cover that
                // ordering too. A redundant UNSUBSCRIBE is harmless; a missing one leaks a
                // server-side subscription.
                if (!routes.ContainsKey(name))
                {
                    releasedRoutes.Add((subscription.Kind, name));
                }
            }
        }

        var connection = _connection;
        if (_disposed || connection is not { IsConnected: true })
        {
            return;
        }

        foreach (var (kind, name) in releasedRoutes)
        {
            try
            {
                await SendControlAsync(
                        connection, UnsubscribeVerb(kind), UnsubscribeOperation(kind), name,
                        CancellationToken.None, instrument: true)
                    .ConfigureAwait(false);
            }
            catch (RespireException)
            {
                // The connection died mid-unsubscribe; the server forgets the subscription anyway.
                break;
            }
        }
    }

    private static Verb SubscribeVerb(SubscriptionKind kind) => kind switch
    {
        SubscriptionKind.Pattern => Verbs.PSubscribe,
        SubscriptionKind.Sharded => Verbs.SSubscribe,
        _ => Verbs.Subscribe,
    };

    private static Verb UnsubscribeVerb(SubscriptionKind kind) => kind switch
    {
        SubscriptionKind.Pattern => Verbs.PUnsubscribe,
        SubscriptionKind.Sharded => Verbs.SUnsubscribe,
        _ => Verbs.Unsubscribe,
    };

    private static string SubscribeOperation(SubscriptionKind kind) => kind switch
    {
        SubscriptionKind.Pattern => "PSUBSCRIBE",
        SubscriptionKind.Sharded => "SSUBSCRIBE",
        _ => "SUBSCRIBE",
    };

    private static string UnsubscribeOperation(SubscriptionKind kind) => kind switch
    {
        SubscriptionKind.Pattern => "PUNSUBSCRIBE",
        SubscriptionKind.Sharded => "SUNSUBSCRIBE",
        _ => "UNSUBSCRIBE",
    };

    private async ValueTask SendControlAsync(
        RespireConnection connection,
        Verb verb,
        string operation,
        string name,
        CancellationToken cancellationToken,
        bool instrument)
    {
        var telemetry = instrument
            ? RespireTelemetry.StartOperation(
                operation, connection.Host, connection.Port, core.Options.Database)
            : default;
        try
        {
            var reply = await connection.SendAsync(new Cmd1(verb, name), cancellationToken).ConfigureAwait(false);
            if (reply.IsError)
            {
                var error = ResponseReader.ServerError(in reply);
                reply.Dispose();
                throw error;
            }

            reply.Dispose();
            telemetry.Complete(core, operation, connection: connection);
        }
        catch (Exception ex)
        {
            telemetry.Complete(core, operation, error: ex, connection: connection);
            throw;
        }
    }

    private async ValueTask<RespireConnection> EnsureConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsConnected: true } existing)
        {
            return existing;
        }

        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connection is { IsConnected: true } raced)
            {
                return raced;
            }

            var previous = _connection;
            RespireEndpoint endpoint;
            if (core.Cluster is { } cluster)
            {
                await cluster.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                endpoint = cluster.SeedEndpoint;
            }
            else
            {
                endpoint = core.Options.PrimaryEndpoint;
            }

            var options = core.Options.ToConnectionOptions(OnPush);
            var connection = await RespireConnection.ConnectAsync(
                endpoint.Host, endpoint.Port, options, core.Logger, cancellationToken).ConfigureAwait(false);
            _connection = connection;
            _ = WatchConnectionAsync(connection);
            if (previous is not null)
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }

            return connection;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    /// <summary>Reconnect-and-resubscribe loop, armed once per connection.</summary>
    private async Task WatchConnectionAsync(RespireConnection connection)
    {
        await connection.Closed.ConfigureAwait(false);
        if (_disposed)
        {
            return;
        }

        var delay = TimeSpan.FromMilliseconds(250);
        while (!_disposed)
        {
            try
            {
                var replacement = await EnsureConnectionAsync(CancellationToken.None).ConfigureAwait(false);

                (SubscriptionKind Kind, string Name)[] routes;
                lock (_gate)
                {
                    var snapshot = new List<(SubscriptionKind Kind, string Name)>();
                    for (var i = 0; i < _routes.Length; i++)
                    {
                        foreach (var name in _routes[i].Names)
                        {
                            snapshot.Add(((SubscriptionKind)i, name));
                        }
                    }

                    routes = [.. snapshot];
                }

                foreach (var (kind, name) in routes)
                {
                    await SendControlAsync(
                            replacement, SubscribeVerb(kind), SubscribeOperation(kind), name,
                            CancellationToken.None, instrument: false)
                        .ConfigureAwait(false);
                }

                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                core.Logger?.LogWarning(ex, "Pub/sub reconnect failed; retrying in {Delay}", delay);
                await Task.Delay(delay).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 5000));
            }
        }
    }

    /// <summary>Runs on the connection's receive loop — copy out of the frame, never block.</summary>
    private void OnPush(in RespValue value)
    {
        var elements = value.AsArray();
        if (elements.Length < 3)
        {
            return;
        }

        var frameKind = elements[0].AsSpan();
        if (frameKind.SequenceEqual("message"u8))
        {
            Deliver(SubscriptionKind.Channel, elements[1].AsSpan(), elements[1].AsSpan(),
                isPattern: false, elements[2].AsSpan());
        }
        else if (frameKind.SequenceEqual("smessage"u8))
        {
            Deliver(SubscriptionKind.Sharded, elements[1].AsSpan(), elements[1].AsSpan(),
                isPattern: false, elements[2].AsSpan());
        }
        else if (frameKind.SequenceEqual("pmessage"u8) && elements.Length >= 4)
        {
            Deliver(SubscriptionKind.Pattern, elements[1].AsSpan(), elements[2].AsSpan(),
                isPattern: true, elements[3].AsSpan());
        }
    }

    private void Deliver(
        SubscriptionKind kind,
        ReadOnlySpan<byte> routeName,
        ReadOnlySpan<byte> channel,
        bool isPattern,
        ReadOnlySpan<byte> payload)
    {
        RespireSubscription[] targets;
        string cachedRouteName;
        lock (_gate)
        {
            if (!Routes(kind).TryGetValue(routeName, out cachedRouteName, out var list))
            {
                return;
            }

            targets = [.. list];
        }

        var channelName = isPattern
            ? Encoding.UTF8.GetString(channel)
            : cachedRouteName;
        var message = new RespireMessage(
            channelName,
            isPattern ? cachedRouteName : null,
            payload.ToArray(),
            core.Options.Serializer);
        foreach (var target in targets)
        {
            // Bounded with DropOldest/DropWrite — TryWrite applies the overflow policy.
            target.Buffer.Writer.TryWrite(message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        List<RespireSubscription> subscriptions = [];
        lock (_gate)
        {
            foreach (var routes in _routes)
            {
                foreach (var list in routes.Values)
                {
                    subscriptions.AddRange(list);
                }

                routes.Clear();
            }
        }

        foreach (var subscription in subscriptions)
        {
            subscription.Buffer.Writer.TryComplete();
        }

        // Synchronize with a racing first connect: once the gate is ours, any connection an
        // in-flight EnsureConnectionAsync published is visible here and gets swept instead of
        // leaking an open socket past client disposal.
        await _connectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_connection is { } connection)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _connectionGate.Release();
            _connectionGate.Dispose();
        }
    }

    private Utf8RouteDictionary<List<RespireSubscription>> Routes(SubscriptionKind kind)
        => _routes[(int)kind];
}
