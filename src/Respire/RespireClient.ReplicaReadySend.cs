using System.Runtime.CompilerServices;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire;

public sealed partial class RespireClient
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryDispatchReplica<TCommand, TResult, TSend>(
        string operation, in TCommand command, CancellationToken cancellationToken,
        TSend sender, out ValueTask<TResult> response, RespireTelemetry.ErrorObservation observation)
        where TCommand : struct, IRespCommand
        where TSend : struct, IReadySend<TResult>
    {
        if (_readFrom == RespireReadFrom.Primary
            || TryGetDirectReplicaConnection(operation, in command, cancellationToken) is not { } connection)
        {
            response = default;
            return false;
        }
        response = SendOnReadyReplicaAsync<TCommand, TResult, TSend>(
            operation, connection, in command, cancellationToken, sender, observation);
        return true;
    }

    private RespireConnection? TryGetDirectReplicaConnection<TCommand>(
        string operation, in TCommand command, CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
    {
        // Circuit admission, cache coordination, server-local cursors, hedging and telemetry own their normal route.
        if (_core.Cluster is not null || _core.Circuits is not null || _core.ClientCache is not null || _core.HedgedReads is not null
            || command.ReadKind != ReadCommandKind.Read || command is IStreamingRespCommand
            || _readFrom is not (RespireReadFrom.Replica or RespireReadFrom.ReplicaPreferred)
            || cancellationToken.IsCancellationRequested || RespireTelemetry.IsOperationEnabled(operation)) return null;
        try { return _core.ReadRouter.TryAcquireReadyConnection(_readFrom, cancellationToken); }
        catch (Exception error) when (error is RespireConnectionException or ObjectDisposedException or OperationCanceledException)
        {
            // No command was accepted. The ordinary async route preserves acquisition failures.
            return null;
        }
    }

    private static ValueTask<TResult> SendOnReadyReplicaAsync<TCommand, TResult, TSend>(
        string operation, RespireConnection connection, in TCommand command,
        CancellationToken cancellationToken, TSend sender, RespireTelemetry.ErrorObservation observation)
        where TCommand : struct, IRespCommand
        where TSend : struct, IReadySend<TResult>
    {
        // The ready replica gate excludes telemetry; the ordinary route owns its duration.
        try { return sender.Send(connection, operation, in command, cancellationToken, default, errorObservation: observation); }
        catch (Exception error)
        {
            // Readiness is an observation, not a lease. Preserve the former async failure shape.
            return CaptureReadySendFailure<TResult>(error);
        }
    }
}
