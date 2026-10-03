using Respire.Internal;

namespace Respire.Tests.Networking;

internal static class SentinelTestSetup
{
    internal static async Task CompleteReadSetupAsync(RespireClient client)
    {
        // Monitor startup queues a delivery-gap rediscovery. Finish it with healthy
        // transports before arming dedicated failures or counting acquisition attempts.
        var sentinel = client.Core.Sentinel!;
        using var setup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await WaitForSubscriptionsAsync(sentinel, 1, setup.Token);
        if (sentinel.NotificationRediscovery is { } startup) await startup.WaitAsync(setup.Token);
        await client.Core.ReadRouter.RefreshNowAsync(setup.Token);
    }

    internal static async Task WaitForSubscriptionsAsync(
        SentinelRouter sentinel, int expectedSubscriptions, CancellationToken cancellationToken)
    {
        // The router exposes a synchronized readiness count, not a subscription-ready task.
        while (sentinel.SubscribedSentinelCount < expectedSubscriptions)
            await Task.Delay(5, cancellationToken);
    }
}
