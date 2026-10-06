using Respire.Testing;
using Respire.TestSupport;
using TUnit.Core;

namespace Respire.Tests;

public class ArrayFacetTests
{
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
