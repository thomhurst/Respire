using Respire.Infrastructure;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using MigrationState = Respire.Internal.ClusterMigrationState<object>;

namespace Respire.Tests.Networking;

public class ClusterMigrationStateTests
{
    private static readonly RespireEndpoint Source = new("source", 7000);
    private static readonly RespireEndpoint Target = new("target", 7001);

    [Test]
    public async Task SequencesRejectReplaysButRestartForEachConnection()
    {
        var state = new MigrationState();
        var oldConnection = new object();
        var newConnection = new object();

        await Assert.That(state.TryRecordSequence(oldConnection, 0)).IsTrue();
        await Assert.That(state.TryRecordSequence(oldConnection, 10)).IsTrue();
        await Assert.That(state.TryRecordSequence(oldConnection, 10)).IsFalse();
        await Assert.That(state.TryRecordSequence(oldConnection, 1)).IsFalse();
        await Assert.That(state.TryRecordSequence(newConnection, 0)).IsTrue();
        // A late item on the old connection must not poison the new connection's window.
        await Assert.That(state.TryRecordSequence(oldConnection, 100)).IsTrue();
        await Assert.That(state.TryRecordSequence(newConnection, 1)).IsTrue();
    }

    [Test]
    public async Task ConcurrentReplaysClaimOneSequenceOnly()
    {
        var state = new MigrationState();
        var connection = new object();
        var accepted = 0;
        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
        {
            if (state.TryRecordSequence(connection, 42)) Interlocked.Increment(ref accepted);
        })));
        await Assert.That(accepted).IsEqualTo(1);
    }

    [Test]
    public async Task CountEvictionKeepsNewestEntriesAndOriginalSender()
    {
        var state = new MigrationState();
        var original = new object();
        var later = new object();
        var skipped = new List<(string Reason, object Sender)>();
        state.Defer(new(Source, Target, [0, 1], 10, 0, original), skipped);
        for (var i = 0; i < 64; i++) state.Defer(new(Source, Target, [i], 11, 1, later), skipped);

        await Assert.That(state.DeferredCount).IsEqualTo(64);
        await Assert.That(state.DeferredSlots).IsEqualTo(64);
        await Assert.That(skipped).IsEquivalentTo(new[] { ("deferral_evicted", original) });
    }

    [Test]
    public async Task SlotBudgetEvictsOldestEvenBelowEntryLimit()
    {
        var state = new MigrationState();
        var original = new object();
        var skipped = new List<(string Reason, object Sender)>();
        state.Defer(new(Source, Target, Enumerable.Range(0, ClusterHash.SlotCount).ToArray(), 10, 0, original), skipped);
        await Assert.That(state.DeferredSlots).IsEqualTo(ClusterHash.SlotCount);
        await Assert.That(skipped).IsEmpty();

        state.Defer(new(Source, Target, [2], 11, 1, new object()), skipped);
        await Assert.That(state.DeferredCount).IsEqualTo(1);
        await Assert.That(state.DeferredSlots).IsEqualTo(1);
        await Assert.That(skipped).IsEquivalentTo(new[] { ("deferral_evicted", original) });
    }

    [Test]
    public async Task ExpiryUsesExactBoundaryAndPreservesYoungerEntries()
    {
        long now = 29_999;
        var state = new MigrationState(() => now);
        var first = new object();
        var second = new object();
        var skipped = new List<(string Reason, object Sender)>();
        state.Defer(new(Source, Target, [0, 1], 10, 0, first), skipped);
        state.Defer(new(Source, Target, [2], 11, 1, second), skipped);
        state.Expire(skipped);
        await Assert.That(skipped).IsEmpty();

        now = 30_000;
        state.Expire(skipped);
        await Assert.That(state.DeferredCount).IsEqualTo(1);
        await Assert.That(state.DeferredSlots).IsEqualTo(1);
        await Assert.That(skipped).IsEquivalentTo(new[] { ("deferral_expired", first) });

        now++;
        state.Expire(skipped);
        await Assert.That(state.DeferredCount).IsEqualTo(0);
        await Assert.That(state.DeferredSlots).IsEqualTo(0);
        await Assert.That(skipped).IsEquivalentTo(new[] { ("deferral_expired", first), ("deferral_expired", second) });
    }

    [Test]
    public async Task DependentChainResolvesInOneCallWithOriginalTokens()
    {
        var state = new MigrationState();
        var endpoints = Enumerable.Range(0, 4).Select(i => new RespireEndpoint("node", 7000 + i)).ToArray();
        var nodes = endpoints.ToDictionary(endpoint => endpoint, _ => new object());
        var skipped = new List<(string Reason, object Sender)>();
        // C->D arrived before B->C, before A->B gave B the slot.
        state.Defer(new(endpoints[2], endpoints[3], [0], 10, 0, nodes[endpoints[0]]), skipped);
        state.Defer(new(endpoints[1], endpoints[2], [0], 11, 0, nodes[endpoints[0]]), skipped);
        var applied = new Queue<MigrationState.AppliedMove>();
        applied.Enqueue(new(nodes[endpoints[1]], [0]));
        var tokens = new List<long>();
        state.RetryDependencies(applied, endpoint => nodes[endpoint], Apply);

        await Assert.That(tokens.SequenceEqual(new long[] { 11, 10 })).IsTrue();
        await Assert.That(state.DeferredCount).IsEqualTo(0);
        await Assert.That(state.DeferredSlots).IsEqualTo(0);

        MigrationState.AppliedMove? Apply(RespireEndpoint source, RespireEndpoint target,
            int[] slots, long token, out int[]? waiting)
        {
            tokens.Add(token);
            waiting = null;
            return new(nodes[target], slots);
        }
    }

    [Test]
    public async Task RetrySelectsOnlyMatchingSourceAndOverlappingSlots()
    {
        var state = new MigrationState();
        var sourceNode = new object();
        var otherNode = new object();
        var deferred = new MigrationState.DeferredMigration(Source, Target, [1, 3, 5], 42, 0, sourceNode);
        state.Defer(deferred, []);
        var applied = new Queue<MigrationState.AppliedMove>();
        applied.Enqueue(new(otherNode, [1])); // Wrong identity, even though a slot matches.
        applied.Enqueue(new(sourceNode, [2])); // Right identity, no matching slot.
        applied.Enqueue(new(sourceNode, [1, 3]));
        var attempts = new List<int[]>();
        state.RetryDependencies(applied, _ => sourceNode, Apply);

        await Assert.That(attempts.Count).IsEqualTo(1);
        await Assert.That(attempts[0].SequenceEqual(new[] { 1, 3 })).IsTrue();
        // Slot 1 moved, slot 3 lost its source to an earlier entry, slot 5 was never ready.
        await Assert.That(deferred.Slots.SequenceEqual(new[] { 3, 5 })).IsTrue();
        await Assert.That(state.DeferredCount).IsEqualTo(1);
        await Assert.That(state.DeferredSlots).IsEqualTo(2);

        MigrationState.AppliedMove? Apply(RespireEndpoint source, RespireEndpoint target,
            int[] slots, long token, out int[]? waiting)
        {
            attempts.Add(slots);
            waiting = [3];
            return new(otherNode, [1]);
        }
    }

    [Test]
    public async Task FencedRetryRemovesSlotsWithoutPublishingAnotherMove()
    {
        var state = new MigrationState();
        var sourceNode = new object();
        state.Defer(new(Source, Target, [0], 42, 0, sourceNode), []);
        var applied = new Queue<MigrationState.AppliedMove>();
        applied.Enqueue(new(sourceNode, [0]));
        state.RetryDependencies(applied, _ => sourceNode, Fenced);

        await Assert.That(state.DeferredCount).IsEqualTo(0);
        await Assert.That(state.DeferredSlots).IsEqualTo(0);
        await Assert.That(applied).IsEmpty();

        static MigrationState.AppliedMove? Fenced(RespireEndpoint source, RespireEndpoint target,
            int[] slots, long token, out int[]? waiting)
        {
            waiting = null;
            return null;
        }
    }

    [Test]
    public async Task ClearReleasesDeferredStateWithoutForgettingSequenceClaims()
    {
        var state = new MigrationState();
        var connection = new object();
        state.TryRecordSequence(connection, 42);
        state.Defer(new(Source, Target, [0], 42, 0, connection), []);
        state.ClearDeferred();

        await Assert.That(state.DeferredCount).IsEqualTo(0);
        await Assert.That(state.DeferredSlots).IsEqualTo(0);
        await Assert.That(state.TryRecordSequence(connection, 42)).IsFalse();
        state.ForgetSequence(connection);
        await Assert.That(state.TryRecordSequence(connection, 42)).IsTrue();
    }
}
