using Respire.Networking;

namespace Respire.Internal;

internal static class ReadFallbackPolicy
{
    // Once a preferred read switches roles, redirects and retirement keep that fallback role.
    internal static RespireReadFrom AfterRoleSwitch(RespireReadFrom policy)
        => policy switch
        {
            RespireReadFrom.ReplicaPreferred => RespireReadFrom.Primary,
            RespireReadFrom.PrimaryPreferred => RespireReadFrom.Replica,
            _ => policy,
        };

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
