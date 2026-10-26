using Respire.Testing;
using Respire.TestSupport;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ArrayFacetTests
{
    [Test]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task NumericCollectionExpressionsPreserveConsecutiveSlots(int protocol, bool transactional)
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespireProtocol)protocol });
#pragma warning restore RESP010
        await Assert.That(await client.Arrays.SetAsync("numbers", 0UL, [9, 9, 9])).IsEqualTo(3UL);
        await Assert.That(await client.Arrays.SetAsync("numbers", 0UL, [1, 2])).IsEqualTo(0UL);
        await Assert.That(await client.Arrays.GetStringAsync("numbers", 1)).IsEqualTo("2");
        await Assert.That(await client.Arrays.SetAsync("numbers", 0UL, [3, 4], CancellationToken.None)).IsEqualTo(0UL);
        await Assert.That(await client.Arrays.GetStringAsync("numbers", 1)).IsEqualTo("4");

        using var batch = transactional ? null : client.CreateBatch();
        await using var transaction = transactional ? client.CreateTransaction() : null;
        var queue = (IRespireCommandQueue?)transaction ?? batch!;
        var set = queue.Arrays.Set("numbers", 0UL, [5, 6]);
        var second = queue.Arrays.GetString("numbers", 1UL);
        var third = queue.Arrays.GetString("numbers", 2UL);
        byte[] bytes = [65, 66];
        var binary = queue.Arrays.Set("binary", 0UL, (ReadOnlyMemory<byte>)bytes);
        var binaryRead = queue.Arrays.GetString("binary", 0UL);
        var binaryCount = queue.Arrays.Count("binary");
        if (transaction is not null) await transaction.CommitAsync();
        else await batch!.ExecuteAsync();
        await Assert.That(set.Result).IsEqualTo(0UL);
        await Assert.That(second.Result).IsEqualTo("6");
        await Assert.That(third.Result).IsEqualTo("9");
        await Assert.That(binary.Result).IsEqualTo(1UL);
        await Assert.That(binaryRead.Result).IsEqualTo("AB");
        await Assert.That(binaryCount.Result).IsEqualTo(1UL);
    }

    [Test]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task EscapedGlobClassCharactersStayLiteral(int protocol, bool ignoreCase)
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespireProtocol)protocol });
#pragma warning restore RESP010
        await client.Arrays.SetAsync("letters", 0, new RespireValue[] { "a", "-", "z", "b", "A", "Z", "B" });
        var matches = await client.Arrays.GrepAsync("letters", RespireArrayBound.First, RespireArrayBound.Last,
            [new RespireArrayPredicate(RespireArrayPredicateKind.Glob, @"[\a-z]")],
            new RespireArrayGrepOptions { IgnoreCase = ignoreCase });
        await Assert.That(matches).IsEquivalentTo(ignoreCase ? new ulong[] { 0, 1, 2, 4, 5 } : new ulong[] { 0, 1, 2 });
    }

    [Test]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task BoundedAggregateArithmeticPreservesRequestedDirection(int protocol, bool descending)
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespireProtocol)protocol });
#pragma warning restore RESP010
        var maximum = decimal.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RespireValue[] values = descending ? [maximum, maximum, "-" + maximum] : ["-" + maximum, maximum, maximum];
        await client.Arrays.SetAsync("ordered", 0, values);
        var start = descending ? 2UL : 0UL;
        var end = descending ? 0UL : 2UL;
        await Assert.That((await client.Arrays.AggregateAsync("ordered", start, end, RespireArrayOperation.Sum)).NumericText)
            .IsEqualTo(maximum);
        await Assert.That(async () => await client.Arrays.AggregateAsync("ordered", end, start, RespireArrayOperation.Sum))
            .Throws<RespireServerException>().WithMessage("ERR Respire.Testing does not support this AROP numeric range");
        await Assert.That(await client.Arrays.CountAsync("ordered")).IsEqualTo(3UL);
    }

    [Test]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task EmptyRegularExpressionIsRejectedBeforeKeyLookup(int protocol, bool present)
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespireProtocol)protocol });
#pragma warning restore RESP010
        await ArrayFacetScenarios.EmptyRegularExpressionIsRejectedBeforeKeyLookup(client, present);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task GlobSearchHasBoundedWorkAndLeavesConnectionsUsable(int protocol)
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespireProtocol)protocol });
#pragma warning restore RESP010
        await client.Arrays.SetAsync("adversarial", 0, new string('a', 1024));
        await Assert.That(await client.Arrays.GrepAsync("adversarial", RespireArrayBound.First, RespireArrayBound.Last,
            new RespireArrayPredicate(RespireArrayPredicateKind.Glob, "*a*a*a*a*a*a*a*a*b"))).IsEmpty();
        var oversized = new string('?', 1_000_001);
        await client.Arrays.SetAsync("budget", 0, new string('a', oversized.Length));
        await Assert.That(async () => await client.Arrays.GrepAsync("budget", RespireArrayBound.First, RespireArrayBound.Last,
            new RespireArrayPredicate(RespireArrayPredicateKind.Glob, oversized)))
            .Throws<RespireServerException>().WithMessage("ERR Respire.Testing ARGREP glob exceeded its work limit");
        await Assert.That(await client.Arrays.CountAsync("budget")).IsEqualTo(1UL);
        await Assert.That(await client.Arrays.GetStringAsync("adversarial", 0)).IsEqualTo(new string('a', 1024));
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task SerializationPredicatesAndValidation(int protocol)
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespireProtocol)protocol });
#pragma warning restore RESP010
        await ArrayFacetScenarios.SerializationPredicatesAndValidation(client.WithKeyPrefix("validation:"));
    }

    [Test]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task EveryCommandHasDeferredExecution(int protocol, bool transactional)
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespireProtocol)protocol });
#pragma warning restore RESP010
        await ArrayFacetScenarios.DeferredCommands(client.WithKeyPrefix("deferred:"), transactional);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task SparseSlotsAndUnsignedIndexesPreservePresence(int protocol)
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespireProtocol)protocol });
#pragma warning restore RESP010
        await ArrayFacetScenarios.SparseSlotsAndUnsignedIndexesPreservePresence(client);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ScansPageByIndexInBothDirections(int protocol)
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespireProtocol)protocol });
#pragma warning restore RESP010
        await ArrayFacetScenarios.ScansPageByIndexInBothDirections(client);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task CursorRingAndLastItemsRespectHolesAndResize(int protocol)
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespireProtocol)protocol });
#pragma warning restore RESP010
        await ArrayFacetScenarios.CursorRingAndLastItemsRespectHolesAndResize(client);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task SearchAggregateAndInfoKeepReplyShapes(int protocol)
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespireProtocol)protocol });
#pragma warning restore RESP010
        await ArrayFacetScenarios.SearchAggregateAndInfoKeepReplyShapes(client);
    }

}
