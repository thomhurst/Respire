using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.FusionCache.Tests;

public class DistributedLockerPollingTests
{
    [Test]
    [Arguments(50, 0, 45)]
    [Arguments(50, 0.5, 50)]
    [Arguments(50, 1, 55)]
    [Arguments(1, 0, 1)]
    [Arguments(1, 0.5, 1)]
    [Arguments(1, 1, 1.1)]
    [Arguments(1000, 0, 900)]
    [Arguments(1000, 1, 1100)]
    public async Task JitterSpreadsRetriesAndPreservesMinimumDelay(double intervalMs, double sample, double expectedMs)
    {
        var delay = RespireFusionCacheDistributedLocker.GetPollDelay(TimeSpan.FromMilliseconds(intervalMs), sample);
        await Assert.That(delay).IsEqualTo(TimeSpan.FromMilliseconds(expectedMs));
    }
}
