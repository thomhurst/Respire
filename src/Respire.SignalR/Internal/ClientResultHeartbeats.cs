using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Logging;

namespace Respire.SignalR.Internal;

// An optional extension: only callers requesting heartbeats receive them. Older
// peers keep their existing cancellation/deadline behavior until a heartbeat arrives.
internal sealed class ClientResultHeartbeats(RespirePubSub bus, ClientResultsManager results, ILogger logger)
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Forwarded> _forwarded = new(StringComparer.Ordinal);

    internal static string Channel(string returnChannel) => returnChannel + ":respire:heartbeat";

    internal IDisposable Track(string invocationId, string connectionId)
    {
        _pending[invocationId] = new(connectionId);
        return new Registration(() => _pending.TryRemove(invocationId, out _));
    }

    internal IDisposable Forward(string invocationId, string returnChannel)
    {
        _forwarded[invocationId] = new(Channel(returnChannel), RedisProtocol.WriteHeartbeat(invocationId));
        return new Registration(() => _forwarded.TryRemove(invocationId, out _));
    }

    internal void Receive(string invocationId)
    {
        if (_pending.TryGetValue(invocationId, out var pending)) pending.Receive();
    }

    internal Task SendAsync(string invocationId, CancellationToken token)
        => _forwarded.TryGetValue(invocationId, out var forwarded)
            ? SendAsync(forwarded, token) : Task.CompletedTask;

    private async Task SendAsync(Forwarded forwarded, CancellationToken token)
    {
        try { await bus.PublishAsync(forwarded.Channel, forwarded.Payload, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { RedisLog.InternalMessageFailed(logger, error); }
    }

    internal async Task RunAsync()
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(bus.Stopping).ConfigureAwait(false))
            {
                foreach (var (id, pending) in _pending)
                {
                    if (pending.HasExpired())
                        await results.TryCompleteResult(pending.ConnectionId,
                            CompletionMessage.WithError(id, "The connection's server is unavailable (client-result heartbeat lost)."))
                            .ConfigureAwait(false);
                }

                if (_forwarded.IsEmpty) continue;
                // Bound publication so a broken route cannot stall checks for other servers.
                using var sending = CancellationTokenSource.CreateLinkedTokenSource(bus.Stopping);
                sending.CancelAfter(Interval);
                await Task.WhenAll(_forwarded.Values.Select(value => SendAsync(value, sending.Token))).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (bus.Stopping.IsCancellationRequested) { }
    }

    private sealed record Forwarded(string Channel, byte[] Payload);

    private sealed class Pending(string connectionId)
    {
        private readonly Lock _gate = new();
        private long? _lastSeen;
        private bool _expired;
        internal string ConnectionId => connectionId;

        internal void Receive()
        {
            lock (_gate)
            {
                if (!_expired) _lastSeen = Stopwatch.GetTimestamp();
            }
        }

        internal bool HasExpired()
        {
            lock (_gate)
            {
                if (_expired || _lastSeen is not { } lastSeen || Stopwatch.GetElapsedTime(lastSeen) < Timeout) return false;
                return _expired = true;
            }
        }
    }

    private sealed class Registration(Action remove) : IDisposable
    {
        public void Dispose() => remove();
    }
}
