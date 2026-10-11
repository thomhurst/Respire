using System.Reflection;
using Respire.Internal;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public partial class ErrorMetricsTests
{
    [Test]
    public async Task ClusterScriptTelemetryFallbackSaturatesRetryHistory()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var client = RespireClient.Create(new RespireOptions { Endpoints = [new("127.0.0.1", 1)] });
        using var capture = new Capture(throwOnMeasurement: true);
        // Ordinary routes borrow their caller observation. Cover the legacy telemetry-only
        // fallback separately, including counts too large to reach with actual network retries.
        var type = typeof(RespireClient).GetNestedType("ClusterScriptTelemetry", BindingFlags.NonPublic)!;
        var telemetry = Activator.CreateInstance(type, client.Core, RespireScript.Create("return 1"),
            default(RespireTelemetry.OperationStart))!;
        var attempts = type.GetField("_errorAttempts", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var record = type.GetMethod("RecordRetry", BindingFlags.Instance | BindingFlags.NonPublic)!;
        attempts.SetValue(telemetry, int.MaxValue - 1);
        var error = new RespireServerException("NOSCRIPT missing");
        for (var index = 0; index < 3; index++) record.Invoke(telemetry, [error]);
        await Assert.That((int)attempts.GetValue(telemetry)!).IsEqualTo(int.MaxValue);
        await Assert.That(capture.Items.Select(item => (int)item.Tags["redis.client.operation.retry_attempts"]!).ToArray())
            .IsEquivalentTo(new[] { int.MaxValue - 1, int.MaxValue, int.MaxValue });
        await Assert.That(capture.Items.All(item => (bool)item.Tags["redis.client.errors.internal"]!)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ProcedureRecoveryPublishesAfterMutationCleanup(bool function)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        const string source = "#!lua name=sample\nreturn 1";
        var replyPending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var loaded = false;
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) =>
            {
                if (command.StartsWith("HELLO ", StringComparison.Ordinal)) return "%1\r\n+proto\r\n:3\r\n"u8.ToArray();
                if (command.StartsWith("EVALSHA ", StringComparison.Ordinal)) return "-NOSCRIPT missing\r\n"u8.ToArray();
                if (command.StartsWith("FCALL ", StringComparison.Ordinal)) return "-ERR Function not found\r\n"u8.ToArray();
                if (command.StartsWith("FUNCTION LIST ", StringComparison.Ordinal))
                    return loaded ? FunctionLibraryMetadata(source) : "*0\r\n"u8.ToArray();
                if (command.StartsWith("FUNCTION LOAD ", StringComparison.Ordinal))
                {
                    loaded = true;
                    return "$6\r\nsample\r\n"u8.ToArray();
                }
                return null;
            },
            SuppressReply = command =>
            {
                if (!command.StartsWith("EVAL ", StringComparison.Ordinal)
                    && !(command.StartsWith("FCALL ", StringComparison.Ordinal) && ++calls == 2)) return false;
                replyPending.TrySetResult();
                return true;
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp3, ClientSideCache = new(),
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        var cache = client.Core.ClientCache!;
        var mutationsAtFinal = -1;
        using var capture = new Capture(throwOnMeasurement: true, onMeasurement: item =>
        {
            if (!(bool)item.Tags["redis.client.errors.internal"]!)
                mutationsAtFinal = cache.InspectForTests().ActiveMutationCount;
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = function
            ? client.Functions.ExecuteAsync<int>(RespireFunctionLibrary.Create(source).Function("function"),
                cancellationToken: deadline.Token).AsTask()
            : client.Scripts.ExecuteAsync<int>(RespireScript.Create("return 1"), cancellationToken: deadline.Token).AsTask();
        await replyPending.Task.WaitAsync(deadline.Token);
        await Assert.That(cache.InspectForTests().ActiveMutationCount).IsEqualTo(1);
        await Assert.That(pending.IsCompleted).IsFalse();
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That((bool)capture.Items.Single().Tags["redis.client.errors.internal"]!).IsTrue();
        await server.SendRawAsync("$3\r\nbad\r\n"u8.ToArray());
        var error = await Assert.That(async () => await pending.WaitAsync(deadline.Token)).Throws<Exception>();
        var final = capture.Items.Single(item => !(bool)item.Tags["redis.client.errors.internal"]!);
        await Assert.That(final.Tags["error.type"]).IsEqualTo(error!.GetType().FullName);
        await Assert.That(final.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
        await Assert.That(mutationsAtFinal).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FunctionReloadStateKeepsCallerRetryHistoriesIndependent(bool bothFail)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        const string source = "#!lua name=sample\nreturn 1";
        var calls = 0;
        var loaded = false;
        await using var server = new FakeRespServer
        {
            ReplyOverride = (_, command) =>
            {
                if (command.StartsWith("FCALL ", StringComparison.Ordinal))
                {
                    if (Interlocked.Increment(ref calls) <= 2) return "-ERR Function not found\r\n"u8.ToArray();
                    return bothFail || command.EndsWith(" first", StringComparison.Ordinal)
                        ? "$3\r\nbad\r\n"u8.ToArray() : ":42\r\n"u8.ToArray();
                }
                if (command.StartsWith("FUNCTION LIST ", StringComparison.Ordinal))
                    return loaded ? FunctionLibraryMetadata(source) : "*0\r\n"u8.ToArray();
                if (command.StartsWith("FUNCTION LOAD ", StringComparison.Ordinal))
                {
                    loaded = true;
                    return "-ERR Library 'sample' already exists\r\n"u8.ToArray();
                }
                return null;
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var library = RespireFunctionLibrary.Create(source);
        var gate = library.ReloadState(client.Core).Gate;
        using var capture = new Capture(throwOnMeasurement: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await gate.WaitAsync(deadline.Token);
        Task<int> first;
        Task<int> second;
        try
        {
            first = client.Functions.ExecuteAsync<int>(library.Function("function"), args: ["first"],
                cancellationToken: deadline.Token).AsTask();
            second = client.Functions.ExecuteAsync<int>(library.Function("function"), args: ["second"],
                cancellationToken: deadline.Token).AsTask();
            // Both calls must capture the old generation before either reload enters the gate.
            while (capture.Items.Count < 2) await Task.Delay(1, deadline.Token);
            await Assert.That(capture.Items.All(item => (bool)item.Tags["redis.client.errors.internal"]!)).IsTrue();
            await Assert.That(first.IsCompleted || second.IsCompleted).IsFalse();
        }
        finally { gate.Release(); }

        await Assert.That(async () => await first.WaitAsync(deadline.Token)).Throws<Exception>();
        if (bothFail) await Assert.That(async () => await second.WaitAsync(deadline.Token)).Throws<Exception>();
        else await Assert.That(await second.WaitAsync(deadline.Token)).IsEqualTo(42);
        var finals = capture.Items.Where(item => !(bool)item.Tags["redis.client.errors.internal"]!).ToArray();
        await Assert.That(finals.Length).IsEqualTo(bothFail ? 2 : 1);
        var counts = finals.Select(item => (int)item.Tags["redis.client.operation.retry_attempts"]!).Order().ToArray();
        // Exactly one caller observes the LOAD collision; the other only observes its own FCALL miss.
        if (bothFail) await Assert.That(counts).IsEquivalentTo(new[] { 1, 2 });
        else await Assert.That(counts.Single() is 1 or 2).IsTrue();
        await Assert.That(capture.Items.Count(item => (bool)item.Tags["redis.client.errors.internal"]!)).IsEqualTo(3);
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("FUNCTION LOAD ", StringComparison.Ordinal)))
            .IsEqualTo(1);
        await Assert.That(gate.CurrentCount).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SharedScriptCorrectionKeepsProducerAndCallerFinalOwnersIndependent(bool correctionFails)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        var correctionWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer
        {
            SuppressReply = command =>
            {
                if (command != "SCRIPT FLUSH") return false;
                correctionWritten.TrySetResult();
                return true;
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture(throwOnMeasurement: true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var original = new OperationCanceledException(cancellation.Token);
        // Share the owned Task result, never the producer's pooled response source or observation.
        var producer = client.Scripts.FlushAsync().AsTask();
        using var firstObservation = RespireTelemetry.ErrorObservation.Rent(force: true);
        using var secondObservation = RespireTelemetry.ErrorObservation.Rent(force: true);
        firstObservation.SetAttempts(2);
        secondObservation.SetAttempts(3);
        Task<bool> Join(RespireTelemetry.ErrorObservation observation)
            => client.ExecuteWithCorrectionAsync(new ProcedureCorrectionExecution(original, observation),
                RespireClient.CorrectionOrdering.OrderedCorrection, producer,
                static async (correction, _) => await correction.ConfigureAwait(false)).AsTask();
        var first = Join(firstObservation);
        var second = Join(secondObservation);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await correctionWritten.Task.WaitAsync(deadline.Token);
        await Assert.That(first.IsCompleted || second.IsCompleted || producer.IsCompleted).IsFalse();
        await Assert.That(capture.Items).IsEmpty();
        await server.SendRawAsync(correctionFails ? "-NOPERM correction rejected\r\n"u8.ToArray() : FakeRespServer.OkReply);
        Exception expected = original;
        if (correctionFails)
            expected = (await Assert.That(async () => await producer.WaitAsync(deadline.Token)).Throws<RespireServerException>())!;
        else await producer.WaitAsync(deadline.Token);
        var firstError = await Assert.That(async () => await first.WaitAsync(deadline.Token)).Throws<Exception>();
        var secondError = await Assert.That(async () => await second.WaitAsync(deadline.Token)).Throws<Exception>();
        await Assert.That(ReferenceEquals(firstError, expected) && ReferenceEquals(secondError, expected)).IsTrue();
        if (!correctionFails)
            await Assert.That(((OperationCanceledException)firstError!).CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(capture.Items.All(item => !(bool)item.Tags["redis.client.errors.internal"]!)).IsTrue();
        var counts = capture.Items.Select(item => (int)item.Tags["redis.client.operation.retry_attempts"]!).Order().ToArray();
        await Assert.That(counts).IsEquivalentTo(correctionFails ? new[] { 0, 2, 3 } : new[] { 2, 3 });
    }

    private sealed class ProcedureCorrectionExecution(Exception error, RespireTelemetry.ErrorObservation observation)
        : RespireClient.ITrackedCorrectionExecution<bool>
    {
        public ValueTask<bool> Response => ValueTask.FromException<bool>(error);
        public RespireClient.TrackedConnectionIdentity ConnectionIdentity => default;
        public bool CommandMayBeOutstanding => true;
        public RespireTelemetry.ErrorObservation ErrorObservation => observation;
    }
}
