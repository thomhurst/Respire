using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ClientCacheProvenanceTests
{
    [Test]
    [Arguments("LCS")]
    [Arguments("MGET")]
    [Arguments("JSON.MGET")]
    [Arguments("ZUNION")]
    public async Task ProvenanceCoversEveryQueryDependencyLayout(string operation)
    {
        var cache = new ClientSideCacheCoordinator(new());
        var owner = new ClientSideCacheCoordinator.TrackingProvenance();
        RespireValue[] arguments = operation switch
        {
            "JSON.MGET" => ["first", "second", "$"],
            "ZUNION" => [2, "first", "second"],
            _ => ["first", "second"],
        };
        var command = new CmdN(new Verb(operation), arguments);
        owner.Register(operation, in command);
        Put(cache, "first");
        Put(cache, "second");
        Put(cache, "unrelated");
        owner.Fence(cache);
        await Assert.That(cache.TryGetHit(new RespireKey("first"), out _)).IsFalse();
        await Assert.That(cache.TryGetHit(new RespireKey("second"), out _)).IsFalse();
        await Assert.That(cache.TryGetHit(new RespireKey("unrelated"), out _)).IsTrue();
    }

    [Test]
    public async Task LossFencesSharedKeysQueriesAndFillsButPreservesIndependentOwners()
    {
        var cache = new ClientSideCacheCoordinator(new());
        var lost = new ClientSideCacheCoordinator.TrackingProvenance();
        var healthy = new ClientSideCacheCoordinator.TrackingProvenance();
        var command = new CmdN(Verbs.MGet, ["lost", "shared"]);
        lost.Register("MGET", in command);
        var healthyCommand = new CmdN(Verbs.MGet, ["healthy", "shared"]);
        healthy.Register("MGET", in healthyCommand);
        Put(cache, "lost");
        Put(cache, "healthy");
        Put(cache, "shared");
        var lostFill = cache.BeginRead(new RespireKey("lost"));
        var healthyFill = cache.BeginRead(new RespireKey("healthy-fill"));
        var query = new CmdN(RespireCommands.String.LCS.Verb, ["shared", "healthy"]);
        await Assert.That(ClientSideCacheCoordinator.TryCreateQuery("LCS", in query, out var request)).IsTrue();
        var queryFill = cache.BeginRead("LCS", in request);
        using var response = RespValue.BulkString("old"u8.ToArray());
        cache.CompleteRead(in queryFill, in response, allowInsert: true);
        var pendingQuery = cache.BeginRead("LCS", in request);

        lost.Fence(cache);
        cache.CompleteRead(in lostFill, in response, allowInsert: true);
        cache.CompleteRead(in healthyFill, in response, allowInsert: true);
        cache.CompleteRead(in pendingQuery, in response, allowInsert: true);

        await Assert.That(cache.TryGetHit(new RespireKey("lost"), out _)).IsFalse();
        await Assert.That(cache.TryGetHit(new RespireKey("shared"), out _)).IsFalse();
        await Assert.That(cache.TryGetHit(in request, out _)).IsFalse();
        await Assert.That(cache.TryGetHit(new RespireKey("healthy"), out _)).IsTrue();
        await Assert.That(cache.TryGetHit(new RespireKey("healthy-fill"), out _)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UnknownOrOverBudgetProvenanceKeepsGlobalFence(bool bytes)
    {
        var cache = new ClientSideCacheCoordinator(new());
        var owner = new ClientSideCacheCoordinator.TrackingProvenance();
        if (bytes)
        {
            var command = new Cmd1(Verbs.Get, new string('x', ClientSideCacheCoordinator.TrackingProvenance.MaximumKeyBytes + 1));
            owner.Register("GET", in command);
        }
        else
        {
            var command = new Cmd1(Verbs.Eval, "return 1");
            owner.Register("EVAL", in command);
        }
        Put(cache, "unrelated");
        var pending = cache.BeginRead(new RespireKey("pending"));
        owner.Fence(cache);
        using var response = RespValue.BulkString("old"u8.ToArray());
        cache.CompleteRead(in pending, in response, allowInsert: true);
        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task KeyBudgetIncludesHistoricalEvictedKeys()
    {
        var cache = new ClientSideCacheCoordinator(new() { MaxEntries = 1 });
        var owner = new ClientSideCacheCoordinator.TrackingProvenance();
        for (var index = 0; index <= ClientSideCacheCoordinator.TrackingProvenance.MaximumKeys; index++)
        {
            var command = new Cmd1(Verbs.Get, $"key{index}");
            owner.Register("GET", in command);
            Put(cache, $"key{index}");
        }
        Put(cache, "unrelated");
        owner.Fence(cache);
        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task BinaryProvenanceOwnsKeysAndMutationStillRejectsRebasedFills()
    {
        var cache = new ClientSideCacheCoordinator(new());
        var owner = new ClientSideCacheCoordinator.TrackingProvenance();
        var bytes = "key"u8.ToArray();
        var command = new Cmd1(Verbs.Get, bytes);
        owner.Register("GET", in command);
        bytes[0] = (byte)'x';
        var pending = cache.BeginRead(new RespireKey("key"));
        var mutation = cache.BeginKeysMutation(["key"]);
        cache.CompleteMutation(in mutation);
        var rebased = cache.RebaseRead(in pending);
        Put(cache, "key");
        owner.Fence(cache);
        using var response = RespValue.BulkString("old"u8.ToArray());
        cache.CompleteRead(in rebased, in response, allowInsert: true);
        await Assert.That(cache.Count).IsEqualTo(0);
    }

    private static void Put(ClientSideCacheCoordinator cache, string text)
    {
        var token = cache.BeginRead(new RespireKey(text));
        using var response = RespValue.BulkString("value"u8.ToArray());
        cache.CompleteRead(in token, in response, allowInsert: true);
    }
}
