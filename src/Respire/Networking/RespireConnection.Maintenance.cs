using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire.Networking;

internal sealed partial class RespireConnection
{
    private static readonly RawCommand EnableMaintenance = new(
        "*3\r\n$6\r\nCLIENT\r\n$19\r\nMAINT_NOTIFICATIONS\r\n$2\r\nON\r\n"u8.ToArray());
    private readonly RespireConnectionOptions? _maintenanceOptions;
    // Created lazily and only by the receive loop; other threads read the state volatilely.
    private MaintenanceTimeoutState? _maintenanceState;
    private MaintenanceTelemetry? _maintenanceTelemetry;
    private const int MaintenanceInactive = 0;
    // Negotiation sent; pushes parsed before the acknowledgement may be replayed completions.
    private const int MaintenanceNegotiating = 1;
    private const int MaintenanceEnabled = 2;
    private int _maintenanceStatus;
    private MaintenanceNotification? _lastMovingNotification;
    internal bool HasMaintenanceWindow => Volatile.Read(ref _maintenanceState)?.Remaining(Environment.TickCount64) > 0;
    internal MaintenanceNotification? LastMovingNotification => Volatile.Read(ref _lastMovingNotification);

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
        Volatile.Write(ref _maintenanceStatus, MaintenanceNegotiating);
        try
        {
            using var reply = await SendAsync(EnableMaintenance, cancellationToken, armCommandDeadline,
                "CLIENT MAINT_NOTIFICATIONS").ConfigureAwait(false);
            if (reply.IsError)
            {
                DisableMaintenance();
                var error = reply.GetErrorMessage();
                if (options.MaintenanceNotifications == RespireMaintenanceNotificationMode.Auto
                    && error.StartsWith("ERR ", StringComparison.Ordinal)
                    && (error.Contains("unknown subcommand", StringComparison.OrdinalIgnoreCase)
                        || error.Contains("unknown command", StringComparison.OrdinalIgnoreCase))) return;
                throw CreateHandshakeException(in reply, "CLIENT MAINT_NOTIFICATIONS");
            }
            if (!IsMaintenanceAcknowledgement(in reply))
                throw new RespireConnectionException("CLIENT MAINT_NOTIFICATIONS returned an invalid acknowledgement.");
            // The receive loop normally enables at the acknowledgement's wire position already.
            Volatile.Write(ref _maintenanceStatus, MaintenanceEnabled);
        }
        catch
        {
            DisableMaintenance();
            throw;
        }
    }

    private void DisableMaintenance()
    {
        Volatile.Write(ref _maintenanceStatus, MaintenanceInactive);
        Volatile.Write(ref _maintenanceState, null);
    }

    private static bool IsMaintenanceAcknowledgement(in RespValue reply)
        => reply.Type == RespDataType.SimpleString && reply.AsSpan().SequenceEqual("OK"u8);

    /// <summary>
    /// Receive loop only, for the reply that answers the negotiation command (the handshake
    /// has no other command in flight). Enabling here, rather than in the deferred awaiting
    /// continuation, lets a completion that follows the acknowledgement in the same read end
    /// the window its start opened.
    /// </summary>
    private void ObserveMaintenanceAcknowledgement(in RespValue reply)
    {
        if (IsMaintenanceAcknowledgement(in reply))
            Volatile.Write(ref _maintenanceStatus, MaintenanceEnabled);
    }

    private bool TryHandleMaintenancePush(in RespValue value)
    {
        var status = Volatile.Read(ref _maintenanceStatus);
        if (status == MaintenanceInactive || MaintenanceNotification.Parse(in value) is not { } notification) return false;
        // Servers can replay historical completion notifications during opt-in. They must not
        // become a new maintenance window or a current diagnostic event.
        if (status == MaintenanceNegotiating && notification.IsCompletion) return true;
        var state = Volatile.Read(ref _maintenanceState);
        if (state is null)
        {
            state = new MaintenanceTimeoutState((long)_maintenanceOptions!.MaintenanceWindowTimeout.TotalMilliseconds);
            Volatile.Write(ref _maintenanceState, state);
        }
        state.Apply(notification, Environment.TickCount64);
        if (notification.Kind == "MOVING")
        {
            Volatile.Write(ref _lastMovingNotification, notification);
            MovingNotification?.Invoke(notification);
        }
        _capacitySignal.Signal(); // Wake parked producers to recompute their effective deadline.
        if (RespireTelemetry.Source.HasListeners() || RespireTelemetry.MaintenanceNotifications.Enabled || _logger is not null)
        {
            (_maintenanceTelemetry ??= new MaintenanceTelemetry(Host, Port, _maintenanceOptions!.Database, _logger))
                .Publish(notification);
        }
        return true;
    }

    internal event Action<MaintenanceNotification>? MovingNotification;

    private TimeSpan MaintenanceTimeout(TimeSpan normal, long now, out long remainingWindow, out long started,
        long deadline = long.MaxValue)
    {
        var window = Volatile.Read(ref _maintenanceState)?.GetWindow(now);
        started = window?.Started ?? long.MaxValue;
        remainingWindow = window is not null && deadline > window.Started ? window.Expires - now : 0;
        return remainingWindow > 0 && _maintenanceOptions!.MaintenanceRelaxedTimeout > normal
            ? _maintenanceOptions.MaintenanceRelaxedTimeout : normal;
    }

    // Marks a deadline that a reroute already extended by the relaxed-timeout allowance, so a
    // later MOVING handoff in the same send cannot add it again. TickCount64 never reaches this bit;
    // deadline consumers strip it before tick arithmetic.
    private const long RelaxedRerouteDeadline = 1L << 62;

    internal static long PlainDeadline(long deadline) => deadline & ~RelaxedRerouteDeadline;

    private long GetReroutedCommandDeadline(long deadline)
    {
        if (deadline == 0 || (deadline & RelaxedRerouteDeadline) != 0
            || _maintenanceOptions is null || _commandTimeout is not { } normal) return deadline;
        var window = Volatile.Read(ref _maintenanceState)?.GetWindow(Environment.TickCount64);
        return window is not null && deadline > window.Started
            && _maintenanceOptions.MaintenanceRelaxedTimeout > normal
            ? (deadline + (long)(_maintenanceOptions.MaintenanceRelaxedTimeout - normal).TotalMilliseconds)
                | RelaxedRerouteDeadline
            : deadline;
    }

    private async Task WaitForMaintenanceCapacityAsync(Task capacityAvailable, long deadline,
        string? commandName, CancellationToken cancellationToken)
    {
        // An already relaxed rerouted deadline must not be relaxed again by this socket's window.
        var alreadyRelaxed = (deadline & RelaxedRerouteDeadline) != 0;
        deadline = PlainDeadline(deadline);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = Environment.TickCount64;
            var windowTimeout = MaintenanceTimeout(_commandTimeout!.Value, now, out var window, out _, deadline);
            var timeout = alreadyRelaxed ? _commandTimeout.Value : windowTimeout;
            var remaining = deadline + (long)(timeout - _commandTimeout.Value).TotalMilliseconds - now;
            if (remaining <= 0)
                throw new RespireTimeoutException(commandName ?? "(command)", timeout, null,
                    CaptureTimeoutDiagnostics(stage: RespireCommandStage.WaitingForCapacity));
            // Window expiration may restore a shorter deadline while this producer is parked.
            if (window > 0) remaining = Math.Min(remaining, window);
            try
            {
                await capacityAvailable.WaitAsync(TimeSpan.FromMilliseconds(remaining), cancellationToken).ConfigureAwait(false);
                var resumedAt = Environment.TickCount64;
                var resumedTimeout = alreadyRelaxed ? _commandTimeout!.Value
                    : MaintenanceTimeout(_commandTimeout!.Value, resumedAt, out _, out _, deadline);
                var resumedRemaining = deadline + (long)(resumedTimeout - _commandTimeout.Value).TotalMilliseconds - resumedAt;
                if (resumedRemaining <= 0)
                    throw new RespireTimeoutException(commandName ?? "(command)", resumedTimeout, null,
                        CaptureTimeoutDiagnostics(stage: RespireCommandStage.WaitingForCapacity));
                return;
            }
            catch (TimeoutException) { /* Recheck maintenance state before declaring expiry. */ }
        }
    }
}
