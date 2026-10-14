using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ExceptionEndpointPublicationTests
{
    [Test]
    public async Task CandidateSelectionFailureKeepsUnknownEndpointThroughObservation()
    {
        var endpoint = new RespireEndpoint("last-candidate", 6380);
        var cause = new RespireConnectionException("candidate failed") { Endpoint = endpoint };
        var error = new RespireConnectionException("selection failed", cause) { HasAmbiguousEndpoint = true };
        var owner = Internal.DispatchResponseSource<long>.Start();
        var observation = owner.Observation;
        observation.UseEndpoint(endpoint);
        observation.Handled(error);
        owner.Fail(error);

        await Assert.That(error.Endpoint).IsNull();
        await Assert.That(error.InnerException).IsSameReferenceAs(cause);
        await Assert.That(cause.Endpoint).IsEqualTo((RespireEndpoint?)endpoint);
        await Assert.That(Internal.ConnectionFailures.AtEndpoint(error, endpoint)).IsSameReferenceAs(error);
    }

    [Test]
    public async Task PopulationDoesNotTakeThePublicExceptionLock()
    {
        var error = new RespireConnectionException("shared failure");
        var endpoint = new RespireEndpoint("origin", 6380);
        Task population;
        bool completed;
        lock (error)
        {
            population = Task.Factory.StartNew(() => RespireException.PopulateEndpoint(error, endpoint),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            completed = population.Wait(TimeSpan.FromSeconds(5));
        }

        await population;
        await Assert.That(completed).IsTrue();
        await Assert.That(error.Endpoint).IsEqualTo((RespireEndpoint?)endpoint);
    }

    [Test]
    public async Task ConcurrentPublicationExposesOneCompleteStableEndpoint()
    {
        var first = new RespireEndpoint("first-origin", 6380);
        var second = new RespireEndpoint("second-origin", 6381);
        var errors = Enumerable.Range(0, 4096)
            .Select(_ => new RespireConnectionException("shared failure")).ToArray();
        using var start = new Barrier(3);

        Task Worker(Action work) => Task.Factory.StartNew(() =>
        {
            if (!start.SignalAndWait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Endpoint publication workers did not start.");
            work();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        await Task.WhenAll(
            Worker(() => { foreach (var error in errors) RespireException.PopulateEndpoint(error, first); }),
            Worker(() => { foreach (var error in errors) RespireException.PopulateEndpoint(error, second); }),
            Worker(() =>
            {
                foreach (var error in errors)
                {
                    var observed = error.Endpoint;
                    if (observed is { } endpoint && endpoint != first && endpoint != second)
                        throw new InvalidOperationException("A reader observed a partially published endpoint.");
                    if (observed.HasValue && error.Endpoint != observed)
                        throw new InvalidOperationException("A published origin changed.");
                }
            }));

        foreach (var error in errors)
        {
            var winner = error.Endpoint;
            await Assert.That(winner == first || winner == second).IsTrue();
            RespireException.PopulateEndpoint(error, new("later-retry", 6382));
            await Assert.That(error.Endpoint).IsEqualTo(winner);
        }
    }

    [Test]
    public async Task PopulationPreservesInitializedOriginAndIgnoresUnknownEndpoint()
    {
        var origin = new RespireEndpoint("origin", 6380);
        var initialized = new RespireConnectionException("failure") { Endpoint = origin };
        RespireException.PopulateEndpoint(initialized, new("retry", 6381));
        await Assert.That(initialized.Endpoint).IsEqualTo((RespireEndpoint?)origin);

        var unknown = new RespireConnectionException("failure") { Endpoint = null };
        RespireException.PopulateEndpoint(unknown, null);
        await Assert.That(unknown.Endpoint).IsNull();
        RespireException.PopulateEndpoint(unknown, origin);
        await Assert.That(unknown.Endpoint).IsEqualTo((RespireEndpoint?)origin);
    }
}
