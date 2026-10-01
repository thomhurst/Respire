using System.Diagnostics;
using System.Runtime.CompilerServices;
using Respire.Protocol;

namespace Respire.Internal;

/// <summary>Whether a replica connection's last ROLE check still covers a read.</summary>
internal enum ReplicaValidation
{
    /// <summary>Validated within the revalidation interval; serve without a ROLE round trip.</summary>
    Fresh,

    /// <summary>
    /// Validated within two intervals. Serve it while another caller revalidates, so one slow
    /// ROLE check does not stall every concurrent read.
    /// </summary>
    Stale,

    /// <summary>Never validated, or validated too long ago; ROLE must run before use.</summary>
    Required,
}

/// <summary>
/// ROLE validation and health state for one read replica: when each physical connection last
/// passed ROLE, whether the replica's link to its primary is up, and the failure cooldown. It
/// holds no connection, so its rules can be tested without a server.
/// </summary>
/// <typeparam name="TConnection">The physical connection type; weakly keyed.</typeparam>
internal sealed class ReplicaHealth<TConnection> where TConnection : class
{
    // Reconnects publish new connection objects, which are validated before use; dead
    // connections drop out with their weak keys.
    private readonly ConditionalWeakTable<TConnection, StrongBox<long>> _validated = new();
    private long _failedAt;
    private volatile bool _replicationLinkDown;

    /// <summary>True when the last ROLE check found the replica's link to its primary down.</summary>
    internal bool IsReplicationLinkDown => _replicationLinkDown;

    /// <summary>Classifies <paramref name="connection"/> against <paramref name="interval"/>.</summary>
    internal ReplicaValidation Check(TConnection connection, TimeSpan interval)
    {
        if (!_validated.TryGetValue(connection, out var checkedAt)) return ReplicaValidation.Required;
        var elapsed = Stopwatch.GetElapsedTime(Volatile.Read(ref checkedAt.Value));
        if (elapsed < interval) return ReplicaValidation.Fresh;
        return elapsed < interval + interval ? ReplicaValidation.Stale : ReplicaValidation.Required;
    }

    /// <summary>Records a ROLE reply taken at <paramref name="checkedAt"/> (a Stopwatch timestamp).</summary>
    /// <returns>False, and forgets the connection, when the node is not a replica.</returns>
    internal bool Record(TConnection connection, long checkedAt, in RespValue role)
    {
        if (!IsReplica(in role, out var linkUp))
        {
            _validated.Remove(connection);
            return false;
        }
        _replicationLinkDown = !linkUp;
        _validated.AddOrUpdate(connection, new StrongBox<long>(checkedAt));
        Volatile.Write(ref _failedAt, 0);
        return true;
    }

    /// <summary>Starts the failure cooldown.</summary>
    internal void MarkFailed() => Volatile.Write(ref _failedAt, Stopwatch.GetTimestamp());

    /// <summary>True while a failure recorded by <see cref="MarkFailed"/> is within <paramref name="cooldown"/>.</summary>
    internal bool IsCoolingDown(TimeSpan cooldown)
    {
        var failedAt = Volatile.Read(ref _failedAt);
        return failedAt != 0 && Stopwatch.GetElapsedTime(failedAt) < cooldown;
    }

    // ROLE on a replica: ["slave" | "replica", primary-host, primary-port, link-state, offset].
    // A link state other than "connected" means the replica is connecting or syncing to its
    // primary and can serve arbitrarily stale data.
    internal static bool IsReplica(in RespValue role, out bool linkUp)
    {
        linkUp = false;
        if (role.Type != RespDataType.Array) return false;
        var fields = role.AsArray();
        if (fields.IsEmpty || fields[0].Type is not (RespDataType.BulkString or RespDataType.SimpleString)) return false;
        if (fields[0].AsString() is not ("slave" or "replica")) return false;
        linkUp = fields.Length >= 4
            && fields[3].Type is (RespDataType.BulkString or RespDataType.SimpleString)
            && fields[3].AsString() == "connected";
        return true;
    }
}
