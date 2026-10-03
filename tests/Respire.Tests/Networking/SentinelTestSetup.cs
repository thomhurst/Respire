using Respire.Internal;

namespace Respire.Tests.Networking;

internal static class SentinelTestSetup
{
    internal static Task CompleteReadSetupAsync(RespireClient client)
        => WaitForStartupAsync(client, refreshReplicas: true);

    internal static async Task WaitForStartupAsync(RespireClient client, bool refreshReplicas = false)
    {
        // Monitor startup queues a delivery-gap rediscovery. Finish it with healthy
        // transports before arming dedicated failures or counting acquisition attempts.
        var sentinel = client.Core.Sentinel!;
        using var setup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await WaitForSubscriptionsAsync(sentinel, 1, setup.Token);
        if (sentinel.NotificationRediscovery is { } startup) await startup.WaitAsync(setup.Token);
        if (refreshReplicas) await client.Core.ReadRouter.RefreshNowAsync(setup.Token);
    }

    internal static Task WaitForSubscriptionsAsync(
        SentinelRouter sentinel, int expectedSubscriptions, CancellationToken cancellationToken)
        => sentinel.Monitoring.WaitForSubscriptionsAsync(expectedSubscriptions, cancellationToken);
}
