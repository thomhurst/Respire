using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class CorrectionCoordinatorTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static readonly RespireClient.TrackedConnectionIdentity Identity = new(new("original", 6379), 42, RequiresAsking: true);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AcknowledgementSurvivesRetirementFailureAndReleaseRetries(bool retirementFails)
    {
        await using var client = RespireClient.Create(new RespireOptions { Endpoints = [new("127.0.0.1", 1)] });
        var fences = 0;
        var releases = 0;
        var fence = new CorrectionFence(Identity, (identity, _, acknowledge) =>
        {
            if (identity != Identity) throw new InvalidOperationException("The physical identity changed.");
            fences++;
            acknowledge();
            return retirementFails ? ValueTask.FromException(new ObjectDisposedException("retirement")) : ValueTask.CompletedTask;
        });
        var succeeded = await client.Core.Corrections.EnqueueFencedAsync(fence, _ =>
        {
            if (!fence.IsAcknowledged) throw new InvalidOperationException("Release overtook its fence.");
            return ++releases < 3 ? ValueTask.FromException(new RespireServerException("ERR retry release")) : ValueTask.CompletedTask;
        }, Limit, new(Limit, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2)),
            (_, _) => throw new InvalidOperationException("Unexpected abandonment.")).WaitAsync(Limit);
        await Assert.That(succeeded).IsTrue();
        await Assert.That(fences).IsEqualTo(1);
        await Assert.That(releases).IsEqualTo(3);
    }

    [Test]
    public async Task StrictFencePropagatesRetirementFailureButRetainsAcknowledgement()
    {
        var failure = new IOException("local retirement failed");
        var attempts = 0;
        var fence = new CorrectionFence(Identity, (_, _, acknowledge) =>
        {
            attempts++;
            acknowledge();
            return ValueTask.FromException(failure);
        });
        var error = await Assert.That(async () => await fence.EnsureAsync()).Throws<IOException>();
        await Assert.That(error).IsSameReferenceAs(failure);
        await fence.EnsureAsync();
        await Assert.That(attempts).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RejectedOrUnansweredFenceCannotDispatchCorrection(bool unanswered)
    {
        await using var client = RespireClient.Create(new RespireOptions { Endpoints = [new("127.0.0.1", 1)] });
        var releases = 0;
        string? stage = null;
        string? reason = null;
        var fence = new CorrectionFence(Identity, async (_, token, _) =>
        {
            if (unanswered) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new RespireServerException("NOPERM fence rejected");
        });
        var succeeded = await client.Core.Corrections.EnqueueFencedAsync(fence,
            _ => { releases++; return ValueTask.CompletedTask; }, TimeSpan.FromMilliseconds(10),
            new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero),
            (actualStage, actualReason) => { stage = actualStage; reason = actualReason; }).WaitAsync(Limit);
        await Assert.That(succeeded).IsFalse();
        await Assert.That(fence.IsAcknowledged).IsFalse();
        await Assert.That(releases).IsEqualTo(0);
        await Assert.That(stage).IsEqualTo("fence");
        await Assert.That(reason).IsEqualTo("exhausted");
    }

    [Test]
    public async Task QueueDisposalStopsUnacknowledgedFence()
    {
        await using var client = RespireClient.Create(new RespireOptions { Endpoints = [new("127.0.0.1", 1)] });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releases = 0;
        string? reason = null;
        var fence = new CorrectionFence(Identity, async (_, token, _) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        var cleanup = client.Core.Corrections.EnqueueFencedAsync(fence,
            _ => { releases++; return ValueTask.CompletedTask; }, Limit,
            new(Limit, TimeSpan.Zero, TimeSpan.Zero), (_, value) => reason = value);
        await entered.Task.WaitAsync(Limit);
        await client.DisposeAsync();
        await Assert.That(await cleanup.WaitAsync(Limit)).IsFalse();
        await Assert.That(releases).IsEqualTo(0);
        await Assert.That(reason).IsEqualTo("client_disposed");
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(5)]
    public async Task AttemptsDistinguishTerminalDisposalFromRetryableErrors(int failure)
    {
        await using var client = RespireClient.Create(new RespireOptions { Endpoints = [new("127.0.0.1", 1)] });
        if (failure == 0) await client.DisposeAsync();
        using var stop = new CancellationTokenSource();
        if (failure == 1) stop.Cancel();
        Exception error = failure switch
        {
            0 or 5 => new ObjectDisposedException("client"),
            4 => new ObjectDisposedException("unrelated-resource"),
            1 or 2 => new OperationCanceledException(),
            _ => new RespireServerException("NOPERM release rejected"),
        };
        var result = await CorrectionCoordinator.AttemptAsync(_ => ValueTask.FromException(error), Limit, stop.Token,
            owner: failure == 5 ? null : client.Core);
        await Assert.That(result).IsEqualTo(failure < 2 ? CleanupAttemptResult.Abandoned : CleanupAttemptResult.Failed);
    }

    [Test]
    public async Task OverduePassBoundsFenceWithoutAuthorizingDependentCorrection()
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", 1)], ConnectTimeout = TimeSpan.FromMilliseconds(10),
        });
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFence = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sends = 0;
        var fence = new CorrectionFence(Identity, async (_, token, _) =>
        {
            entered.SetResult(token);
            await releaseFence.Task.WaitAsync(token);
        }, client.Core);
        var correction = CorrectionCoordinator.ConvergeAsync(Identity, fence,
            (_, _) => ++sends == 1 ? pending.Task : Task.CompletedTask,
            TimeSpan.FromMilliseconds(10), TimeSpan.MaxValue).AsTask();
        try
        {
            var token = await entered.Task.WaitAsync(Limit);
            await Assert.That(token.CanBeCanceled).IsTrue();
            await Assert.That(async () => await correction.WaitAsync(Limit)).Throws<OperationCanceledException>();
            await Assert.That(fence.IsAcknowledged).IsFalse();
            await Assert.That(sends).IsEqualTo(1);
        }
        finally
        {
            releaseFence.TrySetResult();
            pending.TrySetResult();
            try { await correction.WaitAsync(Limit); }
            catch (OperationCanceledException) { }
        }
    }

    [Test]
    public async Task OverdueOrderedPassFencesBeforeRetryAndRetainsOriginalRoute()
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sends = 0;
        var fence = new CorrectionFence(Identity, (_, _, acknowledge) => { acknowledge(); return ValueTask.CompletedTask; });
        var retryObservedProof = false;
        var retryRetainedRoute = false;
        try
        {
            await CorrectionCoordinator.ConvergeAsync(Identity, fence, (ordered, identity) =>
            {
                if (++sends == 1) return first.Task;
                retryObservedProof = fence.IsAcknowledged;
                retryRetainedRoute = ordered && identity == Identity with { ServerClientId = 0 };
                return Task.CompletedTask;
            }, TimeSpan.FromMilliseconds(10), TimeSpan.MaxValue);
            await Assert.That(retryObservedProof).IsTrue();
            await Assert.That(retryRetainedRoute).IsTrue();
            await Assert.That(sends).IsEqualTo(2);
        }
        finally { first.TrySetResult(); }
    }

    [Test]
    public async Task CompletedFifoPassNeedsNoFence()
    {
        var fences = 0;
        var fence = new CorrectionFence(Identity, (_, _, _) => { fences++; throw new InvalidOperationException(); });
        await CorrectionCoordinator.ConvergeAsync(Identity, fence, (_, _) => Task.CompletedTask, Limit, TimeSpan.MaxValue);
        await Assert.That(fences).IsEqualTo(0);
    }

    [Test]
    public async Task FailedFenceRetriesSameIdentityBeforeRelease()
    {
        await using var client = RespireClient.Create(new RespireOptions { Endpoints = [new("127.0.0.1", 1)] });
        var identities = new List<RespireClient.TrackedConnectionIdentity>();
        var releases = 0;
        var fence = new CorrectionFence(Identity, (identity, _, acknowledge) =>
        {
            identities.Add(identity);
            if (identities.Count == 1) return ValueTask.FromException(new IOException("unacknowledged"));
            acknowledge();
            return ValueTask.CompletedTask;
        });
        var succeeded = await client.Core.Corrections.EnqueueFencedAsync(fence,
            _ => { releases++; return ValueTask.CompletedTask; }, Limit,
            new(Limit, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2)), (_, _) => { }).WaitAsync(Limit);
        await Assert.That(succeeded).IsTrue();
        await Assert.That(identities.Count).IsEqualTo(2);
        await Assert.That(identities.All(identity => identity == Identity)).IsTrue();
        await Assert.That(releases).IsEqualTo(1);
    }

    [Test]
    public async Task ForegroundDeadlineDoesNotCancelOwedCorrection()
    {
        var correction = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource();
        deadline.Cancel();
        await Assert.That(await CorrectionCoordinator.WaitAsync(correction.Task, deadline.Token)).IsFalse();
        await Assert.That(correction.Task.IsCompleted).IsFalse();
        correction.SetResult();
        await correction.Task;
    }

    [Test]
    public async Task OverduePassWithRejectedFenceNeverStartsDependentRetry()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sends = 0;
        var rejected = new RespireServerException("NOPERM rejected");
        var fence = new CorrectionFence(Identity, (_, _, _) => ValueTask.FromException(rejected));
        try
        {
            var error = await Assert.That(async () => await CorrectionCoordinator.ConvergeAsync(Identity, fence,
                (_, _) => { sends++; return pending.Task; }, TimeSpan.FromMilliseconds(1), TimeSpan.MaxValue))
                .Throws<RespireServerException>();
            await Assert.That(error).IsSameReferenceAs(rejected);
            await Assert.That(sends).IsEqualTo(1);
        }
        finally { pending.TrySetResult(); }
    }
}
