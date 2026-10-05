using System.Net;
using System.Reflection;
using System.Text.Json;
using Docker.DotNet;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Respire.Testing.Containers;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
public class ContainerStartupRetryTests
{
    [Test]
    [Arguments("driver failed programming external connectivity: Bind for 127.0.0.1:32791 failed: port is already allocated", true)]
    [Arguments("failed to bind host port for 127.0.0.1:32791:172.17.0.14:32791/tcp: address already in use", true)]
    [Arguments("Ports are not available: exposing port TCP 127.0.0.1:32791 -> 0.0.0.0:0: listen tcp 127.0.0.1:32791: bind: address already in use", true)]
    [Arguments("Ports are not available: exposing port TCP 127.0.0.1:32791 -> 0.0.0.0:0: listen tcp4 127.0.0.1:32791: bind: Only one usage of each socket address (protocol/network address/port) is normally permitted.", true)]
    [Arguments("Bind for 127.0.0.1:327910 failed: port is already allocated", false)]
    [Arguments("Bind for 0.0.0.0:32791 failed: port is already allocated", false)]
    [Arguments("failed to bind host port for 127.0.0.1:32791:172.17.0.14:32791/udp: address already in use", false)]
    [Arguments("Ports are not available: listen tcp 127.0.0.1:32791: bind: permission denied", false)]
    [Arguments("Ports are not available: listen tcp 127.0.0.1:32791: bind: An attempt was made to access a socket in a way forbidden by its access permissions.", false)]
    [Arguments("image pull failed: address already in use", false)]
    [Arguments("port is already allocated", false)]
    [Arguments("failed to set up container networking: driver failed programming external connectivity on endpoint fixture (abc123): failed to listen on TCP socket: address already in use", true)]
    [Arguments("failed to set up container networking: driver failed programming external connectivity on endpoint fixture (abc123): failed to listen on UDP socket: address already in use", false)]
    [Arguments("failed to set up container networking: driver failed programming external connectivity on endpoint fixture (abc123): failed to listen on TCP socket: permission denied", false)]
    [Arguments("failed to listen on TCP socket: address already in use", false)]
    public void RecognizesOnlyKnownPortBindingFailures(string message, bool expected)
        => ContainerPortCollision.IsMatch(ApiError(message), [32791]).Should().Be(expected);

