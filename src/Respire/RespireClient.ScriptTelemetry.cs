using Respire.Internal;
using Respire.Networking;

namespace Respire;

public sealed partial class RespireClient
{
    /// <summary>Shares one selected script scope across cluster redirects and NOSCRIPT fallback.</summary>
    private sealed class ClusterScriptTelemetry(ClientCore core, RespireScript script, RespireTelemetry.OperationStart started)
    {
        private readonly RespireTelemetry.OperationScope _scope = RespireTelemetry.StartOperation(
            script.EvalShaOperation, null, 6379, core.Options.Database, storedProcedureName: script.Sha1, started: started);
        private RespireConnection? _connection;

        /// <summary>Updates the selected peer without resetting the logical operation's start time.</summary>
        internal void UseConnection(RespireConnection connection)
        {
            _connection = connection;
            _scope.UpdateServerEndpoint(connection.Host, connection.Port);
        }

        /// <summary>Records one completion, leaving the endpoint absent when acquisition failed.</summary>
        internal void Complete(Exception? error = null)
            => _scope.Complete(script.EvalShaOperation, _connection?.Host, _connection?.Port ?? 6379,
                core.Options.Database, script.Sha1, error, _connection);
    }
}
