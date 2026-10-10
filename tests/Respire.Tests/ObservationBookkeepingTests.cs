using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

[NotInParallel]
public class ObservationBookkeepingTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RetryCountsSaturateAcrossObservationOwners(bool legacy)
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        using var owner = new Owner(legacy);
        owner.SetAttempts(int.MaxValue - 1);
        owner.Handled(new IOException());
        owner.Handled(new IOException());
        owner.Retry();
        owner.Final(new IOException());
        await Assert.That(capture.Items.Select(item => item.Attempts).ToArray())
            .IsEquivalentTo(new[] { int.MaxValue - 1, int.MaxValue, int.MaxValue });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FinalPublicationClosesAllRetryUpdates(bool legacy)
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        using var owner = new Owner(legacy);
        owner.SetAttempts(3);
        owner.Final(new IOException());
        owner.Handled(new IOException());
        owner.Retry();
        owner.SetAttempts(99);
        owner.Final(new IOException());
        await Assert.That(owner.Attempts).IsEqualTo(3);
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That(capture.Items.Single().Internal).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConcurrentCopiedRetriesRetainCountsAndIndependentFinalFailures(bool legacy)
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        using var first = new Owner(legacy);
        using var second = new Owner(legacy);
        var error = new IOException("shared failure");
        Parallel.For(0, 32, _ => first.Handled(error));
        first.Final(error);
        second.Final(error);
        await Assert.That(capture.Items.Where(item => item.Internal).Select(item => item.Attempts).Order().ToArray())
            .IsEquivalentTo(Enumerable.Range(0, 32).ToArray());
        await Assert.That(capture.Items.Where(item => !item.Internal).Select(item => item.Attempts).ToArray())
            .IsEquivalentTo(new[] { 32, 0 });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FinalPublicationRacingRetriesCapturesOnlyAcceptedAttempts(bool legacy)
    {
        using var configuration = new MetricConfigurationScope();
        using var capture = new Capture();
        using var owner = new Owner(legacy);
        var error = new IOException();
        var retries = Enumerable.Range(0, 32).Select(_ => Task.Run(() => owner.Handled(error))).ToArray();
        var final = Task.Run(() => owner.Final(error));
        await Task.WhenAll(retries.Append(final));
        await Assert.That(capture.Items.Single(item => !item.Internal).Attempts)
            .IsEqualTo(capture.Items.Count(item => item.Internal));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FinalExporterCannotReopenRetryBookkeeping(bool legacy)
    {
        using var configuration = new MetricConfigurationScope();
        using var owner = new Owner(legacy);
        using var capture = new Capture(internallyHandled =>
        {
            if (internallyHandled) return;
            owner.Handled(new IOException("late logical retry"));
            owner.Retry();
            owner.SetAttempts(99);
            throw new InvalidOperationException("Exporter failure");
        });
        owner.SetAttempts(2);
        owner.Final(new IOException("original failure"));
        await Assert.That(owner.Attempts).IsEqualTo(2);
        await Assert.That(capture.Items.Count).IsEqualTo(1);
    }

    private sealed class Owner : IDisposable
    {
        private readonly bool _legacy;
        private readonly ErrorObservation.FinalOwner _owner;
        private readonly RespireTelemetry.ErrorObservation _observation;

        internal Owner(bool legacy)
        {
            _legacy = legacy;
            if (legacy) _observation = RespireTelemetry.ErrorObservation.Rent(force: true);
            else _owner = ErrorObservation.StartFailure();
        }

        internal int Attempts => _legacy ? _observation.Attempts : _owner.RetryAttempts;
        internal void SetAttempts(int attempts)
        {
            if (_legacy) _observation.SetAttempts(attempts);
            else _owner.SetRetryAttempts(attempts);
        }
        internal void Handled(Exception error)
        {
            if (_legacy) _observation.Handled(error);
            else
            {
                var borrower = _owner.Borrow();
                var nested = borrower.Borrow();
                try { nested.RecordHandled(error); }
                finally { nested.Complete(); borrower.Complete(); }
            }
        }
        internal void Retry()
        {
            if (_legacy) _observation.Retry();
            else _owner.RecordRetry();
        }
        internal void Final(Exception error)
        {
            if (_legacy) _observation.Final(error);
            else _owner.PublishFinal(error);
        }
        public void Dispose()
        {
            if (_legacy) _observation.Dispose();
            else _owner.Complete();
        }
    }

    private sealed class Capture : IDisposable
    {
        private readonly MeterListener _listener = new();
        internal readonly ConcurrentQueue<(bool Internal, int Attempts)> Items = new();

        internal Capture(Action<bool>? onMeasurement = null)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, RespireTelemetry.Meter) && instrument.Name == "redis.client.errors")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                var internallyHandled = false;
                var attempts = 0;
                foreach (var tag in tags)
                {
                    if (tag.Key == "redis.client.errors.internal") internallyHandled = (bool)tag.Value!;
                    if (tag.Key == "redis.client.operation.retry_attempts") attempts = (int)tag.Value!;
                }
                Items.Enqueue((internallyHandled, attempts));
                onMeasurement?.Invoke(internallyHandled);
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }
}
