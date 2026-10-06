using Respire.Testing;
using Respire.TestSupport;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ArrayFacetTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task GlobSearchHasBoundedWorkAndLeavesConnectionsUsable(int protocol)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
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
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
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
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
        await ArrayFacetScenarios.DeferredCommands(client.WithKeyPrefix("deferred:"), transactional);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task SparseSlotsAndUnsignedIndexesPreservePresence(int protocol)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
        await ArrayFacetScenarios.SparseSlotsAndUnsignedIndexesPreservePresence(client);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ScansPageByIndexInBothDirections(int protocol)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
        await ArrayFacetScenarios.ScansPageByIndexInBothDirections(client);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task CursorRingAndLastItemsRespectHolesAndResize(int protocol)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
        await ArrayFacetScenarios.CursorRingAndLastItemsRespectHolesAndResize(client);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task SearchAggregateAndInfoKeepReplyShapes(int protocol)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
        await ArrayFacetScenarios.SearchAggregateAndInfoKeepReplyShapes(client);
    }

}
