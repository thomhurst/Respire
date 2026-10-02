using Respire.Networking;
using Respire.Infrastructure;

namespace Respire.Internal;

internal static class ReadFallbackPolicy
{
    // Keep one candidate per health tier so retirement of the healthy fallback still
    // permits a stale-serving replica. Equal-ranked candidates retain round-robin order.
    internal struct ReplicaCandidates<T>
    {
        private T _linked;
        private T _unlinked;
        private bool _hasLinked;
        private bool _hasUnlinked;
        private bool _unlinkedIsLocal;

        internal bool Offer(T candidate, bool local, bool linked, RespireReadFrom policy)
        {
            if (linked)
            {
                if (!UsesAvailabilityZone(policy) || local) return true;
                if (!_hasLinked) _linked = candidate;
                _hasLinked = true;
            }
            else if (!_hasUnlinked || UsesAvailabilityZone(policy) && local && !_unlinkedIsLocal)
            {
                _unlinked = candidate;
                _hasUnlinked = true;
                _unlinkedIsLocal = local;
            }
            return false;
        }

        internal bool TryTake(out T candidate)
        {
            if (_hasLinked)
            {
                _hasLinked = false;
                candidate = _linked;
                return true;
            }
            if (_hasUnlinked)
            {
                _hasUnlinked = false;
                candidate = _unlinked;
                return true;
            }
            candidate = default!;
            return false;
        }
    }

    internal static bool ShouldProbeLocalPrimary(
        RespireReadFrom policy, RespireConnectionMultiplexer? primary, string? clientZone)
        => policy == RespireReadFrom.AzAffinityReplicasAndPrimary
            && (primary is null || primary.MayBeInAvailabilityZone(clientZone));

    internal static bool UsesAvailabilityZone(RespireReadFrom policy)
        => policy is RespireReadFrom.AzAffinity or RespireReadFrom.AzAffinityReplicasAndPrimary;

    internal static bool AllowsPrimaryFallback(RespireReadFrom policy)
        => policy == RespireReadFrom.ReplicaPreferred || UsesAvailabilityZone(policy);

    internal static bool IsSameZone(RespireConnection connection, string? clientZone)
        => clientZone is not null && string.Equals(connection.AvailabilityZone, clientZone, StringComparison.Ordinal);

    // Once a preferred read switches roles, redirects and retirement keep that fallback role.
    internal static RespireReadFrom AfterRoleSwitch(bool selectedReplica)
        => selectedReplica ? RespireReadFrom.Replica : RespireReadFrom.Primary;

    /// <summary>
    /// True when a preferred policy should retry a read on the other server role after
    /// <paramref name="error"/>. Reads are idempotent, so one retry is safe.
    /// </summary>
    /// <remarks>
    /// Covers server-side unavailability of the chosen role: <c>LOADING</c> while a node loads its
    /// dataset, <c>MASTERDOWN</c> from a replica that lost its primary link, and <c>CLUSTERDOWN</c>
    /// from a node whose view of the cluster is failing. Strict policies never switch roles.
    /// </remarks>
    internal static bool CanFallBackToOtherRole(
        RespireServerException error, RespireReadFrom readFrom, int? slot, bool onReplica)
    {
        if (slot is null || error.Code is not (RespireErrorCodes.Loading or RespireErrorCodes.MasterDown
            or RespireErrorCodes.ClusterDown))
        {
            return false;
        }

        return readFrom switch
        {
            RespireReadFrom.ReplicaPreferred => onReplica,
            RespireReadFrom.PrimaryPreferred => !onReplica,
            RespireReadFrom.AzAffinity or RespireReadFrom.AzAffinityReplicasAndPrimary => true,
            _ => false,
        };
    }

    internal static bool IsReplicaConnection(RespireConnection connection)
        => connection.Multiplexer?.Options.ReadOnly == true;

    // ASK sends one command to the importing primary during slot migration. Its replicas do not
    // own the key yet, so a strict Replica read fails instead of silently reading from a primary.
    internal static bool IsStrictReplicaAsk(RespireServerException error, RespireReadFrom readFrom)
        => readFrom == RespireReadFrom.Replica && error.Code == RespireErrorCodes.Ask;

    internal static RespireConnectionException CreateStrictReplicaAskException(RespireServerException error, int? slot)
        => new($"Redis Cluster slot {slot} is migrating and ASK redirects to a primary, so a Replica read cannot follow it.", error);
}
