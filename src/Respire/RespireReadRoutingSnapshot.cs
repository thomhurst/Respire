namespace Respire;

/// <summary>An immutable observation of the last completed latency-band read selection.</summary>
/// <remarks>
/// This snapshot performs no I/O and holds no transports. Eligibility reflects the observed
/// role, link, zone and RTT at selection time; topology and health can change afterward.
/// Client views share the router's last observation. Concurrent selections may publish in any order.
/// </remarks>
public sealed class RespireReadRoutingSnapshot
{
    internal RespireReadRoutingSnapshot(RespireReadFrom policy, int? slot, IReadOnlyList<RespireEndpoint> eligible)
    {
        ReadFrom = policy;
        ClusterSlot = slot;
        EligibleEndpoints = eligible;
    }

    /// <summary>UTC time at which the selection snapshot was captured.</summary>
    public DateTimeOffset CapturedAt { get; } = DateTimeOffset.UtcNow;
    /// <summary>Policy used for this selection.</summary>
    public RespireReadFrom ReadFrom { get; }
    /// <summary>Selected Cluster slot, or null for standalone/Sentinel routing.</summary>
    public int? ClusterSlot { get; }
    /// <summary>Endpoints in the best eligible tier and latency band, in topology order.</summary>
    public IReadOnlyList<RespireEndpoint> EligibleEndpoints { get; }
}
