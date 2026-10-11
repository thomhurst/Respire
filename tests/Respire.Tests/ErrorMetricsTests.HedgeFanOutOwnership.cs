using System.Reflection;
using System.Runtime.CompilerServices;
using Respire.Internal;
using Respire.Protocol;
using Respire.Networking;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public partial class ErrorMetricsTests
{
    [Test]
    public async Task CompletedAttemptCopyAddsNoSuccessAllocation()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture();
        for (var index = 0; index < 10; index++)
        {
            _ = MeasureHedgeAttemptCopy(false);
            _ = MeasureHedgeAttemptCopy(true);
        }
        var measured = AllocationMeasurement.WithoutConcurrentGc(() =>
            (MeasureHedgeAttemptCopy(false), MeasureHedgeAttemptCopy(true)));
        await Assert.That(measured.Item1).IsEqualTo(0L);
        await Assert.That(measured.Item2).IsGreaterThanOrEqualTo(37_000L);
        await Assert.That(capture.Items).IsEmpty();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureHedgeAttemptCopy(bool control)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++)
        {
            var caller = DispatchResponseSource<int>.Start();
            var leg = DispatchResponseSource<int>.Start();
            caller.Observation.CopyCompletedAttempts(0, leg.Observation);
            leg.CompleteInternal();
            caller.CompleteInternal();
            if (control) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Test]
    [MatrixDataSource]
    public async Task CompletedHedgeResultRetainsNestedAttempts(
        [Matrix(false, true)] bool hedgeWins, [Matrix(0, 7, int.MaxValue - 1)] int prefix,
        [Matrix(false, true)] bool listenerThrows)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture(throwOnMeasurement: listenerThrows);
        var caller = DispatchResponseSource<int>.Start();
        var original = DispatchResponseSource<RespValue>.Start();
        var hedge = DispatchResponseSource<RespValue>.Start();
        var winner = hedgeWins ? hedge : original;
        var loser = hedgeWins ? original : hedge;
        try
        {
            caller.Observation.SetAttempts(prefix);
            // A completed inner route contributes its retries without lending its lifetime.
            using (var nested = RespireTelemetry.ErrorObservation.Rent(force: true))
            {
                nested.SetAttempts(2);
                winner.Observation.SetAttempts(nested.Attempts);
            }
            winner.Observation.Retry();
            loser.Observation.SetAttempts(19);
            var winningReply = Task.FromResult(default(RespValue));
            var pending = new TaskCompletionSource<RespValue>(TaskCreationOptions.RunContinuationsAsynchronously);
            var race = CreateOwnershipRace(hedgeWins ? pending.Task : winningReply,
                hedgeWins ? winningReply : pending.Task, original, hedge, client.Core.Multiplexer.GetConnection());
            var result = await ResolveOwnershipRace(race, caller.Observation, prefix);
            await Assert.That(ReferenceEquals(result.Winner, winningReply)).IsTrue();
            var expected = prefix > int.MaxValue - 3 ? int.MaxValue : prefix + 3;
            await Assert.That(caller.Observation.Attempts).IsEqualTo(expected);
            winner.CompleteInternal();
            winner = null!;
            loser.Observation.Retry();
            var failure = new InvalidOperationException("caller conversion");
            caller.Fail(failure);
            caller = null!;
            var final = capture.Items.Single();
            await Assert.That((bool)final.Tags["redis.client.errors.internal"]!).IsFalse();
            await Assert.That(final.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(expected);
            await Assert.That(loser.Observation.Attempts).IsEqualTo(20);
        }
        finally
        {
            caller?.CompleteInternal();
            winner?.CompleteInternal();
            loser.CompleteInternal();
        }
    }

    [Test]
    [MatrixDataSource]
    public async Task FailedHedgeResultPreservesOriginalFailureAndAttempts(
        [Matrix(false, true)] bool cancelled, [Matrix(5, int.MaxValue)] int prefix)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture(throwOnMeasurement: true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Exception expected = cancelled ? new OperationCanceledException(cancellation.Token)
            : new InvalidOperationException("original failure");
        var original = DispatchResponseSource<RespValue>.Start();
        var hedge = DispatchResponseSource<RespValue>.Start();
        var caller = DispatchResponseSource<int>.Start();
        try
        {
            original.Observation.SetAttempts(3);
            hedge.Observation.SetAttempts(23);
            caller.Observation.SetAttempts(prefix);
            var race = CreateOwnershipRace(Task.FromException<RespValue>(expected),
                Task.FromException<RespValue>(new IOException("optional failure")), original, hedge);
            Exception? actual = null;
            try { _ = await ResolveOwnershipRace(race, caller.Observation, prefix); }
            catch (Exception error) { actual = error; }
            await Assert.That(ReferenceEquals(actual, expected)).IsTrue();
            if (cancelled) await Assert.That(((OperationCanceledException)actual!).CancellationToken).IsEqualTo(cancellation.Token);
            await Assert.That(caller.Observation.Attempts).IsEqualTo(prefix == int.MaxValue ? int.MaxValue : 8);
        }
        finally
        {
            caller.CompleteInternal();
            original.CompleteInternal();
            hedge.CompleteInternal();
        }
    }

    private const BindingFlags OwnershipRaceFlags = BindingFlags.Instance | BindingFlags.NonPublic;

    private static object CreateOwnershipRace(Task<RespValue> original, Task<RespValue>? hedge,
        DispatchResponseSource<RespValue> originalOwner, DispatchResponseSource<RespValue> hedgeOwner,
        RespireConnection? alternative = null)
    {
        var type = typeof(RespireClient).GetNestedType("HedgeRace", BindingFlags.NonPublic)!;
        var race = Activator.CreateInstance(type)!;
        type.GetField("Original", OwnershipRaceFlags)!.SetValue(race, original);
        type.GetField("Hedge", OwnershipRaceFlags)!.SetValue(race, hedge);
        type.GetField("OriginalOwner", OwnershipRaceFlags)!.SetValue(race, originalOwner);
        type.GetField("HedgeOwner", OwnershipRaceFlags)!.SetValue(race, hedgeOwner);
        type.GetField("Alternative", OwnershipRaceFlags)!.SetValue(race, alternative);
        return race;
    }

    private static ValueTask<(RespValue Response, Task<RespValue> Winner)> ResolveOwnershipRace(
        object race, RespireTelemetry.ErrorObservation observation, int prefix)
        => (ValueTask<(RespValue, Task<RespValue>)>)race.GetType().GetMethod("ResolveAsync", OwnershipRaceFlags)!
            .Invoke(race, [observation, prefix])!;
}
