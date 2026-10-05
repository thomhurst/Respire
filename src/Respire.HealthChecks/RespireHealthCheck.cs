using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Respire.HealthChecks;

/// <summary>Checks Redis through an existing client or the active client of an existing failover group.</summary>
/// <remarks>
/// This check does not own or dispose clients. IRespireHealthProbe implementations reuse existing command connections.
/// Other IRespireClient implementations are probed through PingAsync when IsConnected is true;
/// their implementation controls connection creation. Probe waits are bounded even if asynchronous work ignores cancellation.
/// </remarks>
public sealed class RespireHealthCheck : IHealthCheck
{
    private const string AllNodesUnsupported = "All-node probes require a client implementing IRespireHealthProbe.";
    private const int ProviderCompletionGraceMilliseconds = 250;
    private readonly IRespireClient? _client;
    private readonly RespireFailoverGroup? _group;
    private readonly RespireHealthCheckOptions _options;

    /// <summary>Creates a health check for an existing client.</summary>
    /// <exception cref="NotSupportedException">All-node probing is requested for a client without IRespireHealthProbe.</exception>
    public RespireHealthCheck(IRespireClient client, RespireHealthCheckOptions? options = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? new();
        _options.Validate();
        if (_options.ProbeAllNodes && client is not IRespireHealthProbe)
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
            // Let cooperative providers finish assembling per-node timeout observations before
            // the backstop interrupts a provider that ignores its own timeout contract.
            var waitTimeout = client is IRespireHealthProbe
                ? TimeSpan.FromMilliseconds(Math.Min(_options.ProbeTimeout.TotalMilliseconds + ProviderCompletionGraceMilliseconds, uint.MaxValue - 1d))
                : _options.ProbeTimeout;
            deadline.CancelAfter(waitTimeout);
            var errors = new List<Exception>();
            RespireNodeHealth[] nodes;
            if (client is IRespireHealthProbe probe)
            {
                var results = await WaitForProbeAsync(probe.ProbeHealthAsync(new()
                {
                    ProbeAllNodes = _options.ProbeAllNodes,
                    MaxConcurrentProbes = _options.MaxConcurrentProbes,
                    Timeout = _options.ProbeTimeout,
                }, deadline.Token).AsTask(), deadline.Token).ConfigureAwait(false);
                if (results is null || results.Any(result => result is null))
                    throw new InvalidOperationException("The health probe provider returned null results or a null node result.");
                nodes = results.Select(result => new RespireNodeHealth(result.Endpoint, result.IsConnected,
                    result.Latency, result.Error?.GetType().Name)).ToArray();
                errors.AddRange(results.Where(result => result.Error is not null).Select(result => result.Error!));
            }
            else
            {
                if (_options.ProbeAllNodes)
                    throw new NotSupportedException(AllNodesUnsupported);
                if (!client.IsConnected)
                    throw new RespireConnectionException("The existing Respire client is not connected.");
                var latency = await WaitForProbeAsync(client.PingAsync(deadline.Token).AsTask(), deadline.Token).ConfigureAwait(false);
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

    private static async Task<T> WaitForProbeAsync<T>(Task<T> task, CancellationToken cancellationToken)
    {
        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // WaitAsync removes its observer when cancelled. A provider that ignores cancellation
            // can still fault later, after this health check has already returned.
            _ = task.ContinueWith(static completed => { _ = completed.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
    }
}
