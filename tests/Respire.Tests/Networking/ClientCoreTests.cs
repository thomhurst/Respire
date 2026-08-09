using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClientCoreTests
{
    [Test]
    public async Task SubscriberRecovery_WaitsForCommandRecoveryBeforePublishingConnected()
    {
        await using var core = new ClientCore(new RespireOptions());
        var states = new List<RespireConnectionState>();
        core.ConnectionStateChanged += states.Add;

        core.NotifyCommandStateChanged(RespireConnectionState.Reconnecting);
        core.NotifySubscriptionStateChanged(RespireConnectionState.Reconnecting);
        core.NotifySubscriptionStateChanged(RespireConnectionState.Connected);

        await Assert.That(states).IsEquivalentTo([RespireConnectionState.Reconnecting]);

        core.NotifyCommandStateChanged(RespireConnectionState.Connected);

        await Assert.That(states).IsEquivalentTo(
            [RespireConnectionState.Reconnecting, RespireConnectionState.Connected]);
    }

    [Test]
    public async Task CommandRecovery_WaitsForSubscriberRecoveryBeforePublishingConnected()
    {
        await using var core = new ClientCore(new RespireOptions());
        var states = new List<RespireConnectionState>();
        core.ConnectionStateChanged += states.Add;

        core.NotifySubscriptionStateChanged(RespireConnectionState.Reconnecting);
        core.NotifyCommandStateChanged(RespireConnectionState.Reconnecting);
        core.NotifyCommandStateChanged(RespireConnectionState.Connected);

        await Assert.That(states).IsEquivalentTo([RespireConnectionState.Reconnecting]);

        core.NotifySubscriptionStateChanged(RespireConnectionState.Connected);

        await Assert.That(states).IsEquivalentTo(
            [RespireConnectionState.Reconnecting, RespireConnectionState.Connected]);
    }
}
