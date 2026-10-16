using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire;

public sealed partial class RespireClient
{
    private ValueTask<Stream?> SendBulkStreamOnConnectionUncheckedAsync<TCommand>(
        string operation, RespireConnection connection, TCommand command,
        CancellationToken cancellationToken, bool sendAsking,
        Action<Exception?>? onFrameCompleted = null,
        RespireTelemetry.ErrorObservation observation = default, bool pinToConnection = false,
        CommandDeadline commandDeadline = default)
        where TCommand : struct, IRespCommand
    {
        var preferredZone = GetTransportReadZone(in command);
        if (ShouldSkipTracking(operation, in command) && connection.ClientTrackingEnabled)
        {
            var caching = new ClientCachingCommand(optOut: true);
            if (sendAsking)
            {
                var asking = new ProtocolCommand<Cmd>(new Cmd(new Verb(-1, "ASKING")));
                return connection.SendPrefixedBulkStreamAsync(
                    in asking, in caching, in command, cancellationToken, operation, onFrameCompleted,
                    preferredZone, observation, pinToConnection, commandDeadline);
            }
            return connection.SendPrefixedBulkStreamAsync(
                in caching, in command, cancellationToken, operation, onFrameCompleted,
                preferredZone, observation, pinToConnection, commandDeadline);
        }
        return sendAsking
            ? ClusterRouter.SendAskingBulkStreamAsync(connection, in command, cancellationToken, operation,
                onFrameCompleted, preferredZone, observation, pinToConnection, commandDeadline)
            : connection.SendBulkStreamAsync(in command, cancellationToken, operation, onFrameCompleted,
                preferredZone, observation, pinToConnection, commandDeadline);
    }
}
