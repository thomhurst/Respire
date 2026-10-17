using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class CacheStampedeTests
{
    [Test]
    [Arguments(100L, 10d, 0d, 0.0001d, false)]
    [Arguments(100L, 10d, 1d, 0.5d, false)]
    [Arguments(100L, 10d, 1d, 0.00001d, true)]
    [Arguments(100L, 10d, 2d, 0.001d, true)]
    [Arguments(1L, 10d, 1d, 0d, true)]
    public async Task XFetchElectionUsesDurationBetaAndLogSample(long remaining, double duration, double beta,
        double sample, bool expected)
        => await Assert.That(RespireClient.ShouldRefreshCacheAside(remaining, duration, beta, sample)).IsEqualTo(expected);

    [Test]
    public async Task LockIdentityIsDeterministicBinarySafeAndPreservesClusterSlots()
    {
        for (var index = 0; index < 16_384; index++)
        {
            RespireKey key = new byte[] { 0, 255, (byte)(index >> 8), (byte)index, (byte)'{', (byte)'}' };
            var first = RespireClient.CreateCacheAsideLockKey(key);
            await Assert.That(first.ClusterSlot).IsEqualTo(key.ClusterSlot);
            await Assert.That(first.ToBytes().SequenceEqual(RespireClient.CreateCacheAsideLockKey(key).ToBytes())).IsTrue();
        }
        await Assert.That(RespireClient.CreateCacheAsideLockKey("{same}:one").ToBytes()
            .SequenceEqual(RespireClient.CreateCacheAsideLockKey("{same}:two").ToBytes())).IsFalse();
    }

    [Test]
    public async Task InvalidOptionsAreRejected()
    {
        RespireCacheAsideOptions[] invalid =
        [
            new() { EarlyRefreshBeta = -1 }, new() { EarlyRefreshBeta = double.NaN },
            new() { EarlyRefreshBeta = double.PositiveInfinity }, new() { LockTimeout = TimeSpan.Zero },
            new() { WaitTimeout = TimeSpan.FromMilliseconds(-1) }, new() { RetryDelay = TimeSpan.Zero },
            new() { LockTimeout = TimeSpan.FromDays(100) }, new() { StaleWhileRevalidate = TimeSpan.FromTicks(-1) },
            new() { DistributedSingleFlight = false, StaleWhileRevalidate = TimeSpan.FromSeconds(1) },
        ];
        foreach (var options in invalid)
            await Assert.That(() => options.Validate()).Throws<ArgumentException>();
        new RespireCacheAsideOptions { WaitTimeout = TimeSpan.Zero }.Validate();
    }
}
