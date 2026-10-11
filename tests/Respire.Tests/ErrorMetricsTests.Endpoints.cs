using System.Text;
using Respire.Internal;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public partial class ErrorMetricsTests
{
    [Test]
    public async Task CircuitRejectionCarriesTheRejectedEndpointBeforeTransportDispatch()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer();
        var endpoint = new RespireEndpoint("127.0.0.1", server.Port);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Endpoints = [endpoint],
            CircuitBreaker = new() { MinimumFailureCount = 1, OpenDuration = TimeSpan.FromMinutes(1) },
        });
        var admission = client.Core.Circuits!.Acquire(endpoint, default);
        try { admission.Failed(new RespireConnectionException("Injected endpoint failure."), default); }
        finally { admission.Dispose(); }
        using var capture = new Capture(serverPort: server.Port);
        await Assert.That(async () => await client.Strings.GetStringAsync("key")).Throws<RespireCircuitOpenException>();
        var item = capture.Items.Single();
        await Assert.That(item.Tags["server.address"]).IsEqualTo(endpoint.Host);
        await Assert.That(item.Tags["server.port"]).IsEqualTo(endpoint.Port);
        await Assert.That(server.ReceivedCommands.Contains("GET key")).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CircuitRejectionOverridesAnEarlierSelectedEndpoint(bool observed)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture();
        // Nearest can retain the primary's endpoint and then pick a replica whose circuit is open.
        var primary = new RespireEndpoint("primary.example", 6380);
        var rejected = new RespireCircuitOpenException(new RespireEndpoint("replica.example", 6381), retryAfter: null);
        if (observed)
        {
            using var observation = RespireTelemetry.ErrorObservation.Rent(force: true);
            observation.UseEndpoint(primary);
            observation.Final(rejected);
        }
        else RespireTelemetry.RecordError(rejected, internallyHandled: false, endpoint: primary);
        var item = capture.Items.Single();
        await Assert.That(item.Tags["server.address"]).IsEqualTo("replica.example");
        await Assert.That(item.Tags["server.port"]).IsEqualTo(6381);
    }

    [Test]
    public async Task SentinelPrimaryConnectFailureNamesTheResolvedPrimary()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        // Sentinel resolves a primary that refuses connections.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var sentinel = new FakeRespServer(16)
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR-BY-NAME ", StringComparison.Ordinal)
                ? Encoding.ASCII.GetBytes($"*2\r\n$9\r\n127.0.0.1\r\n${port.ToString().Length}\r\n{port}\r\n")
                : "*0\r\n"u8.ToArray(),
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, ThreadPoolMonitoring = false,
            Endpoints = [new("127.0.0.1", sentinel.Port)], SentinelPrimaryName = "primary",
            ConnectTimeout = TimeSpan.FromSeconds(2),
        });
        using var capture = new Capture();
        await Assert.That(async () => await client.Strings.GetStringAsync("key")).Throws<Exception>();
        var final = capture.Items.Single(item => !(bool)item.Tags["redis.client.errors.internal"]!);
        await Assert.That(final.Tags["server.address"]).IsEqualTo("127.0.0.1");
        await Assert.That(final.Tags["server.port"]).IsEqualTo(port);
    }

    [Test]
    [Arguments("function")]
    [Arguments("script")]
    public async Task ParallelFanOutFinalErrorNamesTheFailingLeg(string route)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var first = new FakeRespServer(16, FakeRespServer.OkReply);
        await using var second = new FakeRespServer(16, FakeRespServer.OkReply);
        var topology = Encoding.ASCII.GetBytes(
            $"*2\r\n*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{first.Port}\r\n" +
            $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{second.Port}\r\n");
        await using var seed = new FakeRespServer(16, topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, UseCluster = true,
            ClusterTopologyRefreshInterval = null, Endpoints = [new("127.0.0.1", seed.Port)],
        });
        var connections = await client.Core.Cluster!.GetMasterConnectionsAsync(default, discovery: null, observation: default);
        // Task.WhenAll surfaces the first leg's failure. The healthy leg answers later, so it
        // is the last to touch the shared observation and must not own the final error.
        var failingPort = connections[0].Port;
        var failing = failingPort == first.Port ? first : second;
        var healthy = ReferenceEquals(failing, first) ? second : first;
        var operation = route == "function" ? "FUNCTION FLUSH" : "SCRIPT FLUSH";
        failing.ReplyOverride = (_, command) => command.StartsWith(operation, StringComparison.Ordinal)
            ? "-NOPERM failing leg\r\n"u8.ToArray() : null;
        healthy.DelayCommand(operation, 300);
        using var capture = new Capture();
        await Assert.That(async () =>
        {
            if (route == "function") await client.Functions.FlushAsync();
            else await client.Scripts.FlushAsync();
        }).Throws<RespireServerException>();
        var final = capture.Items.Single(item => !(bool)item.Tags["redis.client.errors.internal"]!);
        await Assert.That(final.Tags["server.port"]).IsEqualTo(failingPort);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ColdClusterSeedFailureNamesTheSeed(bool clusterWide)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        // Reserve then release a port so the seed refuses connections.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, Connections = 1, ThreadPoolMonitoring = false,
            ClusterTopologyRefreshInterval = null, ConnectTimeout = TimeSpan.FromSeconds(2),
            Endpoints = [new("127.0.0.1", port)],
        });
        using var capture = new Capture();
        await Assert.That(async () =>
        {
            // SCRIPT FLUSH discovers every master before it sends anything.
            if (clusterWide) using (await client.ExecuteAsync("SCRIPT", ["FLUSH"])) { }
            else await client.Strings.GetStringAsync("key");
        }).Throws<Exception>();
        var final = capture.Items.Single(item => !(bool)item.Tags["redis.client.errors.internal"]!);
        await Assert.That(final.Tags["server.address"]).IsEqualTo("127.0.0.1");
        await Assert.That(final.Tags["server.port"]).IsEqualTo(port);
    }

    [Test]
    [MatrixDataSource]
    public async Task CommandFailuresCarryTheirOwnServerPort(
        [Matrix("string", "bytes", "raw")] string surface, [Matrix(false, true)] bool commandMetrics)
    {
        using var configuration = new MetricConfigurationScope(new()
        {
            Groups = RespireMetricGroups.Resiliency | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None),
        });
        await using var server = new FakeRespServer
        {
            ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal)
                ? "-WRONGTYPE private-key\r\n"u8.ToArray() : null,
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Endpoints = [new("127.0.0.1", server.Port)],
        });
        using var capture = new Capture(serverPort: server.Port);
        // A listener for one client must ignore another node's measurement.
        RespireTelemetry.RecordError(new IOException(), false, endpoint: new("unrelated.example", -1));
        await Assert.That(async () =>
        {
            switch (surface)
            {
                case "string": await client.Strings.GetStringAsync("key"); break;
                case "bytes": await client.Strings.GetBytesAsync("key"); break;
                default: using (await client.ExecuteAsync("GET", ["key"])) break;
            }
        }).Throws<RespireServerException>();
        var item = capture.Items.Single();
        await Assert.That(item.Tags["server.address"]).IsEqualTo("127.0.0.1");
        await Assert.That(item.Tags["server.port"]).IsEqualTo(server.Port);
        await Assert.That(item.Tags["redis.client.errors.internal"]).IsEqualTo(false);
    }

    [Test]
    [MatrixDataSource]
    public async Task ErrorOwnersRetainLastAttemptedEndpoint([Matrix("pooled", "dispatch", "deferred")] string ownerKind)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture();
        var dispatch = ownerKind == "dispatch" ? DispatchResponseSource<int>.Start() : null;
        var pending = ownerKind == "deferred" ? new RespirePending<int>() : null;
        var observation = dispatch?.Observation ?? pending?.Observation ?? RespireTelemetry.ErrorObservation.Rent(force: true);
        try
        {
            observation.UseEndpoint(new("first.example", 6380));
            observation.Handled(new RespireServerException("MOVED 42 second.example:6381"));
            observation.UseEndpoint(new("second.example", 6381));
            var failure = new IOException("connection lost");
            if (dispatch is not null) dispatch.Fail(failure);
            else if (pending is not null) { pending.Fail(failure); pending.ReportError(); }
            else observation.Final(failure);
            var items = capture.Items.ToArray();
            await Assert.That(items.Length).IsEqualTo(2);
            await Assert.That(items[0].Tags["server.address"]).IsEqualTo("first.example");
            await Assert.That(items[0].Tags["server.port"]).IsEqualTo(6380);
            await Assert.That(items[1].Tags["server.address"]).IsEqualTo("second.example");
            await Assert.That(items[1].Tags["server.port"]).IsEqualTo(6381);
            await Assert.That(items[1].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
        }
        finally { observation.Dispose(); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReturnedOwnersDoNotLeakEndpointToUnroutedFailures(bool dispatchOwner)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture();
        if (dispatchOwner)
        {
            var first = DispatchResponseSource<int>.Start();
            first.Observation.UseEndpoint(new("old.example", 6380));
            first.CompleteInternal();
            DispatchResponseSource<int>.Start().Fail(new OperationCanceledException());
        }
        else
        {
            var first = RespireTelemetry.ErrorObservation.Rent(force: true);
            first.UseEndpoint(new("old.example", 6380));
            first.Dispose();
            using var next = RespireTelemetry.ErrorObservation.Rent(force: true);
            next.Final(new OperationCanceledException());
        }
        var item = capture.Items.Single();
        await Assert.That(item.Tags.ContainsKey("server.address")).IsFalse();
        await Assert.That(item.Tags.ContainsKey("server.port")).IsFalse();
    }

    [Test]
    [MatrixDataSource]
    public async Task ParallelRetryBorrowersKeepTheirOwnEndpoint(
        [Matrix("pooled", "dispatch", "deferred")] string ownerKind)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture();
        var dispatch = ownerKind == "dispatch" ? DispatchResponseSource<int>.Start() : null;
        var pending = ownerKind == "deferred" ? new RespirePending<int>() : null;
        var observation = dispatch?.Observation ?? pending?.Observation ?? RespireTelemetry.ErrorObservation.Rent(force: true);
        try
        {
            observation.UseEndpoint(new("last.example", 6399));
            Parallel.For(0, 64, index => observation.Handled(
                index % 2 == 0 ? new IOException() : new RespireServerException("WRONGTYPE private-key"),
                index % 2 == 0 ? new("network.example", 6380) : new("server.example", 6381)));
            await Assert.That(capture.Items.Count).IsEqualTo(64);
            foreach (var item in capture.Items)
                await Assert.That(item.Tags["server.port"]).IsEqualTo(
                    Equals(item.Tags["redis.client.errors.category"], "network") ? 6380 : 6381);
            // An older borrower's late report must not replace the last selected endpoint.
            await Assert.That(observation.Endpoint).IsEqualTo(new RespireEndpoint("last.example", 6399));
        }
        finally
        {
            if (dispatch is not null) dispatch.CompleteInternal();
            else if (pending is not null) { pending.Fail(new IOException()); pending.ReportError(); }
            observation.Dispose();
        }
    }
}
