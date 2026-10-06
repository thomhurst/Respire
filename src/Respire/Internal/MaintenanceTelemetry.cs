using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Respire.Networking;

namespace Respire.Internal;

/// <summary>Bounded, serial diagnostic delivery; user listeners never run on the receive loop.</summary>
internal sealed class MaintenanceTelemetry(string host, int port, int database, ILogger? logger)
{
    private const int Capacity = 256;
    private readonly Lock _gate = new();
    private readonly Queue<(DiagnosticNotification Notification, DateTimeOffset Received)> _pending = [];
    private bool _dispatching;
    private long _dropped;

    private readonly record struct DiagnosticNotification(string Kind, long SequenceId, long? Seconds,
        RespireEndpoint? Target);

    internal void Publish(MaintenanceNotification notification)
    {
        lock (_gate)
        {
            if (_pending.Count == Capacity)
            {
                _pending.Dequeue();
                _dropped++;
            }
            _pending.Enqueue((new(notification.Kind, notification.SequenceId, notification.Seconds, notification.Target),
                DateTimeOffset.UtcNow));
            if (_dispatching) return;
            _dispatching = true;
        }
        ThreadPool.UnsafeQueueUserWorkItem(static state => state.Drain(), this, preferLocal: false);
    }

    private void Drain()
    {
        while (true)
        {
            (DiagnosticNotification Notification, DateTimeOffset Received) item;
            long dropped;
            lock (_gate)
            {
                if (!_pending.TryDequeue(out item))
                {
                    _dispatching = false;
                    return;
                }
                // A drop only happens when Publish enqueues behind it, so every drop precedes
                // a queued item and is read here before the drain can go idle. A failed dropped
                // counter emission stays pending until a later item is delivered.
                dropped = _dropped;
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
            }
            catch (Exception error)
            {
                LogFailure(error);
            }

            try
            {
                RespireTelemetry.RecordMaintenanceNotification(host, port, notification.Kind);
            }
            catch (Exception error)
            {
                LogFailure(error);
            }

            if (dropped != 0)
            {
                try
                {
                    RespireTelemetry.MaintenanceNotificationsDropped.Add(dropped,
                        new KeyValuePair<string, object?>("server.address", host),
                        new KeyValuePair<string, object?>("server.port", port));
                    lock (_gate) _dropped -= dropped;
                }
                catch (Exception error)
                {
                    LogFailure(error);
                }
            }

            try
            {
                logger?.LogInformation("Redis maintenance {Kind} ({SequenceId}) on {Host}:{Port}",
                    notification.Kind, notification.SequenceId, host, port);
            }
            catch (Exception error)
            {
                LogFailure(error);
            }
        }
    }

    private void LogFailure(Exception error)
    {
        try { logger?.LogWarning(error, "Maintenance diagnostic listener threw"); }
        catch { /* A failing logger is also an isolated diagnostic listener. */ }
    }
}
