using System.Runtime.CompilerServices;
using System.Text;
using Respire.Commands;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ImmediateRawKeyLayoutTests
{
    [Test]
    [Arguments("KEYDB.MEXISTS")]
    [Arguments("MGET")]
    [Arguments("DEL")]
    [Arguments("MSET")]
    [Arguments("COPY")]
    [Arguments("BITOP")]
    [Arguments("EVAL")]
    [Arguments("FCALL")]
    [Arguments("ZUNION")]
    [Arguments("ZINTERSTORE")]
    [Arguments("BLPOP")]
    [Arguments("BLMPOP")]
    [Arguments("MSETEX")]
    [Arguments("XREAD")]
    [Arguments("XREADGROUP")]
    [Arguments("MIGRATE")]
    [Arguments("JSON.MGET")]
    public async Task CrossSlotFailsBeforeIoOnAllImmediateSurfaces(string operation)
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            UseCluster = true, Endpoints = [new("localhost", 1)], Connections = 1,
        });
        var args = CreateArguments(operation, "{a}:one", "{b}:two");
        var catalog = RespireCommands.All.ToArray().Single(command => command.Name == operation);
        foreach (var descriptor in new[] { catalog, (RespireCommand)operation })
        {
            var error = await Assert.That(async () =>
                await client.ExecuteAsync(descriptor, args, flags: RespireCommandFlags.NoRedirect))
                .ThrowsExactly<RespireServerException>();
            await Assert.That(error!.Code).IsEqualTo("CROSSSLOT");
            if (catalog.IsBlocking(args)) continue;
            var discarded = await Assert.That(async () => await client.ExecuteFireAndForgetAsync(descriptor, args))
                .ThrowsExactly<RespireServerException>();
            await Assert.That(discarded!.Code).IsEqualTo("CROSSSLOT");
        }
    }

    [Test]
    public async Task InlineAndInterpolatedKeysAreValidatedWithoutSplittingArguments()
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            UseCluster = true, Endpoints = [new("localhost", 1)], Connections = 1,
        });
        var first = "{a}:key with spaces";
        var second = "{b}:key";
        await Assert.That(async () => await client.ExecuteAsync($"MGET {first} {second}"))
            .ThrowsExactly<RespireServerException>();
        await Assert.That(async () => await client.ExecuteFireAndForgetAsync($"MGET {first} {second}"))
            .ThrowsExactly<RespireServerException>();
        await Assert.That(async () => await client.ExecuteAsync("MGET {a}:one", second))
            .ThrowsExactly<RespireServerException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SameSlotBinaryKeysRouteAndPreserveNonKeyArguments(bool catalog)
    {
        byte[] first = [255, .. "{tenant}:one"u8];
        byte[] second = [0, 128, .. "{tenant}:two"u8];
        string[] operations = ["KEYDB.MEXISTS", "MGET", "MSET", "BITOP", "EVAL", "ZINTERSTORE",
            "BLPOP", "BLMPOP", "MSETEX", "XREAD", "XREADGROUP", "MIGRATE", "JSON.MGET"];
        await using var owner = new FakeRespServer(2, FakeRespServer.OkReply);
        var slot = ClusterHash.GetSlot(first);
        var topology = Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{owner.Port}\r\n");
        await using var seed = new FakeRespServer(topology, "-ERR wrong route\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true, Connections = 1, Endpoints = [new("127.0.0.1", seed.Port)],
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        foreach (var operation in operations)
        {
            var args = CreateArguments(operation, first, second);
            RespireCommand descriptor = catalog
                ? RespireCommands.All.ToArray().Single(command => command.Name == operation) : operation;
            using var result = await client.ExecuteAsync(descriptor, args, cancellationToken: timeout.Token);
            await Assert.That(result.AsString()).IsEqualTo("OK");
            var actual = owner.ReceivedArguments[^1];
            await Assert.That(actual.Length).IsEqualTo(args.Length + 1);
            for (var index = 0; index < args.Length; index++)
            {
                var expected = new byte[args[index].GetWireLength()];
                args[index].WriteWirePayload(expected);
                await Assert.That(actual[index + 1]).IsEquivalentTo(expected, CollectionOrdering.Matching);
            }
        }
        await Assert.That(seed.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS"]);
        await Assert.That(owner.CommandsSeen).IsEqualTo(operations.Length);
    }

    [Test]
    public async Task UnknownLayoutsAndStandaloneExecutionKeepServerValidation()
    {
        await using var standaloneServer = new FakeRespServer(FakeRespServer.OkReply);
        await using var standalone = await FakeRespServer.ConnectClientAsync(standaloneServer.Port);
        using var crossSlot = await standalone.ExecuteAsync(RespireCommands.String.MGET, "{a}:one", "{b}:two");
        using var malformed = await standalone.ExecuteAsync(RespireCommands.String.MSET, "unpaired-key");
        await Assert.That(standaloneServer.CommandsSeen).IsEqualTo(2);

        await using var server = new FakeRespServer("*0\r\n"u8.ToArray(), FakeRespServer.OkReply);
        await using var cluster = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true, Connections = 1, Endpoints = [new("127.0.0.1", server.Port)],
        });
        using var unknown = await cluster.ExecuteAsync("VENDOR.UNKNOWN", "{a}:one", "{b}:two");
        using var noKeys = await cluster.ExecuteAsync(RespireCommands.Scripting.EVAL, "return ARGV[1]", 0, "{a}", "{b}");
        using var admin = await cluster.ExecuteAsync(RespireCommands.Dragonfly.DFLYCLUSTER_CONFIG, "{a}", "{b}");
        await Assert.That(server.ReceivedCommands).Contains("VENDOR.UNKNOWN {a}:one {b}:two");
        await Assert.That(server.ReceivedCommands).Contains("EVAL return ARGV[1] 0 {a} {b}");
    }

    [Test]
    public async Task SharedLayoutsPreserveDeferredAllowlistAndRejectMalformedCounts()
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            UseCluster = true, Endpoints = [new("localhost", 1)], Connections = 1,
        });
        foreach (var args in new RespireValue[][] { ["script", -1], ["script", long.MaxValue], ["script", 2, "one"] })
            await Assert.That(async () => await client.ExecuteAsync(RespireCommands.Scripting.EVAL, args))
                .Throws<ArgumentException>();
        await Assert.That(async () => await client.ExecuteAsync(RespireCommands.String.MSET, "unpaired"))
            .Throws<ArgumentException>();
        using var batch = client.CreateBatch();
        await Assert.That(() => batch.Execute(RespireCommands.KeyDb.KEYDB_MEXISTS, "key"))
            .Throws<NotSupportedException>();
        var prefixed = client.WithKeyPrefix("tenant:");
        await Assert.That(async () => await prefixed.ExecuteAsync(RespireCommands.String.MGET, "one", "two"))
            .Throws<NotSupportedException>();
    }

    [Test]
    [NotInParallel] // The no-GC region is process-wide; other tests must not consume its budget.
    public async Task KnownLayoutValidationAllocatesNothingAfterInitialization()
    {
        RespireValue[] keys = ["{tag}:one", "{tag}:two"];
        _ = MeasureValidationAllocations(keys, allocate: false, iterations: 100);
        _ = MeasureValidationAllocations(keys, allocate: true, iterations: 100);

        // Concurrent GC can perturb the thread allocation counter even for an empty
        // interval. See docs/ALLOCATION_MEASUREMENT.md. Failure to establish or retain
        // this boundary fails the test; it never skips or relaxes the zero-byte check.
        var (allocated, positiveControl) = AllocationMeasurement.WithoutConcurrentGc(() =>
            (MeasureValidationAllocations(keys, allocate: false, iterations: 1000),
                MeasureValidationAllocations(keys, allocate: true, iterations: 1000)));
        await Assert.That(allocated).IsEqualTo(0);
        await Assert.That(positiveControl).IsGreaterThanOrEqualTo(1000 * 37);
    }

    // Keep assertion/state-machine allocations outside the JIT's measurement boundary.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureValidationAllocations(RespireValue[] keys, bool allocate, int iterations)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < iterations; index++)
        {
            RawCommandKeyLayouts.ValidateClusterKeys("KEYDB.MEXISTS", keys);
            if (allocate) GC.KeepAlive(AllocateControl());
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    // Returning across a no-inline boundary keeps the deliberate allocation observable.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object AllocateControl() => new byte[37];

    private static RespireValue[] CreateArguments(string operation, RespireValue first, RespireValue second) => operation switch
    {
        "MSET" => [first, "{value-one}", second, "{value-two}"],
        "MSETEX" => [2, first, "{value-one}", second, "{value-two}", "PX", 100],
        "COPY" => [first, second, "REPLACE"],
        "BITOP" => ["AND", first, second],
        "EVAL" or "FCALL" => ["procedure", 2, first, second, "{argument}"],
        "ZUNION" => [2, first, second, "WEIGHTS", 1, 2],
        "ZINTERSTORE" => [first, 1, second, "WEIGHTS", 1],
        "BLPOP" => [first, second, 1],
        "BLMPOP" => [1, 2, first, second, "LEFT"],
        "XREAD" => ["COUNT", 1, "STREAMS", first, second, "0", "0"],
        "XREADGROUP" => ["GROUP", "STREAMS", "consumer", "NOACK", "STREAMS", first, second, ">", ">"],
        "MIGRATE" => ["destination", 6379, "", 0, 1000, "AUTH2", "user", "KEYS", "KEYS", first, second],
        "JSON.MGET" => [first, second, "$.{not-a-key}"],
        _ => [first, second],
    };
}
