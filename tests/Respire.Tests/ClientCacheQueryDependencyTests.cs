using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ClientCacheQueryDependencyTests
{
    [Test]
    [Arguments("publish", false)]
    [Arguments("abandon", false)]
    [Arguments("rebase", false)]
    [Arguments("publish", true)]
    [Arguments("abandon", true)]
    [Arguments("rebase", true)]
    public async Task ReusedRegistrationStorageCannotReviveCompletedCopies(string action, bool publishOriginal)
    {
        var cache = new ClientSideCacheCoordinator(new());
        var request = Request("LCS");
        using var oldResponse = RespValue.Integer(11);
        var old = cache.BeginRead("LCS", in request);
        var oldState = old.Lease!.GetDependency(0).State;
        cache.CompleteRead(in old, in oldResponse, allowInsert: publishOriginal);
        // An abandoned owner can be reused within the same store/epochs: only its identity fences stale publication.
        if (publishOriginal) cache.Clear();
        var current = cache.BeginRead("LCS", in request);
        await Assert.That(ReferenceEquals(old.Lease, current.Lease)).IsTrue();
        await Assert.That(ReferenceEquals(oldState, current.Lease!.GetDependency(0).State)
            || ReferenceEquals(oldState, current.Lease.GetDependency(1).State)).IsTrue();
        if (action == "rebase")
        {
            var stale = cache.RebaseRead(in old);
            await Assert.That(stale.CanCache).IsFalse();
            cache.CompleteRead(in stale, in oldResponse, allowInsert: true);
        }
        else cache.CompleteRead(in old, in oldResponse, allowInsert: action == "publish");
        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(PendingDependencies(cache)).IsEqualTo(2);
        RespireKey second = "second";
        cache.Invalidate(in second);
        using var response = RespValue.Integer(33);
        cache.CompleteRead(in current, in response, allowInsert: true);
        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(PendingDependencies(cache)).IsEqualTo(0);
        var positive = cache.BeginRead("LCS", in request);
        cache.CompleteRead(in positive, in response, allowInsert: true);
        cache.CompleteRead(in positive, in oldResponse, allowInsert: false);
        await Assert.That(cache.TryPeek(in request, out var cached)).IsTrue();
        using (cached) await Assert.That(cached.AsInteger()).IsEqualTo(33);
        await Assert.That(PendingDependencies(cache)).IsEqualTo(0);
    }

    [Test]
    public async Task LargeRegistrationStorageIsClearedButNotRetained()
    {
        var cache = new ClientSideCacheCoordinator(new());
        RespireValue[] keys = Enumerable.Range(0, 17).Select(index => (RespireValue)("large:" + index)).ToArray();
        var request = new ClientSideCacheCoordinator.QueryRequest(new("SINTER", keys), keys[0].AsKey());
        var first = cache.BeginRead("SINTER", in request);
        using var unused = RespValue.Null;
        cache.CompleteRead(in first, in unused, allowInsert: false);
        var second = cache.BeginRead("SINTER", in request);
        await Assert.That(ReferenceEquals(first.Lease, second.Lease)).IsFalse();
        for (var index = 0; index < keys.Length; index++)
            await Assert.That(first.Lease!.GetDependency(index).State is null).IsTrue();
        cache.CompleteRead(in second, in unused, allowInsert: false);
        var idle = cache.InspectForTests().IdleQueryStorage;
        await Assert.That(idle.Leases).IsEqualTo(0);
        await Assert.That(idle.States).IsEqualTo(17);
        await Assert.That(idle.RetainedDependencies).IsEqualTo(0);
    }

    [Test]
    public async Task FullScalarPoolCanRetainAndReuseMultiDependencyStorage()
    {
        var cache = new ClientSideCacheCoordinator(new());
        var scalar = Request("STRLEN");
        var tokens = Enumerable.Range(0, 64).Select(_ => cache.BeginRead("STRLEN", in scalar)).ToArray();
        using var unused = RespValue.Null;
        foreach (var token in tokens) cache.CompleteRead(in token, in unused, allowInsert: false);
        var multiple = Request("LCS");
        var first = cache.BeginRead("LCS", in multiple);
        cache.CompleteRead(in first, in unused, allowInsert: false);
        var second = cache.BeginRead("LCS", in multiple);
        await Assert.That(ReferenceEquals(first.Lease, second.Lease)).IsTrue();
        cache.CompleteRead(in second, in unused, allowInsert: false);
        await Assert.That(cache.InspectForTests().IdleQueryStorage.Leases).IsEqualTo(64);
    }

    [Test]
    public async Task ExhaustedLeaseIdentityCannotWrapOrReturnToThePool()
    {
        var cache = new ClientSideCacheCoordinator(new());
        var request = Request("STRLEN");
        var old = cache.BeginRead("STRLEN", in request);
        // This fresh owner has no concurrent users. Force the terminal identity without billions of rents.
        old.Lease!.Generation = long.MaxValue;
        old = old with { LeaseGeneration = long.MaxValue };
        using var unused = RespValue.Null;
        cache.CompleteRead(in old, in unused, allowInsert: false);
        var current = cache.BeginRead("STRLEN", in request);
        await Assert.That(ReferenceEquals(old.Lease, current.Lease)).IsFalse();
        cache.CompleteRead(in old, in unused, allowInsert: true);
        await Assert.That(PendingDependencies(cache)).IsEqualTo(1);
        cache.CompleteRead(in current, in unused, allowInsert: false);
        await Assert.That(PendingDependencies(cache)).IsEqualTo(0);
    }

    [Test]
    public async Task ConcurrentDependencyChurnKeepsIdleStorageBoundedAndCleared()
    {
        var cache = new ClientSideCacheCoordinator(new());
        var tokens = Enumerable.Range(0, 512).Select(index =>
        {
            RespireValue[] keys = ["churn:" + index, "source:" + index];
            var request = new ClientSideCacheCoordinator.QueryRequest(new("LCS", keys), keys[0].AsKey());
            return cache.BeginRead("LCS", in request);
        }).ToArray();
        cache.FlushForContinuityLoss();
        await Task.Run(() => Parallel.ForEach(tokens, token =>
        {
            using var response = RespValue.Integer(1);
            cache.CompleteRead(in token, in response, allowInsert: true);
            cache.CompleteRead(in token, in response, allowInsert: false);
        }));
        await Assert.That(PendingDependencies(cache)).IsEqualTo(0);
        await Assert.That(cache.Count).IsEqualTo(0);
        var idle = cache.InspectForTests().IdleQueryStorage;
        await Assert.That(idle.Leases).IsEqualTo(64);
        await Assert.That(idle.States).IsEqualTo(256);
        await Assert.That(idle.RetainedDependencies).IsEqualTo(0);
        cache.Clear();
        for (var index = 0; index < 1_000; index++)
        {
            var request = new ClientSideCacheCoordinator.QueryRequest(new("STRLEN", "retired:" + index), "retired:" + index);
            var token = cache.BeginRead("STRLEN", in request);
            using var unused = RespValue.Null;
            cache.CompleteRead(in token, in unused, allowInsert: false);
        }
        idle = cache.InspectForTests().IdleQueryStorage;
        await Assert.That(idle.Leases).IsLessThanOrEqualTo(64);
        await Assert.That(idle.States).IsLessThanOrEqualTo(256);
        await Assert.That(idle.RetainedDependencies).IsEqualTo(0);
    }

    [Test]
    public async Task RetiredRegistrationStorageClearsEveryDependencyReference()
    {
        var cache = new ClientSideCacheCoordinator(new());
        RespireValue[] keys = Enumerable.Range(0, 16).Select(index => (RespireValue)("key:" + index)).ToArray();
        var request = new ClientSideCacheCoordinator.QueryRequest(new("SINTER", keys), keys[0].AsKey());
        var token = cache.BeginRead("SINTER", in request);
        var states = Enumerable.Range(0, 16).Select(index => token.Lease!.GetDependency(index).State).ToArray();
        using var unused = RespValue.Null;
        cache.CompleteRead(in token, in unused, allowInsert: false);
        await Assert.That(token.Lease!.Registered).IsEqualTo(0);
        for (var index = 0; index < states.Length; index++)
        {
            await Assert.That(token.Lease.GetDependency(index).State is null).IsTrue();
            await Assert.That(states[index].Key.Equals(default(RespireKey))).IsTrue();
        }
        await Assert.That(PendingDependencies(cache)).IsEqualTo(0);
    }

    [Test]
    [Arguments("STRLEN", "other", true)]
    [Arguments("STRLEN", "first", false)]
    [Arguments("LCS", "other", true)]
    [Arguments("LCS", "first", false)]
    [Arguments("LCS", "second", false)]
    [Arguments("JSON.MGET", "$", true)]
    [Arguments("JSON.MGET", "second", false)]
    [Arguments("SINTERCARD", "2", true)]
    [Arguments("SINTERCARD", "second", false)]
    [Arguments("SINTER", "second", false)]
    public async Task OnlyActualDependenciesFencePublication(string operation, string invalidated, bool published)
    {
        var cache = new ClientSideCacheCoordinator(new());
        var request = Request(operation);
        var token = cache.BeginRead(operation, in request);
        RespireKey key = invalidated;
        cache.Invalidate(in key);
        using var response = RespValue.Integer(3);
        cache.CompleteRead(in token, in response, allowInsert: true);

        await Assert.That(cache.TryPeek(in request, out var cached)).IsEqualTo(published);
        cached.Dispose();
    }

    [Test]
    public async Task OverlappingMutationFencesRejectBothOldAndIntermediateQueries()
    {
        var cache = new ClientSideCacheCoordinator(new());
        var request = Request("STRLEN");
        var old = cache.BeginRead("STRLEN", in request);
        var command = new Cmd2(Verbs.Set, "first", "new");
        var firstFence = cache.BeforeCommand("SET", in command);
        var intermediate = cache.BeginRead("STRLEN", in request);
        var secondFence = cache.BeforeCommand("SET", in command);
        using var response = RespValue.Integer(3);
        cache.CompleteRead(in old, in response, allowInsert: true);
        cache.CompleteRead(in intermediate, in response, allowInsert: true);
        await Assert.That(cache.Count).IsEqualTo(0);

        var during = cache.BeginRead("STRLEN", in request);
        cache.CompleteMutation(in firstFence);
        cache.CompleteRead(in during, in response, allowInsert: true);
        await Assert.That(cache.Count).IsEqualTo(0);
        var between = cache.BeginRead("STRLEN", in request);
        cache.CompleteMutation(in secondFence);
        cache.CompleteRead(in between, in response, allowInsert: true);
        await Assert.That(cache.Count).IsEqualTo(0);
        var current = cache.BeginRead("STRLEN", in request);
        cache.CompleteRead(in current, in response, allowInsert: true);
        await Assert.That(cache.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WholeStoreFlushRejectsEveryOlderQuery(bool continuity)
    {
        var cache = new ClientSideCacheCoordinator(new());
        var request = Request("STRLEN");
        var token = cache.BeginRead("STRLEN", in request);
        if (continuity) cache.FlushForContinuityLoss();
        else cache.Clear();
        using var response = RespValue.Integer(3);
        cache.CompleteRead(in token, in response, allowInsert: true);
        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RedirectRebaseCapturesFreshDependenciesAndStore()
    {
        var cache = new ClientSideCacheCoordinator(new());
        var request = Request("LCS");
        var original = cache.BeginRead("LCS", in request);
        cache.FlushForContinuityLoss();
        var redirected = cache.RebaseRead(in original);
        using var response = RespValue.Integer(3);
        cache.CompleteRead(in original, in response, allowInsert: false);
        cache.CompleteRead(in redirected, in response, allowInsert: true);
        await Assert.That(cache.Count).IsEqualTo(1);
        cache.Clear();
        await Assert.That(cache.RebaseRead(in redirected).CanCache).IsFalse();
        var fresh = cache.BeginRead("LCS", in request);
        var retried = cache.RebaseRead(in fresh);
        cache.CompleteRead(in fresh, in response, allowInsert: false);
        RespireKey second = "second";
        cache.Invalidate(in second);
        cache.CompleteRead(in retried, in response, allowInsert: true);
        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task UnrelatedSourceDestinationMutationPreservesQueryPublication()
    {
        var cache = new ClientSideCacheCoordinator(new());
        var request = Request("STRLEN");
        var unrelated = cache.BeginRead("STRLEN", in request);
        var command = new Cmd2(Verbs.Rename, "source", "destination");
        var fence = cache.BeforeCommand("RENAME", in command);
        cache.CompleteMutation(in fence);
        using var response = RespValue.Integer(3);
        cache.CompleteRead(in unrelated, in response, allowInsert: true);
        await Assert.That(cache.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("source")]
    [Arguments("destination")]
    public async Task SourceAndDestinationMutationsFenceAffectedQueries(string dependency)
    {
        var cache = new ClientSideCacheCoordinator(new());
        var request = new ClientSideCacheCoordinator.QueryRequest(new("STRLEN", (RespireValue)dependency), dependency);
        var token = cache.BeginRead("STRLEN", in request);
        var command = new Cmd2(Verbs.Rename, "source", "destination");
        var fence = cache.BeforeCommand("RENAME", in command);
        using var response = RespValue.Integer(3);
        cache.CompleteRead(in token, in response, allowInsert: true);
        cache.CompleteMutation(in fence);
        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(PendingDependencies(cache)).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CompletedCopiesCannotRetireOrPublishOverANewGeneration(bool abandon)
    {
        var cache = new ClientSideCacheCoordinator(new());
        var request = Request("LCS");
        using var response = RespValue.Integer(3);
        var old = cache.BeginRead("LCS", in request);
        await Assert.That(PendingDependencies(cache)).IsEqualTo(2);
        cache.CompleteRead(in old, in response, allowInsert: !abandon);
        await Assert.That(PendingDependencies(cache)).IsEqualTo(0);
        cache.Clear();
        var current = cache.BeginRead("LCS", in request);
        cache.CompleteRead(in old, in response, allowInsert: true);
        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(PendingDependencies(cache)).IsEqualTo(2);
        RespireKey second = "second";
        cache.Invalidate(in second);
        cache.CompleteRead(in current, in response, allowInsert: true);
        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(PendingDependencies(cache)).IsEqualTo(0);
    }

    [Test]
    public async Task DuplicateDependenciesRemainRegisteredUntilEveryReaderFinishes()
    {
        var cache = new ClientSideCacheCoordinator(new());
        var request = new ClientSideCacheCoordinator.QueryRequest(new("SINTER", ["first", "first"]), "first");
        var first = cache.BeginRead("SINTER", in request);
        var second = cache.BeginRead("SINTER", in request);
        using var response = RespValue.Integer(3);
        cache.CompleteRead(in first, in response, allowInsert: false);
        await Assert.That(PendingDependencies(cache)).IsEqualTo(1);
        RespireKey key = "first";
        cache.Invalidate(in key);
        cache.CompleteRead(in second, in response, allowInsert: true);
        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(PendingDependencies(cache)).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ErrorAndOversizedRepliesRetireDependencies(bool oversized)
    {
        var cache = new ClientSideCacheCoordinator(new() { MaxSizeBytes = 128 });
        var request = Request("STRLEN");
        var token = cache.BeginRead("STRLEN", in request);
        using var response = oversized ? RespValue.BulkString(new byte[256]) : RespValue.Error("ERR rejected");
        cache.CompleteRead(in token, in response, allowInsert: true);
        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(PendingDependencies(cache)).IsEqualTo(0);
    }

    [Test]
    public async Task UncoveredDependenciesNeverRegisterPendingState()
    {
        var cache = new ClientSideCacheCoordinator(new() { KeyPrefixes = ["first"] });
        var request = Request("LCS");
        var token = cache.BeginRead("LCS", in request);
        await Assert.That(token.CanCache).IsFalse();
        await Assert.That(PendingDependencies(cache)).IsEqualTo(0);
        using var response = RespValue.Integer(3);
        cache.CompleteRead(in token, in response, allowInsert: true);
        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DependencyChurnRetainsOnlyLiveKeysAcrossFlushAndRebase()
    {
        var cache = new ClientSideCacheCoordinator(new());
        for (var index = 0; index < 1_000; index++)
        {
            var key = new RespireKey("unique:" + index);
            var request = new ClientSideCacheCoordinator.QueryRequest(new("STRLEN", key.AsValue()), key);
            var token = cache.BeginRead("STRLEN", in request);
            cache.Invalidate(in key);
            cache.Clear();
            var rebased = cache.RebaseRead(in token);
            using var response = RespValue.Null;
            cache.CompleteRead(in token, in response, allowInsert: false);
            cache.CompleteRead(in rebased, in response, allowInsert: true);
            if (PendingDependencies(cache) != 0) throw new InvalidOperationException("Completed query retained a dependency.");
        }
        await Assert.That(PendingDependencies(cache)).IsEqualTo(0);
    }

    [Test]
    public async Task BinaryQueryAndDependenciesOwnTheirSnapshots()
    {
        var cache = new ClientSideCacheCoordinator(new());
        var bytes = new byte[] { 0, 255, 1 };
        var request = new ClientSideCacheCoordinator.QueryRequest(new("STRLEN", (RespireValue)bytes), new(bytes));
        var token = cache.BeginRead("STRLEN", in request);
        bytes[2] = 2;
        RespireKey unrelated = "other";
        cache.Invalidate(in unrelated);
        using var response = RespValue.Integer(3);
        cache.CompleteRead(in token, in response, allowInsert: true);
        var original = new ClientSideCacheCoordinator.QueryRequest(
            new("STRLEN", (RespireValue)new byte[] { 0, 255, 1 }), new(new byte[] { 0, 255, 1 }));
        await Assert.That(cache.TryPeek(in original, out var cached)).IsTrue();
        cached.Dispose();
        RespireKey dependency = new(new byte[] { 0, 255, 1 });
        cache.Invalidate(in dependency);
        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(PendingDependencies(cache)).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConcurrentPublicationAndInvalidationCannotLeaveAnOlderReply(bool flush)
    {
        var cache = new ClientSideCacheCoordinator(new());
        var request = Request("LCS");
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var tokens = Enumerable.Range(0, 16).Select(_ => cache.BeginRead("LCS", in request)).ToArray();
            using var response = RespValue.Integer(iteration);
            var publishing = Task.Run(() => Parallel.ForEach(tokens, token => cache.CompleteRead(in token, in response, allowInsert: true)));
            var invalidating = Task.Run(() =>
            {
                if (flush) cache.FlushForContinuityLoss();
                else { RespireKey dependency = "second"; cache.Invalidate(in dependency); }
            });
            await Task.WhenAll(publishing, invalidating);
            if (cache.Count != 0 || PendingDependencies(cache) != 0)
                throw new InvalidOperationException("An older query escaped the completed invalidation.");
        }
        await Assert.That(cache.Count).IsEqualTo(0);
    }

    internal static int PendingDependencies(ClientSideCacheCoordinator cache) => cache.InspectForTests().PendingQueryDependencyCount;

    private static ClientSideCacheCoordinator.QueryRequest Request(string operation)
    {
        RespireValue[] arguments = operation switch
        {
            "STRLEN" => ["first"],
            "JSON.MGET" => ["first", "second", "$"],
            "SINTERCARD" => [2, "first", "second"],
            _ => ["first", "second"],
        };
        return new(new ClientCacheCommandKey(operation, arguments), arguments[operation == "SINTERCARD" ? 1 : 0].AsKey());
    }
}
