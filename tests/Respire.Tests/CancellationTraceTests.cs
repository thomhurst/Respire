using System.Collections.Concurrent;
using System.Diagnostics;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

[NotInParallel]
public class CancellationTraceTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    [Test]
    public Task CallerCancellationTagsSpanWithAwaitingReplyAndKeepsToken()
        => AssertCancelledSpanAsync(retryPolicy: null);

    [Test]
    public Task CallerCancellationTagsSpanWhenRetryPolicyWrapsTheCancellation()
        => AssertCancelledSpanAsync(new RespireRetryPolicy { InitialBackoff = TimeSpan.Zero });

    private static async Task AssertCancelledSpanAsync(RespireRetryPolicy? retryPolicy)
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "Respire",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using var client = await RespireClient.ConnectAsync(
            server.CreateOptions() with { Connections = 1, CommandTimeout = TimeSpan.FromSeconds(30), RetryPolicy = retryPolicy });
#pragma warning restore RESP010
        var gate = new RespireFakeGate();
        using var fault = server.InjectFault("PING", RespireFakeFault.Pause(gate, afterExecution: true), occurrences: null);
        try
        {
            using var caller = new CancellationTokenSource();
            var pending = client.PingAsync(caller.Token).AsTask();
            await fault.Matched.WaitAsync(Limit);
            // The server can execute PING before the flush loop records the sent bytes.
            await WaitForWriteFlushAsync(client.Core.Multiplexer.GetConnection());
            await caller.CancelAsync();

            var error = await Assert.That(async () => await pending.WaitAsync(Limit))
                .ThrowsExactly<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);

            var span = stopped.Single(activity => activity.DisplayName == "PING");
            await Assert.That(span.Status).IsEqualTo(ActivityStatusCode.Error);
            await Assert.That(span.GetTagItem("respire.command.stage")).IsEqualTo("AwaitingReply");
            await Assert.That(span.GetTagItem("respire.connection.id")).IsNotNull();
            await Assert.That(span.GetTagItem("respire.connection.pending_write_bytes")).IsEqualTo(0L);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task WaitForWriteFlushAsync(Respire.Networking.RespireConnection connection)
    {
        using var timeout = new CancellationTokenSource(Limit);
        while (connection.CaptureTimeoutDiagnostics().PendingWriteBytes != 0)
            await Task.Delay(1, timeout.Token);
    }
}
