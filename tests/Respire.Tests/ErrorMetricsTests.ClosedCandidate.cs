using System.Reflection;
using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public partial class ErrorMetricsTests
{
    [Test]
    [Arguments(RespProtocol.Resp2)]
    [Arguments(RespProtocol.Resp3)]
    public async Task HandshakeAfterReceiveLoopClosesRetainsOnePhysicalObservation(RespProtocol protocol)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture(throwOnMeasurement: true);
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new() { Protocol = RespProtocol.Resp2 });
        var failedPending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.PendingCommandsFailing += () => failedPending.TrySetResult();
        await server.ConnectionAccepted.WaitAsync(TimeSpan.FromSeconds(5));
        server.CloseConnection(0);
        await failedPending.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(capture.Items.Count).IsEqualTo(1);

        // Invoke the production handshake after the receive-loop barrier, without adding a test-only API.
        var handshake = typeof(RespireConnection).GetMethod("HandshakeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<RespireConnectionOptions, CancellationToken, bool, Task>>(connection);
        var error = await Assert.That(() => handshake(new()
        {
            Protocol = protocol, Password = "private-password", ClientName = "private-name",
        }, CancellationToken.None, false)).Throws<RespireConnectionClosedBeforeSendException>();
        using var observation = RespireTelemetry.ErrorObservation.Rent(force: true);
        ReadEndpointRouter.RecordCandidateFailure(observation, error!);

        await Assert.That(RespireTelemetry.IsObservedConnectionError(error!)).IsTrue();
        await Assert.That(observation.Attempts).IsEqualTo(1);
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That((bool)capture.Items.Single().Tags["redis.client.errors.internal"]!).IsTrue();
        await Assert.That(capture.Items.Single().Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        await Assert.That(server.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    public async Task ClosedCandidateRetainsPhysicalFailureObservation()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture(throwOnMeasurement: true);
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new() { Protocol = RespProtocol.Resp2 });
        var failedPending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.PendingCommandsFailing += () => failedPending.TrySetResult();
        await server.ConnectionAccepted.WaitAsync(TimeSpan.FromSeconds(5));
        server.CloseConnection(0);
        await failedPending.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var observation = RespireTelemetry.ErrorObservation.Rent(force: true);
        var error = await Assert.That(async () =>
        {
            using var reply = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame));
        }).Throws<RespireConnectionClosedBeforeSendException>();
        ReadEndpointRouter.RecordCandidateFailure(observation, error!);

        await Assert.That(RespireTelemetry.IsObservedConnectionError(error!)).IsTrue();
        await Assert.That(observation.Attempts).IsEqualTo(1);
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That((bool)capture.Items.Single().Tags["redis.client.errors.internal"]!).IsTrue();

        // An unrelated candidate failure still needs its own internal event.
        ReadEndpointRouter.RecordCandidateFailure(observation, new RespireConnectionException("unobserved candidate"));
        await Assert.That(observation.Attempts).IsEqualTo(2);
        await Assert.That(capture.Items.Count).IsEqualTo(2);
        await Assert.That(capture.Items.Last().Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
    }
}
