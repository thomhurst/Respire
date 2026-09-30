using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire.Networking;

internal sealed partial class RespireConnection
{
    private static readonly RawCommand EnableMaintenance = new(
        "*3\r\n$6\r\nCLIENT\r\n$19\r\nMAINT_NOTIFICATIONS\r\n$2\r\nON\r\n"u8.ToArray());
    private readonly RespireConnectionOptions? _maintenanceOptions;
    private MaintenanceTimeoutState? _maintenanceState;
    private MaintenanceTelemetry? _maintenanceTelemetry;
    // 0 = inactive, 1 = negotiating (completion replay suppressed), 2 = enabled.
    private int _maintenanceStatus;
    internal bool HasMaintenanceWindow => Volatile.Read(ref _maintenanceState)?.Remaining(Environment.TickCount64) > 0;

    private async ValueTask NegotiateMaintenanceAsync(RespireConnectionOptions options, RespProtocol protocol,
        CancellationToken cancellationToken, bool armCommandDeadline)
    {
        if (_maintenanceOptions is null) return;
        if (protocol != RespProtocol.Resp3)
        {
            if (options.MaintenanceNotifications == RespireMaintenanceNotificationMode.Enabled)
                throw new RespireConnectionException("Maintenance notifications require RESP3.");
            return;
        }
        Volatile.Write(ref _maintenanceStatus, 1);
        using var reply = await SendAsync(EnableMaintenance, cancellationToken, armCommandDeadline,
            "CLIENT MAINT_NOTIFICATIONS").ConfigureAwait(false);
        if (reply.IsError)
        {
            Volatile.Write(ref _maintenanceStatus, 0);
            Volatile.Write(ref _maintenanceState, null);
            var error = reply.GetErrorMessage();
            if (options.MaintenanceNotifications == RespireMaintenanceNotificationMode.Auto
                && error.StartsWith("ERR ", StringComparison.Ordinal)
                && (error.Contains("unknown subcommand", StringComparison.OrdinalIgnoreCase)
                    || error.Contains("unknown command", StringComparison.OrdinalIgnoreCase))) return;
            throw CreateHandshakeException(in reply, "CLIENT MAINT_NOTIFICATIONS");
        }
        if (reply.Type != RespDataType.SimpleString || !reply.AsSpan().SequenceEqual("OK"u8))
            throw new RespireConnectionException("CLIENT MAINT_NOTIFICATIONS returned an invalid acknowledgement.");
        Volatile.Write(ref _maintenanceStatus, 2);
    }

    private bool TryHandleMaintenancePush(in RespValue value)
    {
        var status = Volatile.Read(ref _maintenanceStatus);
        if (status == 0 || MaintenanceNotification.Parse(in value) is not { } notification) return false;
        // Servers can replay historical completion notifications during opt-in. They must not
        // become a new maintenance window or a current diagnostic event.
        if (status == 1 && notification.IsCompletion) return true;
        var state = Volatile.Read(ref _maintenanceState);
        if (state is null)
        {
            state = new MaintenanceTimeoutState((long)_maintenanceOptions!.MaintenanceWindowTimeout.TotalMilliseconds);
            Volatile.Write(ref _maintenanceState, state);
        }
        state.Apply(notification, Environment.TickCount64);
        _capacitySignal.Signal(); // Wake parked producers to recompute their effective deadline.
        if (RespireTelemetry.Source.HasListeners() || RespireTelemetry.MaintenanceNotifications.Enabled || _logger is not null)
        {
            (_maintenanceTelemetry ??= new MaintenanceTelemetry(Host, Port, _maintenanceOptions!.Database, _logger))
                .Publish(notification);
        }
        return true;
    }

    private TimeSpan MaintenanceTimeout(TimeSpan normal, long now, out long remainingWindow, out long started,
        long deadline = long.MaxValue)
    {
        var window = Volatile.Read(ref _maintenanceState)?.GetWindow(now);
        started = window?.Started ?? long.MaxValue;
        remainingWindow = window is not null && deadline > window.Started ? window.Expires - now : 0;
        return remainingWindow > 0 && _maintenanceOptions!.MaintenanceRelaxedTimeout > normal
            ? _maintenanceOptions.MaintenanceRelaxedTimeout : normal;
    }

    private async Task WaitForMaintenanceCapacityAsync(Task capacityAvailable, long deadline,
        string? commandName, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = Environment.TickCount64;
            var timeout = MaintenanceTimeout(_commandTimeout!.Value, now, out var window, out _, deadline);
            var remaining = deadline + (long)(timeout - _commandTimeout.Value).TotalMilliseconds - now;
            if (remaining <= 0)
                throw new RespireTimeoutException(commandName ?? "(command)", timeout, null,
                    CaptureTimeoutDiagnostics(stage: RespireCommandStage.WaitingForCapacity));
            // Window expiration may restore a shorter deadline while this producer is parked.
            if (window > 0) remaining = Math.Min(remaining, window);
            try
            {
                await capacityAvailable.WaitAsync(TimeSpan.FromMilliseconds(remaining), cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (TimeoutException) { /* Recheck maintenance state before declaring expiry. */ }
        }
    }
}
