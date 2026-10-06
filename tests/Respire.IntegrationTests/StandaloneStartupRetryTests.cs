using System.Net;
using System.Text.Json;
using Docker.DotNet;
using FluentAssertions;
using Respire.Testing.Containers;
using TUnit.Core;
using static Respire.IntegrationTests.ContainerStartupRetryTests;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
public class StandaloneStartupRetryTests
{
    internal const string BindFailure = "failed to set up container networking: driver failed programming external connectivity on endpoint fixture (abc123): failed to bind host port for 127.0.0.1::172.17.0.8:6379/tcp: address already in use";

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    public async Task SuccessfulStartEndsCollisionRetries(int collisions)
    {
        var probes = new List<ContainerProbe>();
        var initialized = new InvalidOperationException("Reached fixture initialization after successful start.");
        Func<Task> start = async () => await RespireContainerFixture.StartAsync(new(), default, (_, _) =>
        {
            probes.Should().OnlyContain(probe => probe.DisposeCount == 1);
            var container = ContainerProbe.Create(probes.Count < collisions
                ? _ => Task.FromException(ApiError(BindFailure))
                : _ => Task.CompletedTask);
            var probe = (ContainerProbe)container;
            // Stop at initialization so no real Docker port collision can affect the count.
            probe.HostnameError = initialized;
            probes.Add(probe);
            return Task.FromResult(container);
        });
        (await start.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(initialized);
        probes.Should().HaveCount(collisions + 1)
            .And.OnlyContain(probe => probe.StartCount == 1 && probe.DisposeCount == 1);
    }

    [Test]
    public async Task ExhaustionRemovesThreeFreshContainersAndRetainsFailures()
    {
        var probes = new List<ContainerProbe>();
        var failures = new List<Exception>();
        var tokens = new List<CancellationToken>();
        Func<Task> start = async () => await RespireContainerFixture.StartAsync(new(), default, (ports, token) =>
        {
            probes.Should().OnlyContain(probe => probe.DisposeCount == 1);
            ports.Should().Equal(6379); // Container port stays fixed; Docker chooses a new host port.
            tokens.Add(token);
            var failure = ApiError(BindFailure);
            failures.Add(failure);
            var container = ContainerProbe.Create(_ => Task.FromException(failure));
            probes.Add((ContainerProbe)container);
            return Task.FromResult(container);
        });
        var error = (await start.Should().ThrowAsync<DockerApiException>()).Which;
        probes.Should().HaveCount(3).And.OnlyContain(probe => probe.StartCount == 1 && probe.DisposeCount == 1);
        tokens.Distinct().Should().ContainSingle();
        error.Should().BeSameAs(failures[2]);
        error.Data["RespireFixture.PreviousPortCollisions"].Should().BeEquivalentTo(failures.Take(2).ToArray());
        error.Data["RespireFixture.StartupAttempt"].Should().Be(3);
        error.Data["RespireFixture.SelectedPorts"].Should().BeEquivalentTo(new[] { 6379 });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CancellationDuringCleanupPreventsRetry(bool cancelCaller)
    {
        using var caller = new CancellationTokenSource();
        var clock = new CredentialTestClock();
        var timeout = TimeSpan.FromSeconds(10);
        var probes = new List<ContainerProbe>();
        Func<Task> start = async () => await RespireContainerFixture.StartAsync(new() { StartupTimeout = timeout },
            caller.Token, (_, token) =>
            {
                var container = ContainerProbe.Create(_ => Task.FromException(ApiError(BindFailure)));
                var probe = (ContainerProbe)container;
                probes.Add(probe);
                probe.Cleanup = () =>
                {
                    if (cancelCaller) caller.Cancel();
                    else clock.Advance(timeout);
                    token.IsCancellationRequested.Should().BeTrue();
                    return ValueTask.CompletedTask;
                };
                return Task.FromResult(container);
            }, clock);
        if (cancelCaller)
            (await start.Should().ThrowAsync<OperationCanceledException>()).Which.CancellationToken.Should().Be(caller.Token);
        else
            (await start.Should().ThrowAsync<TimeoutException>()).Which.InnerException.Should().BeOfType<DockerApiException>();
        probes.Should().ContainSingle().Which.DisposeCount.Should().Be(1);
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    public async Task CleanupFailureStopsRetriesAndRetainsCauses(int failOnAttempt)
    {
        var failures = new List<Exception>();
        var probes = new List<ContainerProbe>();
        var cleanup = new IOException("controlled cleanup failure");
        Func<Task> start = async () => await RespireContainerFixture.StartAsync(new(), default, (_, _) =>
        {
            var failure = ApiError(BindFailure);
            failures.Add(failure);
            var container = ContainerProbe.Create(_ => Task.FromException(failure));
            var probe = (ContainerProbe)container;
            probes.Add(probe);
            if (probes.Count == failOnAttempt) probe.Cleanup = () => ValueTask.FromException(cleanup);
            return Task.FromResult(container);
        });
        var error = (await start.Should().ThrowAsync<AggregateException>()).Which;
        error.InnerExceptions.Should().Equal(failures.Append(cleanup));
        probes.Should().HaveCount(failOnAttempt).And.OnlyContain(probe => probe.DisposeCount == 1);
    }

    [Test]
    [Arguments("/udp: address already in use")]
    [Arguments("/tcp: permission denied")]
    public async Task OtherBindErrorsNeverRetry(string suffix)
    {
        await AssertNoRetryAsync(ApiError(BindFailure.Replace("/tcp: address already in use", suffix)));
    }

    [Test]
    [Arguments("127.0.0.1::", "0.0.0.0::")]
    [Arguments("127.0.0.1::", "127.0.0.1:6379:")]
    [Arguments("172.17.0.8:6379", "172.17.0.8:6380")]
    [Arguments("172.17.0.8:6379", "not-an-address:6379")]
    [Arguments("failed to set up container networking: ", "")]
    public async Task UnrelatedMappingsAndMessagesNeverRetry(string before, string after)
        => await AssertNoRetryAsync(ApiError(BindFailure.Replace(before, after)));

    [Test]
    public async Task RequiresStructuredDockerServerError()
    {
        foreach (var error in new Exception[]
        {
            new IOException(BindFailure), new AggregateException(ApiError(BindFailure)),
            new DockerApiException(HttpStatusCode.BadRequest, JsonSerializer.Serialize(new { message = BindFailure })),
            new DockerApiException(HttpStatusCode.InternalServerError, BindFailure),
            new DockerApiException(HttpStatusCode.InternalServerError, "{\"message\":42}"),
        }) await AssertNoRetryAsync(error);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MatchingErrorsOutsideContainerStartNeverRetry(bool postStart)
    {
        var expected = ApiError(BindFailure);
        var attempts = 0;
        ContainerProbe? probe = null;
        Func<Task> start = async () => await RespireContainerFixture.StartAsync(new(), default, (_, _) =>
        {
            attempts++;
            if (!postStart) throw expected;
            var container = ContainerProbe.Create(_ => Task.CompletedTask);
            probe = (ContainerProbe)container;
            probe.HostnameError = expected;
            return Task.FromResult(container);
        });
        (await start.Should().ThrowAsync<DockerApiException>()).Which.Should().BeSameAs(expected);
        attempts.Should().Be(1);
        if (postStart) probe!.DisposeCount.Should().Be(1);
    }

    private static async Task AssertNoRetryAsync(Exception expected)
    {
        var probes = new List<ContainerProbe>();
        Func<Task> start = async () => await RespireContainerFixture.StartAsync(new(), default, (_, _) =>
        {
            var container = ContainerProbe.Create(_ => Task.FromException(expected));
            probes.Add((ContainerProbe)container);
            return Task.FromResult(container);
        });
        Exception? actual = null;
        try { await start(); }
        catch (Exception error) { actual = error; }
        actual.Should().BeSameAs(expected);
        probes.Should().ContainSingle().Which.DisposeCount.Should().Be(1);
    }

    internal static DockerApiException ApiError(string message)
        => new(HttpStatusCode.InternalServerError, JsonSerializer.Serialize(new { message }));
}
