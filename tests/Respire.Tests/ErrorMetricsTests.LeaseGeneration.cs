using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public partial class ErrorMetricsTests
{
    [Test]
    [Arguments("handled")]
    [Arguments("final")]
    [Arguments("attempts")]
    [Arguments("dispose")]
    [Arguments("complete")]
    public async Task LeaseGenerationRejectsReturnedAliasesAfterStorageReuse(string operation)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture(throwOnMeasurement: true);
        var held = new List<RespireTelemetry.ErrorObservation>();
        try
        {
            // Leave exactly one returned object available, proving reuse rather than merely
            // checking a stale alias while the next caller happens to own different storage.
            for (var i = 0; i < 4096; i++) held.Add(RespireTelemetry.ErrorObservation.Rent(force: true));
            var returned = held[^1];
            held.RemoveAt(held.Count - 1);
            returned.Dispose();
            var active = RespireTelemetry.ErrorObservation.Rent(force: true);
            held.Add(active);
            await Assert.That(ReferenceEquals(ObservationStorage(returned), ObservationStorage(active))).IsTrue();
            active.SetAttempts(7);
            Action staleCall = operation switch
            {
                "handled" => () => returned.Handled(new IOException("stale borrower")),
                "final" => () => returned.Final(new RespireServerException("ERR stale borrower")),
                "attempts" => () => returned.SetAttempts(99),
                "complete" => () => returned.DisposeAndGetAttempts(),
                _ => returned.Dispose,
            };
#if DEBUG
            if (operation is not ("dispose" or "complete")) await Assert.That(staleCall).ThrowsExactly<InvalidOperationException>();
            else staleCall();
#else
            staleCall();
#endif
            await Assert.That(active.Attempts).IsEqualTo(7);
            var unrelated = RespireTelemetry.ErrorObservation.Rent(force: true);
            held.Add(unrelated);
            unrelated.SetAttempts(23);
            await Assert.That(active.Attempts).IsEqualTo(7);
            active.Final(new RespireServerException("WRONGTYPE active caller"));
            var item = capture.Items.Single();
            await Assert.That((bool)item.Tags["redis.client.errors.internal"]!).IsFalse();
            await Assert.That(item.Tags["db.response.status_code"]).IsEqualTo("WRONGTYPE");
            await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(7);
            unrelated.Final(new RespireServerException("NOPERM independent caller"));
            await Assert.That(capture.Items.Count).IsEqualTo(2);
            await Assert.That(capture.Items.Last().Tags["redis.client.operation.retry_attempts"]).IsEqualTo(23);
        }
        finally { foreach (var lease in held) lease.Dispose(); }
    }

    [Test]
    public async Task LeaseGenerationCopiesShareOneActiveBoundary()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture();
        using var owner = RespireTelemetry.ErrorObservation.Rent(force: true);
        var borrower = owner;
        borrower.SetAttempts(4);
        owner.Final(new RespireServerException("WRONGTYPE original caller"));
        borrower.Final(new RespireServerException("ERR repeated inspection"));
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That(capture.Items.Single().Tags["redis.client.operation.retry_attempts"]).IsEqualTo(4);
    }

    private static object ObservationStorage(RespireTelemetry.ErrorObservation observation)
        => observation.InspectForTests().StorageIdentity!;

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DeferredOwnerCapturesAttemptsBeforeReturningStorage(bool enabled)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = enabled ? RespireMetricGroups.Resiliency : RespireMetricGroups.None });
        using var capture = new Capture();
        var owner = RespireTelemetry.ErrorObservation.Rent(force: true);
        var borrower = owner;
        borrower.Handled(new IOException("handled retry"));
        borrower.Handled(new IOException("second retry"));
        await Assert.That(owner.DisposeAndGetAttempts()).IsEqualTo(2);
        await Assert.That(owner.Attempts).IsEqualTo(0);
        await Assert.That(borrower.DisposeAndGetAttempts()).IsEqualTo(0);
        await Assert.That(capture.Items.Count).IsEqualTo(enabled ? 2 : 0);
        using var next = RespireTelemetry.ErrorObservation.Rent(force: true);
        await Assert.That(next.Attempts).IsEqualTo(0);
        next.SetAttempts(7);
        owner.Dispose();
        await Assert.That(next.Attempts).IsEqualTo(7);
    }
}