    [Test]
    public void RequiresTypedServerErrorAndStructuredMessage()
    {
        const string message = "Bind for 127.0.0.1:32791 failed: port is already allocated";
        foreach (var error in new Exception[]
        {
            new IOException(message), new AggregateException(ApiError(message)),
            new DockerApiException(HttpStatusCode.BadRequest, JsonSerializer.Serialize(new { message })),
            new DockerApiException(HttpStatusCode.InternalServerError, message),
            new DockerApiException(HttpStatusCode.InternalServerError, "{\"message\":42}"),
            new DockerApiException(HttpStatusCode.InternalServerError, "[]"),
            new DockerApiException(HttpStatusCode.InternalServerError, null),
        }) ContainerPortCollision.IsMatch(error, [32791]).Should().BeFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExhaustionUsesThreeFreshContainersAndRetainsAllFailures(bool addressOmitted)
    {
        var probes = new List<ContainerProbe>();
        var portsUsed = new HashSet<int>();
        var failures = new List<Exception>();
        var tokens = new List<CancellationToken>();
        Func<Task> start = async () => await RespireContainerFixture.StartAsync(new() { Topology = RespireContainerTopology.Cluster },
            default, (ports, token) =>
            {
                probes.Should().OnlyContain(probe => probe.DisposeCount == 1);
                ports.Should().OnlyContain(port => !portsUsed.Contains(port));
                portsUsed.UnionWith(ports);
                tokens.Add(token);
                var failure = addressOmitted
                    ? ApiError("failed to set up container networking: driver failed programming external connectivity on endpoint fixture (abc123): failed to listen on TCP socket: address already in use")
                    : Collision(ports[0]);
                failures.Add(failure);
                var container = ContainerProbe.Create(_ => Task.FromException(failure));
                probes.Add((ContainerProbe)container);
                return Task.FromResult(container);
            });
        var error = (await start.Should().ThrowAsync<DockerApiException>()).Which;
        probes.Should().HaveCount(3).And.OnlyContain(probe => probe.StartCount == 1 && probe.DisposeCount == 1);
        tokens.Distinct().Should().ContainSingle();
        portsUsed.Should().HaveCount(9);
        error.Should().BeSameAs(failures[2]);
        error.Data["RespireFixture.PreviousPortCollisions"].Should().BeEquivalentTo(failures.Take(2).ToArray());
        error.Data["RespireFixture.StartupAttempt"].Should().Be(3);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task UnrelatedErrorsAndStandaloneNeverRetry(int kind)
    {
        var probes = new List<ContainerProbe>();
        Exception? expected = null;
        var options = new RespireContainerOptions
        {
            Topology = kind == 3 ? RespireContainerTopology.Standalone : RespireContainerTopology.Cluster,
        };
        Func<Task> start = async () => await RespireContainerFixture.StartAsync(options, default, (ports, _) =>
        {
            expected = kind switch
            {
                0 => ApiError("image authorization failed"),
                1 => new IOException("address already in use"),
                _ => Collision(ports[0]),
            };
            var container = ContainerProbe.Create(_ => kind == 2 ? Task.CompletedTask : Task.FromException(expected));
            var probe = (ContainerProbe)container;
            // Even a matching error from post-start inspection must not trigger a retry.
            if (kind == 2) probe.HostnameError = expected;
            probes.Add(probe);
            return Task.FromResult(container);
        });
        var error = (await start.Should().ThrowAsync<Exception>()).Which;
        error.Should().BeSameAs(expected);
        probes.Should().ContainSingle().Which.DisposeCount.Should().Be(1);
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    public async Task CleanupFailureStopsRetriesAndPreservesEveryCause(int failCleanupOnAttempt)
    {
        var failures = new List<Exception>();
        var probes = new List<ContainerProbe>();
        var cleanup = new IOException("controlled cleanup failure");
        Func<Task> start = async () => await RespireContainerFixture.StartAsync(new() { Topology = RespireContainerTopology.Cluster },
            default, (ports, _) =>
            {
                var failure = Collision(ports[0]);
                failures.Add(failure);
                var container = ContainerProbe.Create(_ => Task.FromException(failure));
                var probe = (ContainerProbe)container;
                probes.Add(probe);
                if (probes.Count == failCleanupOnAttempt) probe.Cleanup = () => ValueTask.FromException(cleanup);
                return Task.FromResult(container);
            });
        var error = (await start.Should().ThrowAsync<AggregateException>()).Which;
        error.InnerExceptions.Should().Equal(failures.Append(cleanup));
        probes.Should().HaveCount(failCleanupOnAttempt).And.OnlyContain(probe => probe.DisposeCount == 1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CancellationDuringCleanupPreventsAnotherAttempt(bool callerCancellation)
    {
        using var caller = new CancellationTokenSource();
        var probes = new List<ContainerProbe>();
        var tokens = new List<CancellationToken>();
        var options = new RespireContainerOptions
        {
            Topology = RespireContainerTopology.Cluster,
            StartupTimeout = callerCancellation ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(100),
        };
        Func<Task> start = async () => await RespireContainerFixture.StartAsync(options, caller.Token, (ports, token) =>
        {
            tokens.Add(token);
            var container = ContainerProbe.Create(_ => Task.FromException(Collision(ports[0])));
            var probe = (ContainerProbe)container;
            probes.Add(probe);
            if (probes.Count == 2)
                probe.Cleanup = async () =>
                {
                    if (callerCancellation) caller.Cancel();
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                };
            return Task.FromResult(container);
        });
        if (callerCancellation)
            (await start.Should().ThrowAsync<OperationCanceledException>()).Which.CancellationToken.Should().Be(caller.Token);
        else
            (await start.Should().ThrowAsync<TimeoutException>()).Which.InnerException.Should().NotBeNull();
        // The overall deadline may expire before reaching the second attempt on a busy
        // runner. Only explicit caller cancellation is gated on that exact boundary.
        if (callerCancellation) probes.Should().HaveCount(2);
        else probes.Count.Should().BeLessThanOrEqualTo(2);
        probes.Should().OnlyContain(probe => probe.DisposeCount == 1);
        tokens.Distinct().Count().Should().BeLessThanOrEqualTo(1);
    }

    private static DockerApiException Collision(int port)
        => ApiError($"Bind for 127.0.0.1:{port} failed: port is already allocated");

    private static DockerApiException ApiError(string message)
        => new(HttpStatusCode.InternalServerError, JsonSerializer.Serialize(new { message }));

    public class ContainerProbe : DispatchProxy
    {
        private Func<CancellationToken, Task> _start = null!;
        public Func<ValueTask>? Cleanup { get; set; }
        public Func<IList<string>, CancellationToken, Task<ExecResult>>? Execute { get; set; }
        public Exception? HostnameError { get; set; }
        public int StartCount { get; private set; }
        public int DisposeCount { get; private set; }

        internal static IContainer Create(Func<CancellationToken, Task> start)
        {
            var container = DispatchProxy.Create<IContainer, ContainerProbe>();
            ((ContainerProbe)container)._start = start;
            return container;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "StartAsync": StartCount++; return _start((CancellationToken)args![0]!);
                case "DisposeAsync": DisposeCount++; return Cleanup?.Invoke() ?? ValueTask.CompletedTask;
                case "ExecAsync": return Execute?.Invoke((IList<string>)args![0]!, (CancellationToken)args[1]!)
                    ?? Task.FromException<ExecResult>(new InvalidOperationException("Daemon diagnostics unavailable."));
                case "get_Id": return "controlled-container";
                case "get_Hostname": throw HostnameError ?? new InvalidOperationException("Unexpected successful start in failure test.");
                default: throw new NotSupportedException(method.Name);
            }
        }
    }
}
