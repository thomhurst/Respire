using Respire.Commands;
using Respire.Infrastructure;
using Respire.Internal;
using Respire.Protocol;

namespace Respire.Networking;

internal sealed partial class RespireConnection
{
    private static readonly RawCommand EnableMaintenance = new(
        "*3\r\n$6\r\nCLIENT\r\n$19\r\nMAINT_NOTIFICATIONS\r\n$2\r\nON\r\n"u8.ToArray());
    private static readonly RawCommand MaintenanceDrainBarrier = new("*1\r\n$4\r\nPING\r\n"u8.ToArray());
    private const string MaintenanceDrainCommandName = "RESP3 maintenance drain PING";

    internal bool HasOtherIncompleteCommandThanMaintenanceBarrier
        => _inflight.HasOtherIncompleteCommand(MaintenanceDrainCommandName);

    internal async Task WaitForOtherCommandsToCompleteAsync(CancellationToken cancellationToken)
    {
        while (HasOtherIncompleteCommandThanMaintenanceBarrier)
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
    }

    private readonly RespireConnectionOptions? _maintenanceOptions;
    // Serializes maintenance-window publication with streamed-upload deadline cancellation.
    private readonly object _maintenancePublicationGate = new();
    // Created lazily and only by the receive loop; other threads read the state volatilely.
    private MaintenanceTimeoutState? _maintenanceState;
    private MaintenanceTelemetry? _maintenanceTelemetry;
    private const int MaintenanceInactive = 0;
    // Negotiation sent; pushes parsed before the acknowledgement may be replayed completions.
    private const int MaintenanceNegotiating = 1;
    private const int MaintenanceEnabled = 2;
    private int _maintenanceStatus;
    // One immutable reference, so a reader never pairs a notification with another's origin.
    private Respire.Infrastructure.MovingAnnouncement? _lastMovingAnnouncement;
    // Raised on the receive loop after every applied notification; handlers must not block.
    private event Action? MaintenanceStateChanged;
    internal bool HasMaintenanceWindow => Volatile.Read(ref _maintenanceState)?.Remaining(Environment.TickCount64) > 0;
    internal Respire.Infrastructure.MovingAnnouncement? LastMovingAnnouncement => Volatile.Read(ref _lastMovingAnnouncement);

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
    /// Sends a protocol barrier before graceful retirement. Redis emits pushes and command replies
    /// in wire order, so the PING reply proves that pushes already sent on this connection have
    /// passed through the receive loop before retirement closes the socket.
    /// </summary>
    internal async Task DrainPendingMaintenanceNotificationsAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _maintenanceStatus) != MaintenanceEnabled) return;
        using var reply = await SendAsync(MaintenanceDrainBarrier, cancellationToken,
            armCommandDeadline: false, commandName: MaintenanceDrainCommandName).ConfigureAwait(false);
    }

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
        if (status == MaintenanceInactive) return false;
        // Capture the handler set and slot-mutation fence atomically before parsing, which can
        // scan up to 16384 triplets. A concurrent retirement cannot split these observations.
        MaintenanceNotificationHandler? migrationHandlers = null;
        long slotMutationToken = 0;
        if (MaintenanceNotification.IsSlotMigrationPush(in value))
        {
            var multiplexer = Multiplexer;
            using var capture = ClusterSlotMutationClock.BeginCapture(multiplexer);
            slotMutationToken = capture.Token;
            migrationHandlers = multiplexer?.CaptureMaintenanceHandlers(slotMutationToken);
        }
        if (MaintenanceNotification.Parse(in value) is not { } notification) return false;
        // Servers can replay historical completion notifications during opt-in. They must not
        // become a new maintenance window or a current diagnostic event.
        if (status == MaintenanceNegotiating && notification.IsCompletion) return true;
        // Dispatch before the window and diagnostics work below, so the migration reaches the
        // topology queue as early as possible.
        if (notification.IsSlotMigration)
            Multiplexer?.PublishMaintenanceNotification(migrationHandlers, this, notification, slotMutationToken);
        lock (_maintenancePublicationGate)
        {
            var state = Volatile.Read(ref _maintenanceState);
            if (state is null)
            {
                state = new MaintenanceTimeoutState((long)_maintenanceOptions!.MaintenanceWindowTimeout.TotalMilliseconds);
                Volatile.Write(ref _maintenanceState, state);
            }
            state.Apply(notification, Environment.TickCount64);
        }
        if (notification.Kind == "MOVING")
        {
            // Eligibility and receipt time are captured here, when the push is parsed, because the
            // multiplexer may handle it later (after a replay or behind its handoff gate).
            var announcement = Multiplexer?.CaptureMovingAnnouncement(MultiplexerSlot, this, notification)
                ?? new Respire.Infrastructure.MovingAnnouncement(notification, -1, -1, Environment.TickCount64);
            Volatile.Write(ref _lastMovingAnnouncement, announcement);
            MovingNotification?.Invoke(announcement);
        }
        _capacitySignal.Signal(); // Wake parked producers to recompute their effective deadline.
        MaintenanceStateChanged?.Invoke(); // Streamed SET timers recompute theirs too.
        if (RespireTelemetry.Source.HasListeners() || RespireTelemetry.MaintenanceNotifications.Enabled || _logger is not null)
        {
            (_maintenanceTelemetry ??= new MaintenanceTelemetry(Host, Port, _maintenanceOptions!.Database, _logger))
                .Publish(notification);
        }
        return true;
    }

    internal event Action<Respire.Infrastructure.MovingAnnouncement>? MovingNotification;

    // The relaxed timeout for work that is not tied to one command deadline (the deadline sweep
    // applies MaintenanceTimeoutState.Relaxes per entry, using the returned window start).
    private TimeSpan MaintenanceTimeout(TimeSpan normal, long now, out long remainingWindow, out long started)
    {
        var window = Volatile.Read(ref _maintenanceState)?.GetWindow(now);
        started = window?.Started ?? long.MaxValue;
        remainingWindow = window is not null ? window.Expires - now : 0;
        return remainingWindow > 0 && _maintenanceOptions!.MaintenanceRelaxedTimeout > normal
            ? _maintenanceOptions.MaintenanceRelaxedTimeout : normal;
    }

    /// <summary>
    /// The deadline a send rejected by this retired socket carries to its replacement. When this
    /// socket's maintenance window was relaxing it, the relaxed allowance is added once and
    /// marked, so the replacement socket's own window cannot add it again.
    /// </summary>
    internal CommandDeadline GetReroutedCommandDeadline(CommandDeadline deadline)
    {
        if (!deadline.IsSet || deadline.IsRelaxed
            || _maintenanceOptions is null || _commandTimeout is not { } normal) return deadline;
        var window = Volatile.Read(ref _maintenanceState)?.GetWindow(Environment.TickCount64);
        return window is not null && deadline.Ticks > window.Started
            && _maintenanceOptions.MaintenanceRelaxedTimeout > normal
            ? deadline.Relax((long)(_maintenanceOptions.MaintenanceRelaxedTimeout - normal).TotalMilliseconds)
            : deadline;
    }

    // Shared by the full-ring capacity wait and the streamed SET timer; see
    // MaintenanceTimeoutState.RemainingUntilDeadline for the rule the deadline sweep also applies.
    private long RemainingUntilCommandDeadline(long deadline, long now, out TimeSpan effectiveTimeout,
        out long remainingWindow, bool alreadyRelaxed = false)
    {
        var normal = _commandTimeout!.Value;
        return MaintenanceTimeoutState.RemainingUntilDeadline(Volatile.Read(ref _maintenanceState), normal,
            _maintenanceOptions?.MaintenanceRelaxedTimeout ?? normal, deadline, now, out effectiveTimeout,
            out remainingWindow, alreadyRelaxed);
    }

    private async Task WaitForMaintenanceCapacityAsync(Task capacityAvailable, CommandDeadline deadline,
        string? commandName, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = Environment.TickCount64;
            var remaining = RemainingUntilCommandDeadline(deadline.Ticks, now, out var timeout, out var window, deadline.IsRelaxed);
            if (remaining <= 0)
                throw new RespireTimeoutException(commandName ?? "(command)", timeout, null,
                    CaptureTimeoutDiagnostics(stage: RespireCommandStage.WaitingForCapacity));
            // Window expiration may restore a shorter deadline while this producer is parked.
            if (window > 0) remaining = Math.Min(remaining, window);
            try
            {
                await capacityAvailable.WaitAsync(TimeSpan.FromMilliseconds(remaining), cancellationToken).ConfigureAwait(false);
                var resumedAt = Environment.TickCount64;
                var resumedRemaining = RemainingUntilCommandDeadline(deadline.Ticks, resumedAt, out var resumedTimeout, out _, deadline.IsRelaxed);
                if (resumedRemaining <= 0)
                    throw new RespireTimeoutException(commandName ?? "(command)", resumedTimeout, null,
                        CaptureTimeoutDiagnostics(stage: RespireCommandStage.WaitingForCapacity));
                return;
            }
            catch (TimeoutException) { /* Recheck maintenance state before declaring expiry. */ }
        }
    }

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left >= right ? left : right;
}
