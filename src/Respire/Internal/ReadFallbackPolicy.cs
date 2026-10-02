using System.Buffers;
using System.Runtime.CompilerServices;
using Respire.Infrastructure;
using Respire.Networking;

namespace Respire.Internal;

internal static class ReadFallbackPolicy
{
    // Retain every fallback until selection finishes: an earlier socket can retire while a
    // later candidate is checked. One candidate stays inline; larger sets borrow pooled storage.
    internal struct ReplicaCandidates<T>
    {
        private (T Value, int Rank) _first;
        private (T Value, int Rank)[]? _overflow;
        private int _count;
        private int _rank;
        private int _next;

        internal bool Offer(T candidate, bool local, bool linked, RespireReadFrom policy)
        {
            var useZone = UsesAvailabilityZone(policy);
            if (linked && (!useZone || local)) return true;
            var rank = 2;
            if (linked) rank = 0;
            else if (useZone && local) rank = 1;
            var ranked = (candidate, rank);
            if (_count == 0) _first = ranked;
            else
            {
                if (_overflow is null)
                {
                    _overflow = ArrayPool<(T, int)>.Shared.Rent(4);
                    _overflow[0] = _first;
                }
                else if (_count == _overflow.Length)
                {
                    var larger = ArrayPool<(T, int)>.Shared.Rent(_count * 2);
                    _overflow.AsSpan(0, _count).CopyTo(larger);
                    Return(_overflow);
                    _overflow = larger;
                }
                _overflow[_count] = ranked;
            }
            _count++;
            return false;
        }

        internal bool TryTake(out T candidate)
        {
            // Three stable passes retain health/zone priority and rotation within each tier.
            while (_rank < 3)
            {
                while (_next < _count)
                {
                    var ranked = _overflow is null ? _first : _overflow[_next];
                    _next++;
                    if (ranked.Rank != _rank) continue;
                    candidate = ranked.Value;
                    return true;
                }
                _rank++;
                _next = 0;
            }
            candidate = default!;
            return false;
        }

        internal void Dispose()
        {
            if (_overflow is not null) Return(_overflow);
            this = default;
        }

        private static void Return((T, int)[] values)
            => ArrayPool<(T, int)>.Shared.Return(values,
                clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<(T, int)>());
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
