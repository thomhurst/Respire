using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Caching.Hybrid.Tests;

public class RespireHybridCacheCoherenceOptionsTests
{
    [Test]
    [Arguments(1L)]
    [Arguments(5_000L)]
    [Arguments(TimeSpan.TicksPerMillisecond - 1)]
    public async Task ObservationSweepIntervalBelowOneMillisecondIsRejected(long ticks)
    {
        IHybridCacheBuilder builder = new ServiceCollection().AddRespireHybridCache("redis://localhost");

        await Assert.That(() => builder.WithRespireClientSideCoherence(options =>
            options.ObservationSweepInterval = TimeSpan.FromTicks(ticks))).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments(TimeSpan.TicksPerMillisecond)]
    [Arguments(TimeSpan.TicksPerMillisecond + 1)]
    public async Task ObservationSweepIntervalAtOrJustAboveMinimumIsAccepted(long ticks)
    {
        IHybridCacheBuilder builder = new ServiceCollection().AddRespireHybridCache("redis://localhost");

        IHybridCacheBuilder configuredBuilder = builder.WithRespireClientSideCoherence(options =>
            options.ObservationSweepInterval = TimeSpan.FromTicks(ticks));

        await Assert.That(configuredBuilder).IsSameReferenceAs(builder);
    }
}
