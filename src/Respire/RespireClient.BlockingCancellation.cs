using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire;

public sealed partial class RespireClient
{
    // Only this private signal authorizes pool return after an exceptional blocking send.
    // Public callers still receive OperationCanceledException with their original token.
    private sealed class UnblockedCommandCanceledException(CancellationToken token) : OperationCanceledException(token);

    private ValueTask<RespValue> SendBlockingWithCancellationAsync<TCommand>(
        DedicatedConnectionPool pool, RespireConnection connection, TCommand command,
        CancellationToken cancellationToken, int errorAttempts, RespireTelemetry.ErrorObservation observation,
        bool sendAsking = false)
        where TCommand : struct, IRespCommand
    {
        if (_core.Options.DedicatedPool.UnblockOnCancel && cancellationToken.CanBeCanceled
            && pool.MovingOwner is { } owner)
        {
            try
            {
                var control = owner.GetConnection();
                // IDs are local to a server. Never follow Sentinel failover, MOVING, DNS changes,
                // or a reconnect to another peer when sending this connection-specific command.
                if (control.NetworkPeerAddress is { } peer && peer == connection.NetworkPeerAddress
                    && control.NetworkPeerPort == connection.NetworkPeerPort)
                    return SendUnblockableAsync(connection, control, command, cancellationToken,
                        errorAttempts, observation, sendAsking);
            }
            catch (RespireConnectionException) { /* No healthy control socket: retain abandon-on-cancel. */ }
        }
        return SendBlockingFrameAsync(connection, command, cancellationToken, errorAttempts, observation, sendAsking);
    }

    private ValueTask<RespValue> SendBlockingFrameAsync<TCommand>(
        RespireConnection connection, TCommand command, CancellationToken cancellationToken,
        int errorAttempts, RespireTelemetry.ErrorObservation observation, bool sendAsking)
        where TCommand : struct, IRespCommand
        => sendAsking
            ? ClusterRouter.SendBlockingAskingUncheckedAsync(connection, in command, cancellationToken,
                errorAttempts, pinToConnection: _core.Circuits is not null, observation: observation)
            : connection.SendWithoutResponseTimeoutAsync(command, cancellationToken, errorAttempts,
                pinToConnection: _core.Circuits is not null, observation: observation);

    private async ValueTask<RespValue> SendUnblockableAsync<TCommand>(
        RespireConnection connection, RespireConnection control, TCommand command,
        CancellationToken cancellationToken, int errorAttempts, RespireTelemetry.ErrorObservation observation,
        bool sendAsking)
        where TCommand : struct, IRespCommand
    {
        long id;
        try
        {
            id = await connection.EnsureServerClientIdAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (RespireServerException)
        {
            // Old servers and restricted ACLs must retain the existing cancellation behavior.
            return await SendBlockingFrameAsync(connection, command, cancellationToken,
                errorAttempts, observation, sendAsking).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (id <= 0)
            return await SendBlockingFrameAsync(connection, command, cancellationToken,
                errorAttempts, observation, sendAsking).ConfigureAwait(false);

        // Keep the response source alive after caller cancellation so its FIFO reply can be drained.
        // A separate token also lets failed cleanup stop an acquisition before discarding the lease.
        using var sendCancellation = new CancellationTokenSource();
        var pending = SendBlockingFrameAsync(connection, command, sendCancellation.Token,
            errorAttempts, observation, sendAsking).AsTask();
        try
        {
            return await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var reusable = false;
            try
            {
                // Await acknowledgement even if the blocking reply wins the race. A late UNBLOCK
                // must never reach a subsequent renter's command on this socket.
                using var reply = await control.SendAsync(new ClientUnblockCommand(id), cleanup.Token,
                    armCommandDeadline: false, pinToConnection: true).ConfigureAwait(false);
                if (!reply.IsError && reply.Type == RespireDataType.Integer && reply.AsInteger() is 0 or 1)
                {
                    using var drained = await pending.WaitAsync(cleanup.Token).ConfigureAwait(false);
                    reusable = true;
                }
            }
            catch (Exception error) when (error is RespireException or OperationCanceledException or ObjectDisposedException)
            {
                // Cleanup failure cannot replace the caller's cancellation. The outer owner discards.
            }
            finally
            {
                await sendCancellation.CancelAsync().ConfigureAwait(false);
            }
            if (reusable) throw new UnblockedCommandCanceledException(cancellationToken);
            // If cleanup failed, reclaim any late result (or observe cancellation/failure).
            try
            {
                using var abandoned = await pending.ConfigureAwait(false);
            }
            catch (Exception) { /* The original cancellation remains the public failure. */ }
            throw new OperationCanceledException(cancellationToken);
        }
    }
}
