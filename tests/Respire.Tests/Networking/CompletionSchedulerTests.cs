using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class CompletionSchedulerTests
{
    [Test]
    public async Task ParsedReplyWinsOverCancellationAndAnAlreadyCapturedDeadlineSweep()
    {
        var source = new PendingResponsePool(1).Rent();
        var pending = source.Task.AsTask();
        var observedState = source.State;
        var scheduler = new CompletionScheduler();
        scheduler.Add(source, RespValue.Integer(42));
        // Hold the parsed reply in the scheduler, exactly as a busy earlier continuation does.
        await Assert.That(pending.IsCompleted).IsFalse();
        await Assert.That(source.TrySetCanceled(new CancellationToken(true))).IsFalse();
        RespireTimeoutDiagnostics? diagnostics = null;
        await Assert.That(source.TrySetTimedOut(observedState, TimeSpan.FromSeconds(1), ref diagnostics, null)).IsFalse();
        scheduler.Flush();
        using var reply = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(reply.AsInteger()).IsEqualTo(42);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MultiReplyReservesOnlyAfterItsFinalReplyAndPreservesPrefixErrors(bool cancelBeforeFinal)
    {
        var source = MultiReplyPendingResponseSource.Rent(2, 0, "MULTI/EXEC");
        var pending = source.Task.AsTask();
        var scheduler = new CompletionScheduler();
        scheduler.Add(source, RespValue.Error("ERR queue rejected"u8.ToArray()));
        if (cancelBeforeFinal) await Assert.That(source.TrySetCanceled(new CancellationToken(true))).IsTrue();
        scheduler.Add(source, RespValue.Integer(42));
        if (!cancelBeforeFinal) await Assert.That(source.TrySetCanceled(new CancellationToken(true))).IsFalse();
        scheduler.Flush();
        if (cancelBeforeFinal)
            await Assert.That(async () => await pending).Throws<OperationCanceledException>();
        else
        {
            using var reply = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(reply.IsError).IsTrue();
            await Assert.That(System.Text.Encoding.UTF8.GetString(reply.AsSpan())).IsEqualTo("ERR queue rejected");
        }
    }

    [Test]
    public async Task ParsedStreamingErrorPreservesTheAskingFailure()
    {
        var source = new BulkStreamPendingResponseSource("GET", true, null);
        var pending = source.Task.AsTask();
        var scheduler = new CompletionScheduler();
        var prefix = RespValue.Error("ERR asking rejected"u8.ToArray());
        source.ObservePrefix(prefix);
        scheduler.Add(source, prefix);
        scheduler.Add(source, RespValue.Error("ERR get rejected"u8.ToArray()));
        await Assert.That(source.TrySetCanceled(new CancellationToken(true))).IsFalse();
        scheduler.Flush();
        var error = await Assert.That(async () => await pending).Throws<RespireServerException>();
        await Assert.That(error!.Message).Contains("asking rejected");
    }
}
