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
