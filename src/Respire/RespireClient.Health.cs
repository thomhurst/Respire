using System.Diagnostics;
using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;

namespace Respire;

public sealed partial class RespireClient : IRespireHealthProbe
{
    /// <inheritdoc/>
    public async ValueTask<RespireHealthProbeResult[]> ProbeHealthAsync(
        RespireHealthProbeOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new();
        options.Validate();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.Timeout);
        var targets = CaptureHealthConnections(options.ProbeAllNodes);
        using var capacity = new SemaphoreSlim(options.MaxConcurrentProbes);
        var results = await Task.WhenAll(targets.Select(target => ProbeHealthConnectionAsync(
            target.Endpoint, target.Connection, capacity, deadline.Token))).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return results;
    }

    private static async Task<RespireHealthProbeResult> ProbeHealthConnectionAsync(
        RespireEndpoint endpoint, RespireConnection? connection, SemaphoreSlim capacity, CancellationToken cancellationToken)
    {
        var connected = connection?.IsAcceptingCommands == true;
        var entered = false;
        try
        {
            if (!connected) throw new RespireConnectionException("The node has no existing usable command connection.");
            await capacity.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            // Admission can win the semaphore race while deadline cancellation is releasing
            // another probe. Do not publish a new PING with an already-canceled token.
            cancellationToken.ThrowIfCancellationRequested();
            var started = Stopwatch.GetTimestamp();
            using var reply = await connection!.SendAsync(new RawCommand(RespCommands.Ping),
                cancellationToken, commandName: "PING", pinToConnection: true).ConfigureAwait(false);
            reply.ThrowIfError();
            if (reply.AsString() != "PONG") throw new RespireProtocolException("PING did not return PONG.");
            return new(endpoint, true, Stopwatch.GetElapsedTime(started));
        }
        catch (Exception error)
        {
            return new(endpoint, connected, null, error);
        }
        finally
        {
            if (entered) capacity.Release();
        }
    }

    // Retain this internal entry point for older HealthChecks binaries. New integrations use IRespireHealthProbe.
    // Capture one routing publication. Never discover topology or open a probe connection.
    internal (RespireEndpoint Endpoint, RespireConnection? Connection)[] CaptureHealthConnections(bool allNodes)
    {
        ObjectDisposedException.ThrowIf(_core.Disposed, this);
        if (_core.Cluster is { } cluster)
        {
            var snapshot = cluster.RoutingSnapshot;
            if (!snapshot.IsComplete)
                throw new RespireConnectionException("Redis Cluster topology is not available for a health check.");
            var nodes = allNodes ? snapshot.Masters.Concat(snapshot.ReplicaNodes) : snapshot.Masters;
            var targets = nodes.Distinct().Select(node =>
                (Endpoint: new RespireEndpoint(node.Host, node.Port), Connection: node.GetExistingHealthConnection())).ToArray();
            if (allNodes) return targets;
            foreach (var candidate in targets)
            {
                if (candidate.Connection is not null) return [candidate];
            }
            return targets.Take(1).ToArray();
        }

        var primary = _core.Sentinel is { } sentinel ? sentinel.Current?.Multiplexer : _core.Multiplexer;
        if (primary is null)
            throw new RespireConnectionException("Sentinel has no validated primary for a health check.");
        (RespireEndpoint Endpoint, RespireConnection? Connection) target =
            (new(primary.Host, primary.Port), primary.GetExistingHealthConnection());
        return allNodes ? new[] { target }.Concat(_core.ReadRouter.CaptureHealthConnections()).ToArray() : [target];
    }
}
