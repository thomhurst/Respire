using System.Runtime.CompilerServices;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class CorrectionDispatchTests
{
    [Test, NotInParallel]
    public async Task CompletedDispatchDoesNotAllocateIdentityClosures()
    {
#pragma warning disable RESP010 // Test-owned client supplies the core for the synchronous correction-dispatch allocation probe.
        await using var client = RespireClient.Create(new RespireOptions { Endpoints = [new("127.0.0.1", 6379)] });
#pragma warning restore RESP010
        var execution = new Execution { Response = ValueTask.FromResult(true) };
        for (var i = 0; i < 20; i++)
        {
            Measure(client, execution, false);
            Measure(client, execution, true);
        }
        var measured = AllocationMeasurement.WithoutConcurrentGc(() => (
            Dispatch: Measure(client, execution, false), Closures: Measure(client, execution, true)));
        await Assert.That(measured.Dispatch).IsEqualTo(0L);
        await Assert.That(measured.Closures).IsGreaterThan(0L);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure(RespireClient client, Execution execution, bool control)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            var response = client.ExecuteWithCorrectionAsync(execution, RespireClient.CorrectionOrdering.FenceFirst);
#pragma warning disable RESP009 // Short-circuit completion checks prove these synthetic responses cannot block the allocation probe.
            if (!response.IsCompletedSuccessfully || !response.Result) throw new InvalidOperationException();
#pragma warning restore RESP009
            var withState = client.ExecuteWithCorrectionAsync(execution, RespireClient.CorrectionOrdering.OrderedCorrection,
                state: (Client: client, Key: "key"), correct: static (_, _) => throw new InvalidOperationException());
#pragma warning disable RESP009 // Preserve the synchronous fast-path probe; Result is read only after successful completion.
            if (!withState.IsCompletedSuccessfully || !withState.Result) throw new InvalidOperationException();
#pragma warning restore RESP009
            if (control) GC.KeepAlive(CreateIdentityAccessor(execution));
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Func<RespireClient.TrackedConnectionIdentity> CreateIdentityAccessor(Execution execution)
        => () => execution.ConnectionIdentity;

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SuccessfulOrDefinitiveOutcomeDoesNotCorrect(bool serverError)
    {
#pragma warning disable RESP010 // Test-owned client isolates definitive outcomes that must not trigger correction.
        await using var client = RespireClient.Create(new RespireOptions { Endpoints = [new("127.0.0.1", 6379)] });
#pragma warning restore RESP010
        var execution = new Execution
        {
            Response = serverError ? ValueTask.FromException<bool>(new RespireServerException("ERR definite")) : ValueTask.FromResult(true),
        };
        var corrected = false;
        var notified = false;
        var response = client.ExecuteWithCorrectionAsync(execution, RespireClient.CorrectionOrdering.OrderedCorrection,
            state: 0, correct: (_, _) => { corrected = true; return default; }, () => notified = true);
        if (serverError) await Assert.That(async () => await response).Throws<RespireServerException>();
        else await Assert.That(await response).IsTrue();
        await Assert.That(corrected || notified).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ProvenUnsubmittedOperationDoesNotCorrect(bool submissionFlag)
    {
#pragma warning disable RESP010 // Test-owned client isolates operations proven not to have been submitted.
        await using var client = RespireClient.Create(new RespireOptions { Endpoints = [new("127.0.0.1", 6379)] });
#pragma warning restore RESP010
        var execution = new Execution
        {
            CommandMayBeOutstanding = !submissionFlag,
            Response = ValueTask.FromException<bool>(submissionFlag
                ? new OperationCanceledException()
                : new RespireCommandNotSubmittedException(new OperationCanceledException())),
        };
        var corrected = false;
        var notified = false;
        await Assert.That(async () => await client.ExecuteWithCorrectionAsync(execution,
            RespireClient.CorrectionOrdering.OrderedCorrection,
            state: 0, correct: (_, _) => { corrected = true; return default; }, () => notified = true)).Throws<OperationCanceledException>();
        await Assert.That(corrected || notified).IsFalse();
    }

    [Test]
    public async Task CorrectionReadsFinalIdentityAndNotifiesBeforeWaiting()
    {
#pragma warning disable RESP010 // Test-owned client isolates final-identity observation and correction notification ordering.
        await using var client = RespireClient.Create(new RespireOptions { Endpoints = [new("127.0.0.1", 6379)] });
#pragma warning restore RESP010
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = new Execution { Response = new(completion.Task), ConnectionIdentity = new(new("old", 1), 1) };
        var notified = false;
        var identity = default(RespireClient.TrackedConnectionIdentity);
        var response = client.ExecuteWithCorrectionAsync(execution, RespireClient.CorrectionOrdering.OrderedCorrection,
            state: 0, correct: async (_, current) =>
            {
                identity = current;
                await Assert.That(notified).IsTrue();
                started.TrySetResult();
                await release.Task;
            }, () => notified = true).AsTask();
        execution.ConnectionIdentity = new(new("replacement", 2), 2);
        var failure = new IOException("lost reply");
        completion.SetException(failure);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(identity).IsEqualTo(execution.ConnectionIdentity);
            await Assert.That(response.IsCompleted).IsFalse();
        }
        finally { release.TrySetResult(); }
        var observed = await Assert.That(async () => await response).Throws<IOException>();
        await Assert.That(ReferenceEquals(observed, failure)).IsTrue();
    }

    [Test]
    public async Task MissingIdentityCannotRunFenceDependentCorrection()
    {
#pragma warning disable RESP010 // Test-owned client exercises missing-identity rejection before any dependent correction.
        await using var client = RespireClient.Create(new RespireOptions { Endpoints = [new("127.0.0.1", 6379)] });
#pragma warning restore RESP010
        var execution = new Execution { Response = ValueTask.FromException<bool>(new IOException("lost reply")) };
        var corrected = false;
        await Assert.That(async () => await client.ExecuteWithCorrectionAsync(execution,
            RespireClient.CorrectionOrdering.FenceFirst, state: 0, correct: (_, _) => { corrected = true; return default; }))
            .Throws<InvalidOperationException>();
        await Assert.That(corrected).IsFalse();
    }

    [Test]
    public async Task RejectedFenceCannotRunDependentCorrection()
    {
        await using var server = new FakeRespServer(2, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("CLIENT KILL ID ") ? "-NOPERM fence denied\r\n"u8.ToArray() : null,
        };
#pragma warning disable RESP010 // Test-owned client isolates the rejected fence from dependent correction dispatch.
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespireProtocol.Resp2, Endpoints = [new("127.0.0.1", server.Port)],
        });
#pragma warning restore RESP010
        var execution = new Execution
        {
            Response = ValueTask.FromException<bool>(new IOException("lost reply")),
            ConnectionIdentity = new(new("127.0.0.1", server.Port), 42),
        };
        var corrected = false;
        await Assert.That(async () => await client.ExecuteWithCorrectionAsync(execution,
            RespireClient.CorrectionOrdering.FenceFirst, state: 0, correct: (_, _) => { corrected = true; return default; }))
            .Throws<RespireServerException>();
        await Assert.That(corrected).IsFalse();
    }

    private sealed class Execution : RespireClient.ITrackedCorrectionExecution<bool>
    {
        public ValueTask<bool> Response { get; init; }
        public RespireClient.TrackedConnectionIdentity ConnectionIdentity { get; set; }
        public bool CommandMayBeOutstanding { get; init; } = true;
    }
}
