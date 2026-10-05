using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;

namespace Respire.HealthChecks;

/// <summary>Checks Redis through an existing client or the active client of an existing failover group.</summary>
/// <remarks>
/// This check does not own or dispose clients. RespireClient probes reuse existing command connections.
/// Other IRespireClient implementations are probed through PingAsync when IsConnected is true;
/// their implementation controls connection creation and cancellation handling.
/// </remarks>
public sealed class RespireHealthCheck : IHealthCheck
{
    private const string AllNodesUnsupported = "All-node probes require a RespireClient with an owned routing snapshot.";
    private readonly IRespireClient? _client;
    private readonly RespireFailoverGroup? _group;
    private readonly RespireHealthCheckOptions _options;

    /// <summary>Creates a health check for an existing client.</summary>
    /// <exception cref="NotSupportedException">All-node probing is requested for a custom client.</exception>
    public RespireHealthCheck(IRespireClient client, RespireHealthCheckOptions? options = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? new();
        _options.Validate();
        if (_options.ProbeAllNodes && client is not RespireClient)
            throw new NotSupportedException(AllNodesUnsupported);
    }

    /// <summary>Creates a health check that selects the group's active client on every invocation.</summary>
    public RespireHealthCheck(RespireFailoverGroup group, RespireHealthCheckOptions? options = null)
    {
        _group = group ?? throw new ArgumentNullException(nameof(group));
        _options = options ?? new();
        _options.Validate();
    }

    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var data = new Dictionary<string, object>();
        try
        {
            IReadOnlyList<RespireFailoverEndpointStatus>? statuses = null;
            if (_group is not null)
            {
                statuses = _group.GetEndpointStatuses();
                data["failoverEndpoints"] = statuses;
                data["failoverConnected"] = _group.IsConnected;
            }
            var client = _group?.ActiveClient ?? _client!;
            data["connected"] = client.IsConnected;
            if (_options.IncludeClientSideCache && client.ClientSideCache is { } cache)
                data["clientSideCache"] = cache.GetStatistics();

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_options.ProbeTimeout);
            using var capacity = new SemaphoreSlim(_options.MaxConcurrentProbes);
            var errors = new List<Exception>();
            RespireNodeHealth[] nodes;
            if (client is RespireClient concrete)
            {
                var targets = concrete.CaptureHealthConnections(_options.ProbeAllNodes);
                var results = await Task.WhenAll(targets.Select(target => ProbeAsync(
                    target.Endpoint, target.Connection, capacity, deadline.Token))).ConfigureAwait(false);
                nodes = results.Select(result => result.Node).ToArray();
                errors.AddRange(results.Where(result => result.Error is not null).Select(result => result.Error!));
            }
            else
            {
                if (_options.ProbeAllNodes)
                    throw new NotSupportedException(AllNodesUnsupported);
                if (!client.IsConnected)
                    throw new RespireConnectionException("The existing Respire client is not connected.");
                var latency = await client.PingAsync(deadline.Token).ConfigureAwait(false);
                nodes = [new(client.Endpoint, true, latency, null)];
            }
            data["nodes"] = nodes;
            data["failedNodes"] = errors.Count;
            cancellationToken.ThrowIfCancellationRequested();
            if (nodes.Length == 0)
                throw new RespireConnectionException("No data nodes are available for a health check.");
            if (errors.Count != 0)
                return new(context.Registration.FailureStatus, $"{errors.Count} of {nodes.Length} Respire node probes failed.",
                    new AggregateException(errors), data);
            var slow = _options.DegradedLatency is { } threshold && nodes.Any(node => node.Latency >= threshold);
            var failover = _options.DegradeOnFailover && statuses?.Any(status => !status.IsHealthy) == true;
            if (slow)
                return HealthCheckResult.Degraded("Respire PING latency exceeds the threshold.", data: data);
            if (failover)
                return HealthCheckResult.Degraded("A failover candidate is unhealthy.", data: data);
            return HealthCheckResult.Healthy("Respire data nodes responded to PING.", data);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception error)
        {
            return new(context.Registration.FailureStatus, "Respire health probe failed.", error, data);
        }
    }

    private static async Task<(RespireNodeHealth Node, Exception? Error)> ProbeAsync(
        RespireEndpoint endpoint, RespireConnection? connection,
        SemaphoreSlim capacity, CancellationToken cancellationToken)
    {
        var connected = connection?.IsAcceptingCommands == true;
        var entered = false;
        try
        {
            if (!connected)
                throw new RespireConnectionException("The node has no existing usable command connection.");
            await capacity.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            var started = Stopwatch.GetTimestamp();
            using var reply = await connection!.SendAsync(new RawCommand(RespCommands.Ping),
                cancellationToken, commandName: "PING", pinToConnection: true).ConfigureAwait(false);
            reply.ThrowIfError();
            if (reply.AsString() != "PONG") throw new RespireProtocolException("PING did not return PONG.");
            return (new(endpoint, true, Stopwatch.GetElapsedTime(started), null), null);
        }
        catch (Exception error)
        {
            return (new(endpoint, connected, null, error.GetType().Name), error);
        }
        finally
        {
            if (entered) capacity.Release();
        }
    }
}
