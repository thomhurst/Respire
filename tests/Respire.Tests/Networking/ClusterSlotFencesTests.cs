using Respire.Infrastructure;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClusterSlotFencesTests
{
    [Test]
    public async Task MigrationReceivedAfterTheLastOwnerChangeIsNotFenced()
    {
        await using var a = Node(7000);
        var fences = new ClusterSlotFences();
        fences.MarkOwnerChanged(0);
        var token = ClusterSlotMutationClock.Next();

        await Assert.That(fences.IsFenced(0, a, a, Endpoint(a), token)).IsFalse();
        // The source check is the router's job when nothing newer happened.
        await Assert.That(fences.IsFenced(0, null, a, Endpoint(a), token)).IsFalse();
    }

    [Test]
    public async Task OwnerChangeAfterReceiptFencesTheMigration()
    {
        await using var a = Node(7000);
        var fences = new ClusterSlotFences();
        var token = ClusterSlotMutationClock.Next();
        fences.MarkOwnerChanged(0);

        await Assert.That(fences.IsFenced(0, a, a, Endpoint(a), token)).IsTrue();
        await Assert.That(fences.Version(0)).IsGreaterThan(token);
    }

    [Test]
    public async Task DependentMigrationCrossesTheFenceOnlyFromTheCurrentOwner()
    {
        await using var a = Node(7000);
        await using var b = Node(7001);
        await using var c = Node(7002);
        var fences = new ClusterSlotFences();
        fences.MarkOwnerChanged(0);
        var bc = ClusterSlotMutationClock.Next();
        var ab = ClusterSlotMutationClock.Next();

        fences.RecordMigration([0], a, Endpoint(a), b, Endpoint(b), ab);
        await Assert.That(fences.Version(0)).IsEqualTo(ab);

        // B->C was received before A->B, but B owns the slot through that SMIGRATED chain.
        await Assert.That(fences.IsFenced(0, b, b, Endpoint(b), bc)).IsFalse();
        await Assert.That(fences.IsFenced(0, b, c, Endpoint(c), bc)).IsTrue();
        await Assert.That(fences.IsFenced(0, null, null, new RespireEndpoint("none", 1), bc)).IsTrue();

        // Crossing keeps the version monotonic.
        fences.RecordMigration([0], b, Endpoint(b), c, Endpoint(c), bc);
        await Assert.That(fences.Version(0)).IsEqualTo(ab);
    }

    [Test]
    public async Task NonSmigratedOwnerChangeEndsTheChain()
    {
        await using var a = Node(7000);
        await using var b = Node(7001);
        var fences = new ClusterSlotFences();
        var bc = ClusterSlotMutationClock.Next();
        var ab = ClusterSlotMutationClock.Next();
        fences.RecordMigration([0], a, Endpoint(a), b, Endpoint(b), ab);
        fences.MarkOwnerChanged(0);

        await Assert.That(fences.IsFenced(0, b, b, Endpoint(b), bc)).IsTrue();
    }

    [Test]
    public async Task MigrationReceivedBeforeTheChainBeganIsFenced()
    {
        await using var a = Node(7000);
        await using var b = Node(7001);
        var fences = new ClusterSlotFences();
        var bc = ClusterSlotMutationClock.Next();
        fences.MarkOwnerChanged(0);
        var ab = ClusterSlotMutationClock.Next();
        fences.RecordMigration([0], a, Endpoint(a), b, Endpoint(b), ab);

        await Assert.That(fences.IsFenced(0, b, b, Endpoint(b), bc)).IsTrue();
    }

    [Test]
    public async Task SourceThatLeftAndReturnedAfterReceiptIsFenced()
    {
        await using var a = Node(7000);
        await using var b = Node(7001);
        var fences = new ClusterSlotFences();
        var ac = ClusterSlotMutationClock.Next();
        var ba = ClusterSlotMutationClock.Next();
        var ab = ClusterSlotMutationClock.Next();
        fences.RecordMigration([0], a, Endpoint(a), b, Endpoint(b), ab);
        fences.RecordMigration([0], b, Endpoint(b), a, Endpoint(a), ba);

        // A->B departed A after A->C was received.
        await Assert.That(fences.IsFenced(0, a, a, Endpoint(a), ac)).IsTrue();
        // A migration received after that departure is newer than the whole chain.
        await Assert.That(fences.IsFenced(0, a, a, Endpoint(a), ClusterSlotMutationClock.Next())).IsFalse();
    }

    [Test]
    public async Task NewerReturnMigrationKeepsEarlierSourceDepartureInChain()
    {
        await using var a = Node(7000);
        await using var b = Node(7001);
        await using var c = Node(7002);
        var fences = new ClusterSlotFences();
        var ab = ClusterSlotMutationClock.Next();
        var ac = ClusterSlotMutationClock.Next();
        var ba = ClusterSlotMutationClock.Next();
        fences.RecordMigration([0], a, Endpoint(a), b, Endpoint(b), ab);
        fences.RecordMigration([0], b, Endpoint(b), a, Endpoint(a), ba);

        // A->C was received after A->B, but B->A was processed first.
        await Assert.That(fences.IsFenced(0, a, a, Endpoint(a), ac)).IsTrue();
    }

    [Test]
    public async Task SlotsMovedTogetherKeepIndependentChains()
    {
        await using var a = Node(7000);
        await using var b = Node(7001);
        var fences = new ClusterSlotFences();
        var bc = ClusterSlotMutationClock.Next();
        fences.MarkOwnerChanged(1);
        var ab = ClusterSlotMutationClock.Next();
        fences.RecordMigration([0, 1], a, Endpoint(a), b, Endpoint(b), ab);

        // Slot 1 changed owner after B->C was received; slot 0 did not.
        await Assert.That(fences.IsFenced(0, b, b, Endpoint(b), bc)).IsFalse();
        await Assert.That(fences.IsFenced(1, b, b, Endpoint(b), bc)).IsTrue();
    }

    [Test]
    public async Task OverlongChainClosesConservatively()
    {
        var nodes = new RespireConnectionMultiplexer[ClusterSlotFences.MaxChainLength + 2];
        for (var i = 0; i < nodes.Length; i++) nodes[i] = Node(7000 + i);
        try
        {
            var fences = new ClusterSlotFences();
            // Tokens received in reverse chain order, so every link after the first is dependent.
            var tokens = new long[nodes.Length - 1];
            for (var i = tokens.Length - 1; i >= 0; i--) tokens[i] = ClusterSlotMutationClock.Next();
            for (var i = 0; i < ClusterSlotFences.MaxChainLength; i++)
            {
                await Assert.That(fences.IsFenced(0, nodes[i], nodes[i], Endpoint(nodes[i]), tokens[i])).IsFalse();
                fences.RecordMigration([0], nodes[i], Endpoint(nodes[i]), nodes[i + 1], Endpoint(nodes[i + 1]), tokens[i]);
            }

            // The next dependent link would exceed the bound: it still applies, then closes the
            // chain, so a further dependent link is fenced.
            var last = ClusterSlotFences.MaxChainLength;
            await Assert.That(fences.IsFenced(0, nodes[last], nodes[last], Endpoint(nodes[last]), tokens[last])).IsFalse();
            fences.RecordMigration([0], nodes[last], Endpoint(nodes[last]), nodes[last + 1], Endpoint(nodes[last + 1]), tokens[last]);
            var older = tokens[^1] - 1;
            await Assert.That(fences.IsFenced(0, nodes[last + 1], nodes[last + 1], Endpoint(nodes[last + 1]), older)).IsTrue();
        }
        finally
        {
            foreach (var node in nodes) await node.DisposeAsync();
        }
    }

    private static RespireConnectionMultiplexer Node(int port) => RespireConnectionMultiplexer.Create("127.0.0.1", port);

    private static RespireEndpoint Endpoint(RespireConnectionMultiplexer node) => new(node.Host, node.Port);
}
