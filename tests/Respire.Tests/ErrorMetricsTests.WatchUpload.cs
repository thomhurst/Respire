using System.Text;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public partial class ErrorMetricsTests
{
    [Test]
    [MatrixDataSource]
    public async Task WatchUploadPreflightHasOneFinalOwner(
        [Matrix(false, true)] bool prefix, [Matrix(false, true)] bool crossSlot,
        [Matrix(false, true)] bool enabled)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = enabled ? RespireMetricGroups.Resiliency : RespireMetricGroups.None });
        await using var server = new FakeRespServer(8, FakeRespServer.OkReply);
        server.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{server.Port}\r\n")
            : null;
        await using var root = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        var client = prefix ? root.WithKeyPrefix("tenant:") : root;
        RespireKey[] keys = crossSlot ? ["{first}:one", "{second}:two"] : ["{same}:one", "{same}:two"];
        using var capture = new Capture(throwOnMeasurement: true);
        async Task Execute() { await using var transaction = await client.CreateTransactionAsync(keys); }
        if (crossSlot)
        {
            await Assert.That(ClusterHash.GetSlot("{first}:one")).IsNotEqualTo(ClusterHash.GetSlot("{second}:two"));
            await Assert.That(Execute).Throws<InvalidOperationException>();
        }
        else await Execute();
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(enabled && crossSlot ? 1 : 0);
        if (enabled && crossSlot)
        {
            await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsFalse();
            await Assert.That(items[0].Tags["error.type"]).IsEqualTo(typeof(InvalidOperationException).FullName);
            await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        }
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("WATCH ")))
            .IsEqualTo(crossSlot ? 0 : 1);
    }

    [Test]
    [MatrixDataSource]
    public async Task WatchUploadDiscardedStreamReplyUsesCopiedAttempts(
        [Matrix(false, true)] bool asking, [Matrix(0, 1, 3)] int attempts)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (!command.StartsWith("SET ", StringComparison.Ordinal)) return false;
                received.TrySetResult();
                return true;
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        using var capture = new Capture(throwOnMeasurement: true, onMeasurement: item =>
        {
            if (Equals(item.Tags.GetValueOrDefault("db.response.status_code"), "NOPERM")) recorded.TrySetResult();
        });
        var observation = RespireTelemetry.ErrorObservation.Rent(force: true);
        observation.SetAttempts(attempts);
        var connection = client.Core.Multiplexer.GetConnection();
        using var payload = new MemoryStream("payload"u8.ToArray());
        var command = new StreamedSetCommand((RespireValue)"key", payload, payload.Length, default, SetWhen.Always);
        var response = asking
            ? ClusterRouter.SendAskingAsync(connection, in command, cancellation.Token, "SET", observation: observation)
            : connection.SendCheckedAsync(in command, cancellation.Token, commandName: "SET", observation: observation);
        var pending = RespireTelemetry.ObserveFinalError(response, observation).AsTask();
        await received.Task.WaitAsync(deadline.Token);
        cancellation.Cancel();
        var error = await Assert.That(async () => { using var reply = await pending.WaitAsync(deadline.Token); })
            .Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        // A different logical owner must not alter the metadata on the canceled reply.
        using var unrelated = RespireTelemetry.ErrorObservation.Rent(force: true);
        unrelated.SetAttempts(7);
        await server.SendRawAsync("-NOPERM private-key\r\n"u8.ToArray());
        await recorded.Task.WaitAsync(deadline.Token);
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(2);
        await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(attempts);
        await Assert.That((bool)items[1].Tags["redis.client.errors.internal"]!).IsTrue();
        await Assert.That(items[1].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(attempts);
        await Assert.That(server.ReceivedCommands.Contains("ASKING")).IsEqualTo(asking);
    }

#if DEBUG
    [Test]
    [Arguments("handled")]
    [Arguments("final")]
    [Arguments("attempts")]
    public async Task WatchUploadReturnedOwnerRejectsCallsInDebug(string operation)
    {
        var observation = RespireTelemetry.ErrorObservation.Rent(force: true);
        observation.Dispose();
        Action call = operation switch
        {
            "handled" => () => observation.Handled(new InvalidOperationException()),
            "final" => () => observation.Final(new InvalidOperationException()),
            _ => () => observation.SetAttempts(1),
        };
        await Assert.That(call).Throws<InvalidOperationException>();
    }
#endif
}
