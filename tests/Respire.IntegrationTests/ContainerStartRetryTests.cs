using DotNet.Testcontainers.Containers;
using FluentAssertions;
using TUnit.Core;
using static Respire.IntegrationTests.ContainerStartupRetryTests;
using static Respire.IntegrationTests.StandaloneStartupRetryTests;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
public class ContainerStartRetryTests
{
    private const string ListenFailure = "failed to set up container networking: driver failed programming external connectivity on endpoint sharp_colden (2db25cff5020): failed to listen on TCP socket: address already in use";

    [Test]
    [Arguments(0, ListenFailure)]
    [Arguments(1, ListenFailure)]
    [Arguments(2, ListenFailure)]
    [Arguments(2, StandaloneStartupRetryTests.BindFailure)]
    public async Task CollisionsStartFreshContainersUntilOneStarts(int collisions, string message)
    {
        var probes = new List<ContainerProbe>();
        var started = await ContainerStartRetry.StartAsync(() =>
        {
            // A failed container is removed before its replacement is built.
            probes.Should().OnlyContain(probe => probe.DisposeCount == 1);
            var collide = probes.Count < collisions;
            var container = ContainerProbe.Create(_ => collide ? Task.FromException(ApiError(message)) : Task.CompletedTask);
            probes.Add((ContainerProbe)container);
            return container;
        }, [6379]);
        probes.Should().HaveCount(collisions + 1).And.OnlyContain(probe => probe.StartCount == 1);
        started.Should().BeSameAs(probes[^1]);
        probes[^1].DisposeCount.Should().Be(0);
        probes.Take(collisions).Should().OnlyContain(probe => probe.DisposeCount == 1);
    }

    [Test]
    public async Task ExhaustionRethrowsTheThirdCollisionWithThePreviousOnes()
    {
        var probes = new List<ContainerProbe>();
        var failures = new List<Exception>();
        Func<Task> start = () => ContainerStartRetry.StartAsync(() =>
        {
            var failure = ApiError(ListenFailure);
            failures.Add(failure);
            var container = ContainerProbe.Create(_ => Task.FromException(failure));
            probes.Add((ContainerProbe)container);
            return container;
        }, [6379]);
        var error = (await start.Should().ThrowAsync<Docker.DotNet.DockerApiException>()).Which;
        error.Should().BeSameAs(failures[2]);
        error.Data[ContainerStartRetry.PreviousPortCollisionsKey].Should().BeEquivalentTo(failures.Take(2).ToArray());
        probes.Should().HaveCount(3).And.OnlyContain(probe => probe.StartCount == 1 && probe.DisposeCount == 1);
    }

    [Test]
    [Arguments("failed to set up container networking: driver failed programming external connectivity on endpoint fixture (abc123): failed to listen on UDP socket: address already in use", 6379)]
    [Arguments("failed to set up container networking: driver failed programming external connectivity on endpoint fixture (abc123): failed to listen on TCP socket: permission denied", 6379)]
    [Arguments("image pull failed (abc123): failed to listen on TCP socket: address already in use", 6379)]
    [Arguments("failed to set up container networking: driver failed programming external connectivity on endpoint fixture (abc123): failed to bind host port for 127.0.0.1::172.17.0.8:6379/tcp: address already in use", 7000)]
    public async Task OtherFailuresAreRemovedAndNeverRetried(string message, int containerPort)
        => await AssertNoRetryAsync(ApiError(message), containerPort);

    [Test]
    public async Task NonDockerFailuresAreRemovedAndNeverRetried()
        => await AssertNoRetryAsync(new IOException(ListenFailure), 6379);

    [Test]
    public async Task CancellationDuringCleanupPreventsRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var probes = new List<ContainerProbe>();
        Func<Task> start = () => ContainerStartRetry.StartAsync(() =>
        {
            var container = ContainerProbe.Create(_ => Task.FromException(ApiError(ListenFailure)));
            var probe = (ContainerProbe)container;
            probe.Cleanup = () =>
            {
                cancellation.Cancel();
                return ValueTask.CompletedTask;
            };
            probes.Add(probe);
            return container;
        }, [6379], cancellation.Token);
        (await start.Should().ThrowAsync<OperationCanceledException>()).Which.CancellationToken.Should().Be(cancellation.Token);
        probes.Should().ContainSingle().Which.DisposeCount.Should().Be(1);
    }

    [Test]
    public async Task CleanupFailureStopsRetriesAndRetainsCauses()
    {
        var failures = new List<Exception>();
        var cleanup = new IOException("controlled cleanup failure");
        Func<Task> start = () => ContainerStartRetry.StartAsync(() =>
        {
            var failure = ApiError(ListenFailure);
            failures.Add(failure);
            var container = ContainerProbe.Create(_ => Task.FromException(failure));
            if (failures.Count == 2) ((ContainerProbe)container).Cleanup = () => ValueTask.FromException(cleanup);
            return container;
        }, [6379]);
        var error = (await start.Should().ThrowAsync<AggregateException>()).Which;
        error.InnerExceptions.Should().Equal(failures.Append(cleanup));
    }

    private static async Task AssertNoRetryAsync(Exception expected, int containerPort)
    {
        var probes = new List<ContainerProbe>();
        Func<Task> start = () => ContainerStartRetry.StartAsync(() =>
        {
            var container = ContainerProbe.Create(_ => Task.FromException(expected));
            probes.Add((ContainerProbe)container);
            return container;
        }, [containerPort]);
        Exception? actual = null;
        try { await start(); }
        catch (Exception error) { actual = error; }
        actual.Should().BeSameAs(expected);
        probes.Should().ContainSingle().Which.DisposeCount.Should().Be(1);
    }
}
