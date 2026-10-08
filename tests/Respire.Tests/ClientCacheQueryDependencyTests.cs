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
        var retried = cache.RebaseRead(in redirected);
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
