using System.Text;
using Respire.Extensions.Probabilistic;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ProbabilisticClientTests
{
    private static readonly byte[] Hello = "%1\r\n+proto\r\n:3\r\n"u8.ToArray();

    [Test]
    public async Task KeyPrefixedProbabilisticCommandsUseTheirKnownModuleLayouts()
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var probabilistic = new RespireProbabilisticClient(client.WithKeyPrefix("tenant:"));

        await probabilistic.BloomAddAsync("filter", "item");

        await Assert.That(server.ReceivedCommands.Contains("BF.ADD tenant:filter item")).IsTrue();
    }

    [Test]
    public async Task KeyPrefixedMergesPrefixDestinationAndEverySource()
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var probabilistic = new RespireProbabilisticClient(client.WithKeyPrefix("tenant:"));

        await probabilistic.CountMinMergeAsync("destination", ["source-a", "source-b"], new() { Weights = [1, 2] });
        await probabilistic.TDigestMergeAsync("digest", ["source-a", "source-b"], new() { Compression = 100, Override = true });

        await Assert.That(server.ReceivedCommands.Contains(
            "CMS.MERGE tenant:destination 2 tenant:source-a tenant:source-b WEIGHTS 1 2")).IsTrue();
        await Assert.That(server.ReceivedCommands.Contains(
            "TDIGEST.MERGE tenant:digest 2 tenant:source-a tenant:source-b COMPRESSION 100 OVERRIDE")).IsTrue();
    }

    [Test]
    public async Task KeyPrefixedViewStillRejectsCatalogCommandsWithoutModuleLayouts()
    {
        await using var client = RespireClient.Create(DisconnectedOptions());
        var prefixed = client.WithKeyPrefix("tenant:");

        await Assert.That(async () => await prefixed.ExecuteAsync(RespireCommands.All.ToArray().Single(command => command.Name == "TS.MGET"), "FILTER", "sensor=1"))
            .Throws<NotSupportedException>();
        // Core commands have layouts for routing, but only explicitly prefixable layouts may be rewritten.
        await Assert.That(async () => await prefixed.ExecuteAsync(RespireCommands.All.ToArray().Single(command => command.Name == "GET"), "key"))
            .Throws<NotSupportedException>();
    }

    [Test]
    public async Task KeyPrefixedViewRejectsAbsentKeysInsteadOfWritingThePrefixKey()
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var prefixed = client.WithKeyPrefix("tenant:");
        var bloomAdd = RespireCommands.All.ToArray().Single(command => command.Name == "BF.ADD");
        var countMinMerge = RespireCommands.All.ToArray().Single(command => command.Name == "CMS.MERGE");

        await Assert.That(async () => await prefixed.ExecuteAsync(bloomAdd, RespireValue.Null, "item"))
            .Throws<ArgumentNullException>();
        await Assert.That(async () => await prefixed.ExecuteFireAndForgetAsync(bloomAdd, RespireValue.Null, "item"))
            .Throws<ArgumentNullException>();
        await Assert.That(async () => await prefixed.ExecuteAsync(countMinMerge, "destination", 2, "source", RespireValue.Null))
            .Throws<ArgumentNullException>();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("BF.", StringComparison.Ordinal)
                || command.StartsWith("CMS.", StringComparison.Ordinal)))
            .IsFalse();
    }

    [Test]
    public async Task NullItemsAreRejectedBeforeSending()
    {
        await using var client = RespireClient.Create(DisconnectedOptions());
        var probabilistic = new RespireProbabilisticClient(client);

        await Assert.That(async () => await probabilistic.CuckooCountAsync("filter", RespireValue.Null))
            .Throws<ArgumentNullException>();
        await Assert.That(async () => await probabilistic.BloomInsertAsync("filter", [RespireValue.Null]))
            .Throws<ArgumentNullException>();
        await Assert.That(async () => await probabilistic.CuckooInsertAsync("filter", []))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task BloomInsertAcceptsCapacityAndErrorIndependently()
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var probabilistic = new RespireProbabilisticClient(client);

        await probabilistic.BloomInsertAsync("capacity-only", ["one"], new() { Capacity = 100 });
        await probabilistic.BloomInsertAsync("error-only", ["two"], new() { ErrorRate = 0.01 });

        await Assert.That(server.ReceivedCommands.Contains("BF.INSERT capacity-only CAPACITY 100 ITEMS one")).IsTrue();
        await Assert.That(server.ReceivedCommands.Contains("BF.INSERT error-only ERROR 0.01 ITEMS two")).IsTrue();
    }

    [Test]
    public async Task CountMinMergeRejectsNonPositiveWeightsBeforeSending()
    {
        await using var client = RespireClient.Create(DisconnectedOptions());
        var probabilistic = new RespireProbabilisticClient(client);

        await Assert.That(async () => await probabilistic.CountMinMergeAsync("destination", ["source"],
            new() { Weights = [0] })).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task CountMinMergeWritesPositiveIntegerWeightsOnly()
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var probabilistic = new RespireProbabilisticClient(client);

        await probabilistic.CountMinMergeAsync("destination", ["source-a", "source-b"], new() { Weights = [2, 3] });

        await Assert.That(server.ReceivedCommands.Contains(
            "CMS.MERGE destination 2 source-a source-b WEIGHTS 2 3")).IsTrue();
    }

    [Test]
    public async Task CountMinIncrementRejectsNonPositiveAmountsBeforeSending()
    {
        await using var client = RespireClient.Create(DisconnectedOptions());
        var probabilistic = new RespireProbabilisticClient(client);
        var increments = new Dictionary<RespireValue, long> { ["item"] = 0 };

        await Assert.That(async () => await probabilistic.CountMinIncrementAsync("sketch", increments))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task TopKEvictionResultsPreserveBinaryBytes()
    {
        await using var server = Server();
        server.ReplyOverride = (_, command) => command switch
        {
            "HELLO 3" => Hello,
            _ when command.StartsWith("TOPK.ADD sketch ", StringComparison.Ordinal)
                || command.StartsWith("TOPK.INCRBY sketch ", StringComparison.Ordinal)
                => [.. "*1\r\n$2\r\n"u8.ToArray(), 0xFF, 0x00, .. "\r\n"u8.ToArray()],
            _ => null,
        };
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var probabilistic = new RespireProbabilisticClient(client);

        var evicted = await probabilistic.TopKAddAsync("sketch", [(RespireValue)(new byte[] { 0xFE, 0x01 })]);
        var increments = new Dictionary<RespireValue, long> { [(RespireValue)(new byte[] { 0xFD, 0x02 })] = 1 };
        var incrementEvictions = await probabilistic.TopKIncrementAsync("sketch", increments);

        await Assert.That(evicted.Length).IsEqualTo(1);
        await Assert.That(evicted[0]!.SequenceEqual(new byte[] { 0xFF, 0x00 })).IsTrue();
        await Assert.That(incrementEvictions[0]!.SequenceEqual(new byte[] { 0xFF, 0x00 })).IsTrue();
    }

    [Test]
    public async Task TDigestReadsBulkStringInfinities()
    {
        await using var server = Server();
        server.ReplyOverride = (_, command) => command switch
        {
            "HELLO 3" => Hello,
            "TDIGEST.BYRANK digest 100 200" => "*2\r\n$3\r\ninf\r\n$4\r\n-inf\r\n"u8.ToArray(),
            "TDIGEST.MAX digest" => "$4\r\n+inf\r\n"u8.ToArray(),
            _ => null,
        };
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var probabilistic = new RespireProbabilisticClient(client);

        var values = await probabilistic.TDigestByRankAsync("digest", [100, 200]);
        var maximum = await probabilistic.TDigestMaximumAsync("digest");

        await Assert.That(double.IsPositiveInfinity(values[0])).IsTrue();
        await Assert.That(double.IsNegativeInfinity(values[1])).IsTrue();
        await Assert.That(double.IsPositiveInfinity(maximum)).IsTrue();
    }

    [Test]
    public async Task CuckooInsertReportsFullFilterSeparatelyFromSuccess()
    {
        await using var server = Server();
        server.ReplyOverride = (_, command) => command switch
        {
            "HELLO 3" => Hello,
            "CF.INSERTNX filter ITEMS a b c" => "*3\r\n:1\r\n:0\r\n:-1\r\n"u8.ToArray(),
            _ => null,
        };
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var probabilistic = new RespireProbabilisticClient(client);

        var results = await probabilistic.CuckooInsertIfAbsentAsync("filter", ["a", "b", "c"]);

        await Assert.That(results.SequenceEqual([RespireCuckooInsertResult.Inserted, RespireCuckooInsertResult.AlreadyExists, RespireCuckooInsertResult.FilterFull])).IsTrue();
    }

    [Test]
    public async Task TDigestAddRejectsNonFiniteObservations()
    {
        await using var client = RespireClient.Create(DisconnectedOptions());
        var probabilistic = new RespireProbabilisticClient(client);

        await Assert.That(async () => await probabilistic.TDigestAddAsync("digest", [1, double.NaN]))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await probabilistic.TDigestAddAsync("digest", [double.PositiveInfinity]))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task TdigestTrimRejectsEqualCutoffs()
    {
        await using var client = RespireClient.Create(DisconnectedOptions());
        var probabilistic = new RespireProbabilisticClient(client);

        await Assert.That(async () => await probabilistic.TDigestTrimmedMeanAsync("digest", 0.5, 0.5))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task InvalidTopKDecayAndTDigestCompressionAreRejected()
    {
        await using var client = RespireClient.Create(DisconnectedOptions());
        var probabilistic = new RespireProbabilisticClient(client);

        await Assert.That(async () => await probabilistic.TopKReserveAsync("sketch", 10,
            new() { Decay = double.NaN })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await probabilistic.TDigestCreateAsync("digest",
            new() { Compression = 1001 })).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task BloomExpansionRejectsValuesAboveRedisMaximum()
    {
        await using var client = RespireClient.Create(DisconnectedOptions());
        var probabilistic = new RespireProbabilisticClient(client);

        await Assert.That(async () => await probabilistic.BloomReserveAsync("filter", 0.01, 100,
            new() { Expansion = 32769 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await probabilistic.BloomInsertAsync("filter", ["item"],
            new() { Expansion = 32769 })).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task CuckooReserveRejectsValuesAboveRedisMaxima()
    {
        await using var client = RespireClient.Create(DisconnectedOptions());
        var probabilistic = new RespireProbabilisticClient(client);

        await Assert.That(async () => await probabilistic.CuckooReserveAsync("filter", 100,
            new() { BucketSize = 256 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await probabilistic.CuckooReserveAsync("filter", 100,
            new() { MaximumIterations = 65536 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await probabilistic.CuckooReserveAsync("filter", 100,
            new() { Expansion = 32769 })).Throws<ArgumentOutOfRangeException>();
    }

    private static FakeRespServer Server()
        => new(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => Hello,
                "BF.ADD tenant:filter item" => ":1\r\n"u8.ToArray(),
                "BF.INSERT capacity-only CAPACITY 100 ITEMS one" => "*1\r\n:1\r\n"u8.ToArray(),
                "BF.INSERT error-only ERROR 0.01 ITEMS two" => "*1\r\n:1\r\n"u8.ToArray(),
                _ => null,
            },
        };

    private static RespireOptions Options(FakeRespServer server) => new()
    {
        Endpoints = { new("127.0.0.1", server.Port) },
        Protocol = RespProtocol.Resp3,
        ThreadPoolMonitoring = false,
    };

    private static RespireOptions DisconnectedOptions() => new()
    {
        Endpoints = { new("localhost") },
        ThreadPoolMonitoring = false,
    };
}
