using Respire.Internal;
using Respire.Networking;

namespace Respire;

public sealed partial class RespireClient
{
    /// <summary>Shares one selected script scope across cluster redirects and NOSCRIPT fallback.</summary>
    private sealed class ClusterScriptTelemetry(ClientCore core, RespireScript script, RespireTelemetry.OperationStart started)
    {
        private RespireTelemetry.OperationScope _scope;
        private RespireConnection? _connection;

        /// <summary>Updates the selected peer without resetting the logical operation's start time.</summary>
        internal void UseConnection(RespireConnection connection)
        {
            if (_connection is null)
                _scope = RespireTelemetry.StartOperation(script.EvalShaOperation, connection,
                    core.Options.Database, storedProcedureName: script.Sha1, started: started);
            else
                _scope.UpdateServerEndpoint(connection.Host, connection.Port);
            _connection = connection;
        }

        /// <summary>Records one completion, leaving the endpoint absent when acquisition failed.</summary>
        internal void Complete(Exception? error = null)
        {
            if (_connection is null)
            {
                if (error is not null)
                    RespireTelemetry.RecordUnroutedFailure(script.EvalShaOperation, core.Options.Database,
                        started, error, script.Sha1);
                return;
            }
            _scope.Complete(script.EvalShaOperation, _connection.Host, _connection.Port,
                core.Options.Database, script.Sha1, error, _connection);
        }
    }
}
