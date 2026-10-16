namespace Respire.Internal;

internal sealed partial class DedicatedConnectionPool
{
    internal int CaptureSize()
    {
        lock (_gate) return _creating + _connections.Count;
    }

    // Broadcast state changes, rather than reserving a permit for a particular lease kind.
    // Cancellation belongs to each wait, so one canceled caller cannot consume a wakeup.
    private void SignalAvailabilityLocked()
    {
        var availability = _availability;
        _availability = null;
        availability?.TrySetResult();
    }

    private void PauseIdleTimerIfEmptyLocked()
    {
        if (_idle.Count == 0) _idleTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private void RetireIdle()
    {
        List<Entry>? closing = null;
        lock (_gate)
        {
            if (_stopping || _idle.Count == 0 || options.DedicatedPool.IdleTimeout is not { } timeout) return;
            // Pending handshakes and disconnected entries cannot protect the retention floor.
            var live = 0;
            foreach (var entry in _connections.Values)
            {
                entry.CountedForIdleRetention = entry.State != State.Closing && entry.Connection.IsConnected;
                if (entry.CountedForIdleRetention) live++;
            }
            var now = options.DedicatedPool.TimeProvider.GetTimestamp();
            for (var index = 0; index < _idle.Count;)
            {
                var entry = _idle[index];
                var connected = entry.Connection.IsConnected;
                // A disconnect can race the count above. Remove its original contribution
                // even if the entry has not expired, before considering another retirement.
                if (!connected && entry.CountedForIdleRetention)
                {
                    entry.CountedForIdleRetention = false;
                    live--;
                }
                if ((connected && live <= options.DedicatedPool.MinimumSize)
                    || options.DedicatedPool.TimeProvider.GetElapsedTime(entry.IdleSince, now) < timeout)
                {
                    index++;
                    continue;
                }
                _idle.RemoveAt(index);
                entry.Connection.RequestMetricCloseReason(ConnectionTelemetry.CloseReason.IdleEviction);
                BeginCloseLocked(entry);
                (closing ??= []).Add(entry);
                if (entry.CountedForIdleRetention) live--;
            }
            PauseIdleTimerIfEmptyLocked();
        }
        if (closing is not null)
            foreach (var entry in closing) _ = CloseAsync(entry);
    }
}
