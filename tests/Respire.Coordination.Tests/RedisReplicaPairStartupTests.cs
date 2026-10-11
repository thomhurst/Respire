using System.Net;
using System.Reflection;
using System.Text.Json;
using Docker.DotNet;
using DotNet.Testcontainers.Containers;

namespace Respire.Coordination.Tests;

public class RedisReplicaPairStartupTests
{
    private const string Collision = "failed to set up container networking: driver failed programming external connectivity on endpoint fixture (abc123): failed to listen on TCP socket: address already in use";

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    public async Task CollisionReplacesContainerAfterCleanup(int collisions)
    {
        var probes = new List<ContainerProbe>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var container = await RedisReplicaPair.StartContainerAsync(() =>
        {
            foreach (var previous in probes)
                if (previous.DisposeCount != 1) throw new InvalidOperationException("Replacement preceded cleanup.");
            var next = ContainerProbe.Create(probes.Count < collisions ? Error(Collision) : null);
            probes.Add((ContainerProbe)next);
            return next;
        }, deadline.Token);
        await Assert.That(probes.Count).IsEqualTo(collisions + 1);
        foreach (var probe in probes)
        {
            await Assert.That(probe.StartCount).IsEqualTo(1);
            await Assert.That(probe.Token).IsEqualTo(deadline.Token);
        }
        await Assert.That(probes[^1].DisposeCount).IsEqualTo(0);
        await container.DisposeAsync();
        await Assert.That(probes[^1].DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task ExhaustionRetainsAllThreeCollisions()
    {
        var probes = new List<ContainerProbe>();
        var failures = new List<Exception>();
        var error = await Assert.That(async () => await RedisReplicaPair.StartContainerAsync(() =>
        {
            var failure = Error(Collision);
            failures.Add(failure);
            var container = ContainerProbe.Create(failure);
            probes.Add((ContainerProbe)container);
            return container;
        }, default)).Throws<DockerApiException>();
        await Assert.That(probes.Count).IsEqualTo(3);
        foreach (var probe in probes) await Assert.That(probe.DisposeCount).IsEqualTo(1);
        await Assert.That(ReferenceEquals(error, failures[2])).IsTrue();
        var previous = (Exception[])error!.Data["RedisReplicaPair.PreviousPortCollisions"]!;
        await Assert.That(previous.SequenceEqual(failures.Take(2))).IsTrue();
    }

    [Test]
    [Arguments("failed to listen on TCP socket: address already in use")]
    [Arguments("failed to set up container networking: driver failed programming external connectivity on endpoint fixture (abc123): failed to listen on UDP socket: address already in use")]
    [Arguments("failed to set up container networking: driver failed programming external connectivity on endpoint fixture (abc123): failed to listen on TCP socket: permission denied")]
    public async Task OtherErrorsNeverRetry(string message)
    {
        var probes = new List<ContainerProbe>();
        var failure = Error(message);
        var error = await Assert.That(async () => await RedisReplicaPair.StartContainerAsync(() =>
        {
            var container = ContainerProbe.Create(failure);
            probes.Add((ContainerProbe)container);
            return container;
        }, default)).Throws<DockerApiException>();
        await Assert.That(ReferenceEquals(error, failure)).IsTrue();
        await Assert.That(probes.Count).IsEqualTo(1);
        await Assert.That(probes[0].DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task CancellationDuringCleanupPreventsReplacement()
    {
        using var deadline = new CancellationTokenSource();
        var attempts = 0;
        ContainerProbe? probe = null;
        await Assert.That(async () => await RedisReplicaPair.StartContainerAsync(() =>
        {
            attempts++;
            var container = ContainerProbe.Create(Error(Collision));
            probe = (ContainerProbe)container;
            probe.Cleanup = () => { deadline.Cancel(); return ValueTask.CompletedTask; };
            return container;
        }, deadline.Token)).Throws<OperationCanceledException>();
        await Assert.That(attempts).IsEqualTo(1);
        await Assert.That(probe!.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task CleanupFailureStopsRetriesAndRetainsBothCauses()
    {
        var collision = Error(Collision);
        var cleanup = new IOException("controlled cleanup failure");
        var attempts = 0;
        var error = await Assert.That(async () => await RedisReplicaPair.StartContainerAsync(() =>
        {
            attempts++;
            var container = ContainerProbe.Create(collision);
            ((ContainerProbe)container).Cleanup = () => ValueTask.FromException(cleanup);
            return container;
        }, default)).Throws<AggregateException>();
        await Assert.That(attempts).IsEqualTo(1);
        await Assert.That(error!.InnerExceptions.SequenceEqual(new Exception[] { collision, cleanup })).IsTrue();
    }

    [Test]
    public async Task ContainerCreationFailureNeverRetries()
    {
        var failure = Error(Collision);
        var attempts = 0;
        var error = await Assert.That(async () => await RedisReplicaPair.StartContainerAsync(() =>
        {
            attempts++;
            throw failure;
        }, default)).Throws<DockerApiException>();
        await Assert.That(ReferenceEquals(error, failure)).IsTrue();
        await Assert.That(attempts).IsEqualTo(1);
    }

    [Test]
    public async Task CancelledStartupNeverCreatesContainer()
    {
        using var deadline = new CancellationTokenSource();
        deadline.Cancel();
        var attempts = 0;
        await Assert.That(async () => await RedisReplicaPair.StartContainerAsync(() =>
        {
            attempts++;
            return ContainerProbe.Create(null);
        }, deadline.Token)).Throws<OperationCanceledException>();
        await Assert.That(attempts).IsEqualTo(0);
    }

    private static DockerApiException Error(string message)
        => new(HttpStatusCode.InternalServerError, JsonSerializer.Serialize(new { message }));

    public class ContainerProbe : DispatchProxy
    {
        private Exception? _failure;
        public Func<ValueTask>? Cleanup { get; set; }
        public int StartCount { get; private set; }
        public int DisposeCount { get; private set; }
        public CancellationToken Token { get; private set; }

        internal static IContainer Create(Exception? failure)
        {
            var container = Create<IContainer, ContainerProbe>();
            ((ContainerProbe)container)._failure = failure;
            return container;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "StartAsync":
                    StartCount++;
                    Token = (CancellationToken)args![0]!;
                    return _failure is null ? Task.CompletedTask : Task.FromException(_failure);
                case "DisposeAsync":
                    DisposeCount++;
                    return Cleanup?.Invoke() ?? ValueTask.CompletedTask;
                default: throw new NotSupportedException(method.Name);
            }
        }
    }
}
