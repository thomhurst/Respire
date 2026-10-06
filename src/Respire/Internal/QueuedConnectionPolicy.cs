using Respire.Networking;

namespace Respire.Internal;

/// <summary>
/// Describes connection-local queue state without owning its lease. Recreate the policy from
/// the queue's existing owners; do not add per-command state or replay an accepted command.
/// </summary>
internal readonly struct QueuedConnectionPolicy(RespireHashImportSession? importSession, RespireConnection? watchConnection = null)
{
    public RespireConnection? ImportConnection => importSession?.Connection;

    public RespireConnection? PinnedConnection => ImportConnection ?? watchConnection;

    public bool IsImportSession => importSession is not null;

    public QueuedRedirectBehavior RedirectBehavior
    {
        get
        {
            if (watchConnection is not null) return QueuedRedirectBehavior.RequireFreshWatch;
            return IsImportSession ? QueuedRedirectBehavior.Reject : QueuedRedirectBehavior.Recover;
        }
    }

    // Ordinary queues can recover only transport/server rejections proved safe by their
    // existing routing helpers. WATCH and HIMPORT state cannot move to another connection.
    public bool CanReplayRejectedCommands => importSession is null && watchConnection is null;

    public RespireHashImportSession.Usage? EnterOperation() => importSession?.EnterOperation();

    public void ValidateQueuedCommand(string operation) => importSession?.ValidateQueuedCommand(operation);

    public void ValidateDurabilityExecution()
    {
        if (IsImportSession)
            throw new NotSupportedException("Hash import sessions require their original connection and do not support durability batch execution.");
    }

    public bool CanRetryRetirement(ClusterRouter cluster, int attempt, CancellationToken cancellationToken)
        => CanReplayRejectedCommands && cluster.CanRetryRetirement(attempt, cancellationToken);

    public bool CanRecoverRejectedCommand(RespireServerException error, int? slot)
        => CanReplayRejectedCommands && ClusterRouter.CanRecover(error, slot);

    public bool RequiresExpiration(Exception error)
        => IsImportSession && RequiresSessionExpiration(error);

    internal static bool RequiresSessionExpiration(Exception error)
        => error is not RespireCommandNotSubmittedException
            && error is not RespireTimeoutException { IsCommandNotSubmitted: true }
            && (error is not RespireServerException server || ClusterRouter.IsRedirect(server)
                || server.Code == RespireErrorCodes.ReadOnly);

    public ValueTask ExpireAsync(Exception error, bool transactionStateUncertain = false)
        => importSession is not null && (transactionStateUncertain || RequiresExpiration(error))
            ? importSession.ExpireAsync(error) : ValueTask.CompletedTask;
}

internal enum QueuedRedirectBehavior
{
    Recover,
    RequireFreshWatch,
    Reject,
}
