using System.Diagnostics;
using Respire.Networking;

namespace Respire.Internal;

/// <summary>Owns cancellation only while an absolute-deadline command waits for admission.</summary>
/// <remarks>
/// This mutable value has a single owner. Pass it by reference to helpers; do not copy or box
/// an initialized scope. Call Dispose directly on that owner to release its current timer.
/// Disposal preserves the caller token and absolute deadline: a later Token access can create
/// another cancellation source using only the remaining admission budget.
/// </remarks>
internal struct CommandAcquisitionScope(
    CancellationToken callerToken, CommandDeadline deadline, TimeSpan? timeout) : IDisposable
{
    private CancellationTokenSource? _source;

    internal readonly CancellationToken CallerToken => callerToken;
    internal readonly bool HasCancellation => _source is not null;

    internal CancellationToken Token
    {
        get
        {
            if (!deadline.IsSet) return callerToken;
            _source ??= CommandTimeoutCancellation.Create(callerToken,
                TimeSpan.FromMilliseconds(Math.Max(1L, deadline.RemainingMilliseconds)));
            return _source.Token;
        }
    }

    internal readonly bool IsDeadlineCancellation(OperationCanceledException error)
        => _source is not null && RespireConnection.IsDeadlineCancellation(error, _source.Token, callerToken);

    internal readonly bool IsCallerCancellation(OperationCanceledException error)
        => _source is not null && CommandTimeoutCancellation.IsFromLinkedToken(error, callerToken, _source.Token);

    internal readonly void CheckDeadline(string operation, ClientCore core, RespireConnection? pinnedConnection = null)
    {
        callerToken.ThrowIfCancellationRequested();
        if (deadline.IsSet && deadline.RemainingMilliseconds == 0)
            throw CreateTimeout(operation, core, pinnedConnection);
    }

    internal readonly RespireTimeoutException CreateTimeout(string operation, ClientCore core,
        RespireConnection? pinnedConnection = null, Exception? cause = null)
    {
        Debug.Assert(deadline.IsSet && timeout.HasValue);
        var diagnostics = core.Cluster is null && core.Sentinel is null
            ? core.Multiplexer.CaptureConnectionWait()
            : RespireTimeoutDiagnostics.Capture(RespireCommandStage.Connecting);
        if (pinnedConnection is not null)
            diagnostics = pinnedConnection.CaptureTimeoutDiagnostics(stage: RespireCommandStage.WaitingForCapacity);
        return new(operation, timeout!.Value, cause, diagnostics);
    }

    // A successful wait releases its timer before transport admission. A later routing wait
    // can arm another timer against the same absolute deadline.
    public void Dispose()
    {
        _source?.Dispose();
        _source = null;
    }
}
