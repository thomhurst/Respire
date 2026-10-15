namespace Respire;

/// <summary>One slot group of a split Cluster command failed. Other groups may have succeeded.</summary>
public sealed class RespireClusterPartitionException : RespireException
{
    /// <summary>The command being split.</summary>
    public string CommandName { get; }

    /// <summary>The hash slot of the failed group.</summary>
    public int Slot { get; }

    /// <summary>Creates a failure for one slot group, retaining the underlying error.</summary>
    public RespireClusterPartitionException(string commandName, int slot, RespireEndpoint? endpoint, Exception innerException)
        : base($"{commandName} failed for Cluster slot {slot} on node {endpoint?.ToString() ?? "unknown (route unavailable)"}. Other slot groups may have succeeded.", innerException)
    {
        CommandName = commandName;
        Slot = slot;
        Endpoint = endpoint;
        HasAmbiguousEndpoint = endpoint is null;
    }
}
