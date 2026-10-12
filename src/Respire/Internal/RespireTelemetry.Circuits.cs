using System.Diagnostics;

namespace Respire.Internal;

internal static partial class RespireTelemetry
{
    internal static void RecordCircuitTransition(RespireEndpoint endpoint, EndpointCircuitState from, EndpointCircuitState to)
    {
        if (!IsMetricEnabled(RespireMetricGroups.Resiliency, CircuitTransitions)) return;
        var tags = CircuitTags(endpoint);
        tags.Add("respire.circuit.state.from", CircuitStateName(from));
        tags.Add("respire.circuit.state.to", CircuitStateName(to));
        try { CircuitTransitions.Add(1, in tags); }
        catch (Exception) { /* Listener failures must not change circuit admission or recovery. */ }
    }

    internal static void RecordCircuitRejection(RespireEndpoint endpoint)
    {
        if (!IsMetricEnabled(RespireMetricGroups.Resiliency, CircuitRejections)) return;
        var tags = CircuitTags(endpoint);
        try { CircuitRejections.Add(1, in tags); }
        catch (Exception) { /* Listener failures must not replace the circuit rejection. */ }
    }

    private static TagList CircuitTags(RespireEndpoint endpoint)
    {
        var tags = new TagList { LibraryTag, SystemTag, new("server.address", endpoint.Host) };
        if (endpoint.Port != DefaultRedisPort) tags.Add("server.port", endpoint.Port);
        return tags;
    }

    private static string CircuitStateName(EndpointCircuitState state) => state switch
    {
        EndpointCircuitState.Closed => "closed",
        EndpointCircuitState.Open => "open",
        EndpointCircuitState.HalfOpen => "half_open",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };
}
