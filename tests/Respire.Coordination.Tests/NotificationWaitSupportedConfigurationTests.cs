using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Coordination.Tests;

[ClassDataSource<SharedRedis74>(Shared = SharedType.PerTestSession)]
public class NotificationWaitSupportedConfigurationTests(SharedRedis74 fixture)
{
    [Test]
    [MatrixDataSource]
    public async Task RootAndPrefixedClientsAcquireWithCoveredPhysicalKeys(
        [Matrix("fair", "fenced", "hash")] string operation,
        [Matrix(false, true)] bool prefixedView,
        [Matrix(RespireClientTrackingMode.OptIn, RespireClientTrackingMode.OptOut,
            RespireClientTrackingMode.Broadcast)] RespireClientTrackingMode trackingMode)
    {
        var prefix = SharedRespireContainer.Prefix("wait");
        await using var client = await RespireClient.ConnectAsync(fixture.CreateOptions() with
        {
            Connections = 1,
            Protocol = RespireProtocol.Resp3,
            ClientSideCache = new()
            {
                TrackingMode = trackingMode,
                KeyPrefixes = [prefix + "lease", prefix + "queue"],
            },
        });
        var coordination = new RespireCoordination(prefixedView ? client.WithKeyPrefix(prefix) : client);
        var keyPrefix = prefixedView ? "" : prefix;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (operation == "hash")
        {
            await using var lease = await coordination.AcquireLeaseAsync(
                keyPrefix + "lease", "worker", TimeSpan.FromMinutes(1), timeout.Token);
            await Assert.That(await lease.VerifyStillHeldAsync()).IsTrue();
        }
        else
        {
            await using var lease = operation == "fair"
                ? await coordination.CreateFairLock(keyPrefix + "lease", keyPrefix + "counter", keyPrefix + "queue")
                    .AcquireAsync(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5), timeout.Token)
                : await coordination.AcquireFencedLockAsync(
                    keyPrefix + "lease", keyPrefix + "counter", TimeSpan.FromMinutes(1), timeout.Token);
            await Assert.That(await lease.VerifyStillHeldAsync()).IsTrue();
        }
    }
}
