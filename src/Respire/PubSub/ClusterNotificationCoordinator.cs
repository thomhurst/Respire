using Respire.Networking;
using Respire;

namespace Respire.Internal;

/// <summary>Owns the state shared by cluster keyspace notification routes and reconciliation.</summary>
/// <remarks>
/// The hub uses <see cref="Gate"/> for its existing cross-subscription lock order. Keeping that
/// gate here gives notification state one owner while allowing lifecycle operations in the hub to
/// update ordinary and cluster subscriptions atomically.
/// </remarks>
internal sealed class ClusterNotificationCoordinator
{
    internal readonly object Gate = new();
    internal readonly Dictionary<RespireEndpoint, ClusterNotificationNode> Nodes = [];
    internal readonly Dictionary<RespireSubscription, NotificationSubscriptionState> Subscriptions = [];
    internal readonly HashSet<RespireEndpoint> DisconnectedEndpoints = [];
    internal readonly HashSet<RespireEndpoint> ExhaustedEndpoints = [];
    internal readonly HashSet<RespireEndpoint> RetryingEndpoints = [];
    internal readonly HashSet<RespireSubscription> ScheduledReconciliationSubscriptions = [];
    internal long TopologyVersion;
    internal NotificationTopology? LatestTopology;
    internal bool ReconciliationScheduled;
    internal bool ReconciliationScheduledAgain;
    internal bool ReconciliationHasExaminedSubscriptions;
    internal long ScheduledReconciliationVersion;

    internal bool AddRoute(ClusterNotificationNode node, RespireSubscription subscription, RespireChannel name)
    {
        var routeName = name.WithoutNotificationMetadata();
        lock (Gate)
        lock (node.Gate)
        {
            var routes = node.Routes[(int)subscription.Kind];
            var added = !routes.TryGetValue(routeName, out var consumers);
            if (added) routes.Add(routeName, consumers = []);
            if (!consumers.Contains(subscription)) consumers.Add(subscription);
            return added;
        }
    }

    internal bool RegisterNode(ClusterNotificationNode node)
    {
        lock (Gate)
        {
            if (node.Retired) return false;
            Nodes[node.Endpoint] = node;
            return true;
        }
    }

    internal bool RemoveRoute(ClusterNotificationNode node, SubscriptionKind kind,
        RespireSubscription subscription, RespireChannel name)
    {
        lock (Gate)
        lock (node.Gate)
        {
            var routes = node.Routes[(int)kind];
            if (!routes.TryGetValue(name, out var consumers) || !consumers.Remove(subscription)) return false;
            if (consumers.Count != 0) return false;
            routes.Remove(name);
            return true;
        }
    }

    internal bool TryRetireNode(ClusterNotificationNode node)
    {
        lock (Gate)
        {
            lock (node.Gate)
            {
                if (node.Retired) return false;
                node.Retired = true;
                Interlocked.Increment(ref node.Epoch);
            }
            if (Nodes.TryGetValue(node.Endpoint, out var current) && ReferenceEquals(current, node))
                Nodes.Remove(node.Endpoint);
            return true;
        }
    }

    internal ClusterNotificationNode? TryGetNode(RespireEndpoint endpoint)
    {
        lock (Gate) return Nodes.TryGetValue(endpoint, out var node) ? node : null;
    }
}

internal sealed class ClusterNotificationNode(RespireEndpoint endpoint)
{
    internal readonly RespireEndpoint Endpoint = endpoint;
    // Route writers hold the coordinator gate and then this gate; delivery takes this gate only.
    internal readonly object Gate = new();
    internal readonly ByteRouteDictionary<List<RespireSubscription>>[] Routes = [new(), new(), new()];
    internal RespireConnection? Connection;
    internal long Epoch;
    internal long PendingEpoch;
    internal long IssuedEpoch;
    internal volatile bool Retired;
    internal DateTimeOffset? InterruptedAt;

    internal bool IsEmpty
    {
        get
        {
            foreach (var routes in Routes)
                if (!routes.IsEmpty) return false;
            return true;
        }
    }
}

internal sealed record NotificationTopology(long Version, RespireEndpoint[] Endpoints, bool Authoritative);

internal sealed class NotificationSubscriptionState
{
    internal readonly HashSet<RespireEndpoint> Coverage = [];
    internal int FailedAttempts;
    internal RespireEndpoint? FailingEndpoint;
    internal bool RetryingOutage;
    internal DateTimeOffset? ReplayRejectedAt;
}
