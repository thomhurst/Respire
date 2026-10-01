using System.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Extensions.Coordination.Tests;

public class SemaphoreTimingTests
{
    [Test]
    public async Task ExpiryIsTruncatedToWholeMilliseconds()
    {
        await Assert.That(RespireSemaphore.ToMilliseconds(TimeSpan.FromTicks(19_999), "expiry")).IsEqualTo(1L);
        await Assert.That(RespireSemaphore.ToMilliseconds(TimeSpan.FromMilliseconds(1500.9), "expiry")).IsEqualTo(1500L);
        await Assert.That(RespireSemaphore.ToMilliseconds(null, "expiry")).IsEqualTo(0L);
        await Assert.That(RespireSemaphore.FromMilliseconds(0)).IsNull();
        await Assert.That(RespireSemaphore.FromMilliseconds(1500)).IsEqualTo(TimeSpan.FromMilliseconds(1500));
    }

    [Test]
    [Arguments(0L)]
    [Arguments(5_000L)]
    [Arguments(9_999L)]
    [Arguments(-10_000L)]
    public async Task ExpiryBelowOneMillisecondIsRejected(long ticks)
        => await Assert.That(() => RespireSemaphore.ToMilliseconds(TimeSpan.FromTicks(ticks), "expiry"))
            .Throws<ArgumentOutOfRangeException>();

    [Test]
    public async Task LocalEstimateSaturatesBelowTheNoExpiryValue()
    {
        var now = Stopwatch.GetTimestamp();
        await Assert.That(RespireSemaphorePermit.AddTimestampDuration(now, TimeSpan.FromSeconds(1)))
            .IsEqualTo(now + Stopwatch.Frequency);
        // long.MaxValue means "no expiry", so a finite permit must stay below it.
        await Assert.That(RespireSemaphorePermit.AddTimestampDuration(now, TimeSpan.MaxValue)).IsEqualTo(long.MaxValue - 1);
        await Assert.That(RespireSemaphorePermit.AddTimestampDuration(long.MaxValue - 1, TimeSpan.FromMilliseconds(1)))
            .IsEqualTo(long.MaxValue - 1);
    }

    [Test]
    public async Task CleanupRetryStopsOnSuccess()
    {
        var attempts = 0;
        var succeeded = await RespireSemaphore.RetryCleanupAsync(Stopwatch.GetTimestamp(), () =>
            new(++attempts < 3 ? SemaphoreCleanupAttempt.Failed : SemaphoreCleanupAttempt.Succeeded));

        await Assert.That(succeeded).IsTrue();
        await Assert.That(attempts).IsEqualTo(3);
    }

    [Test]
    public async Task CleanupRetryStopsWhenTheClientIsDisposed()
    {
        var attempts = 0;
        var succeeded = await RespireSemaphore.RetryCleanupAsync(Stopwatch.GetTimestamp(), () =>
        {
            attempts++;
            return new(SemaphoreCleanupAttempt.Abandoned);
        });

        await Assert.That(succeeded).IsFalse();
        await Assert.That(attempts).IsEqualTo(1);
    }

    [Test]
    public async Task CleanupRetryStopsWhenCleanupIsNoLongerNeeded()
    {
        var attempts = 0;
        var needed = true;
        var succeeded = await RespireSemaphore.RetryCleanupAsync(Stopwatch.GetTimestamp(), () =>
        {
            attempts++;
            needed = false;
            return new(SemaphoreCleanupAttempt.Failed);
        }, () => needed);

        await Assert.That(succeeded).IsFalse();
        await Assert.That(attempts).IsEqualTo(1);
    }

    [Test]
    public async Task CleanupRetryStopsAfterTheRetryWindow()
    {
        var attempts = 0;
        var windowStart = Stopwatch.GetTimestamp()
            - (long)(RespireSemaphore.CleanupRetryLimit.TotalSeconds * Stopwatch.Frequency);
        var succeeded = await RespireSemaphore.RetryCleanupAsync(windowStart, () =>
        {
            attempts++;
            return new(SemaphoreCleanupAttempt.Failed);
        });

        await Assert.That(succeeded).IsFalse();
        await Assert.That(attempts).IsEqualTo(1);
    }
}
