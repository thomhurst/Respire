---
title: Maintenance notifications
description: Negotiate Redis maintenance pushes and relax command timeouts during maintenance.
---

# Maintenance notifications

Redis Cloud and Redis Software can send RESP3 push notifications before maintenance. Respire can
surface these notifications and extend command and receive timeouts for the affected command
connection. Redis Open Source does not send these notifications. See Redis's
[Smart client handoffs documentation](https://redis.io/docs/latest/develop/clients/sch/) for
server support and deployment requirements.

## Configure negotiation

Maintenance notifications are disabled by default. `Auto` requests support and keeps the
connection when the server rejects the capability. `Enabled` requires RESP3 and fails connection
setup if the server rejects the request.

```csharp
await using var redis = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = ["redis.example:6379"],
    Protocol = RespProtocol.Resp3,
    MaintenanceNotifications = RespireMaintenanceNotificationMode.Auto,
    MaintenanceTimeoutExtension = TimeSpan.FromSeconds(20),
});
```

The client sends `CLIENT MAINT_NOTIFICATIONS ON` during setup on command connections only. It does
not send this command on blocking, pub/sub, or Sentinel discovery connections. `Auto` skips the
request when protocol negotiation falls back to RESP2.

## Observe notifications

Subscribe to `MaintenanceNotificationReceived` to observe validated `MOVING`, `MIGRATING`,
`MIGRATED`, `FAILING_OVER`, `FAILED_OVER`, `SMIGRATING`, and `SMIGRATED` pushes. Respire copies
their sequence ID and remaining fields into `RespireMaintenanceNotification`. Handlers run on a
thread-pool thread, outside the receive loop.

```csharp
redis.MaintenanceNotificationReceived += notification =>
{
    Console.WriteLine($"{notification.Type} sequence {notification.SequenceId}");
};
```

Matching start and end notifications apply per-connection timeout relaxation. A start extends
in-flight command deadlines and permits new commands to wait up to `MaintenanceTimeoutExtension`
longer. The receive watchdog uses the same bounded extension while a notification window is
active. An end notification stops extending new commands; it does not shorten deadlines already
extended. Unmatched, duplicate, stale, and replayed notifications do not change timeout state.

`MOVING` is reported but does not redirect the endpoint or hand off connections. `SMIGRATED` is
reported but does not update Cluster slot ownership. Those actions remain separate features.

