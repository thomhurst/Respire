using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClusterNotificationCoordinatorTests
{
    [Test]
    public async Task RoutesShareOneServerSubscriptionAndRetireAfterLastConsumerLeaves()
    {
        var coordinator = new ClusterNotificationCoordinator();
        var endpoint = new RespireEndpoint("127.0.0.1", 26379);
        var node = new ClusterNotificationNode(endpoint);
        var channel = new RespireChannel("events");
        var first = new RespireSubscription(null!, SubscriptionKind.Channel, [channel], 4, SubscriptionOverflow.DropOldest);
        var second = new RespireSubscription(null!, SubscriptionKind.Channel, [channel], 4, SubscriptionOverflow.DropOldest);
        coordinator.RegisterNode(node);

        await Assert.That(coordinator.AddRoute(node, first, channel)).IsTrue();
        await Assert.That(coordinator.AddRoute(node, second, channel)).IsFalse();
        await Assert.That(coordinator.RemoveRoute(node, SubscriptionKind.Channel, first, channel)).IsFalse();
        await Assert.That(coordinator.TryGetNode(endpoint)).IsSameReferenceAs(node);
        await Assert.That(coordinator.RemoveRoute(node, SubscriptionKind.Channel, second, channel)).IsTrue();
        await Assert.That(coordinator.TryRetireNode(node)).IsTrue();
        await Assert.That(coordinator.TryGetNode(endpoint)).IsNull();
        await Assert.That(coordinator.TryRetireNode(node)).IsFalse();
    }
}
