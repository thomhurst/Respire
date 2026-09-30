using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Respire.Networking;

namespace Respire.Internal;

/// <summary>Bounded, serial diagnostic delivery; user listeners never run on the receive loop.</summary>
internal sealed class MaintenanceTelemetry(string host, int port, int database, ILogger? logger)
{
    private const int Capacity = 256;
    private readonly object _gate = new();
    private readonly Queue<(MaintenanceNotification Notification, DateTimeOffset Received)> _pending = [];
    private bool _dispatching;
    private long _dropped;

    internal void Publish(MaintenanceNotification notification)
    {
        lock (_gate)
        {
            if (_pending.Count == Capacity)
            {
                _pending.Dequeue();
                _dropped++;
            }
            _pending.Enqueue((notification, DateTimeOffset.UtcNow));
            if (_dispatching) return;
            _dispatching = true;
        }
        ThreadPool.UnsafeQueueUserWorkItem(static state => state.Drain(), this, preferLocal: false);
    }

    private void Drain()
    {
        while (true)
        {
            (MaintenanceNotification Notification, DateTimeOffset Received) item;
            long dropped;
            lock (_gate)
            {
                if (!_pending.TryDequeue(out item))
                {
                    _dispatching = false;
                    return;
                }
                dropped = _dropped;
                _dropped = 0;
            }
            var notification = item.Notification;
            try
            {
                var tags = new ActivityTagsCollection
                {
                    { "db.system.name", "redis" }, { "server.address", host }, { "server.port", port },
                    { "db.namespace", database.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    { "respire.maintenance.kind", notification.Kind },
                    { "respire.maintenance.sequence_id", notification.SequenceId },
                    { "respire.maintenance.announced_seconds", notification.Seconds },
                    { "respire.maintenance.target.address", notification.Target?.Host },
                    { "respire.maintenance.target.port", notification.Target?.Port },
                };
                using var activity = RespireTelemetry.Source.StartActivity("redis.maintenance", ActivityKind.Consumer,
                    default(ActivityContext), tags, startTime: item.Received);
                activity?.AddEvent(new ActivityEvent(notification.Kind, item.Received));
                RespireTelemetry.MaintenanceNotifications.Add(1,
                    new KeyValuePair<string, object?>("server.address", host),
                    new KeyValuePair<string, object?>("server.port", port),
                    new KeyValuePair<string, object?>("respire.maintenance.kind", notification.Kind));
                if (dropped != 0) RespireTelemetry.MaintenanceNotificationsDropped.Add(dropped);
                logger?.LogInformation("Redis maintenance {Kind} ({SequenceId}) on {Host}:{Port}",
                    notification.Kind, notification.SequenceId, host, port);
            }
            catch (Exception error)
            {
                // Diagnostic failures cannot stop protocol progress or strand the dispatch queue.
                try { logger?.LogWarning(error, "Maintenance diagnostic listener threw"); }
                catch { /* A failing logger is also an isolated diagnostic listener. */ }
            }
        }
    }
}
