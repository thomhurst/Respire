using Respire.TestSupport;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ClassDataSource<VersionedServerFixture>(Shared = SharedType.PerTestSession)]
public class ArrayFacetIntegrationTests(VersionedServerFixture servers)
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task SerializationPredicatesAndValidation(int protocol)
    {
        var lease = await servers.LeaseAsync("redis:8.10-alpine");
        await using var client = await RespireClient.ConnectAsync(lease.ConnectionString(protocol));
        await RequireArrayCommandsAsync(client, "redis:8.10-alpine");
        await ArrayFacetScenarios.SerializationPredicatesAndValidation(client.WithKeyPrefix("validation:" + Guid.NewGuid().ToString("N") + ":"));
    }

    [Test]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task EveryCommandHasDeferredExecution(int protocol, bool transactional)
    {
        var lease = await servers.LeaseAsync("redis:8.10-alpine");
        await using var client = await RespireClient.ConnectAsync(lease.ConnectionString(protocol));
        await RequireArrayCommandsAsync(client, "redis:8.10-alpine");
        await ArrayFacetScenarios.DeferredCommands(client.WithKeyPrefix("deferred:" + Guid.NewGuid().ToString("N") + ":"), transactional);
    }

    [Test]
    [Arguments(2, "redis:8.10-alpine")]
    [Arguments(3, "redis:8.10-alpine")]
    [Arguments(2, "redis:7.4-alpine")]
    [Arguments(3, "redis:7.4-alpine")]
    public async Task SparseSlotsAndUnsignedIndexesPreservePresence(int protocol, string image)
    {
        var lease = await servers.LeaseAsync(image);
        await using var client = await RespireClient.ConnectAsync(lease.ConnectionString(protocol));
        await RequireArrayCommandsAsync(client, image);
        await ArrayFacetScenarios.SparseSlotsAndUnsignedIndexesPreservePresence(client.WithKeyPrefix("SparseSlotsAndUnsignedIndexesPreservePresence:" + Guid.NewGuid().ToString("N") + ":"));
    }

    [Test]
    [Arguments(2, "redis:8.10-alpine")]
    [Arguments(3, "redis:8.10-alpine")]
    [Arguments(2, "redis:7.4-alpine")]
    [Arguments(3, "redis:7.4-alpine")]
    public async Task ScansPageByIndexInBothDirections(int protocol, string image)
    {
        var lease = await servers.LeaseAsync(image);
        await using var client = await RespireClient.ConnectAsync(lease.ConnectionString(protocol));
        await RequireArrayCommandsAsync(client, image);
        await ArrayFacetScenarios.ScansPageByIndexInBothDirections(client.WithKeyPrefix("ScansPageByIndexInBothDirections:" + Guid.NewGuid().ToString("N") + ":"));
    }

    [Test]
    [Arguments(2, "redis:8.10-alpine")]
    [Arguments(3, "redis:8.10-alpine")]
    [Arguments(2, "redis:7.4-alpine")]
    [Arguments(3, "redis:7.4-alpine")]
    public async Task CursorRingAndLastItemsRespectHolesAndResize(int protocol, string image)
    {
        var lease = await servers.LeaseAsync(image);
        await using var client = await RespireClient.ConnectAsync(lease.ConnectionString(protocol));
        await RequireArrayCommandsAsync(client, image);
        await ArrayFacetScenarios.CursorRingAndLastItemsRespectHolesAndResize(client.WithKeyPrefix("CursorRingAndLastItemsRespectHolesAndResize:" + Guid.NewGuid().ToString("N") + ":"));
    }

    [Test]
    [Arguments(2, "redis:8.10-alpine")]
    [Arguments(3, "redis:8.10-alpine")]
    [Arguments(2, "redis:7.4-alpine")]
    [Arguments(3, "redis:7.4-alpine")]
    public async Task SearchAggregateAndInfoKeepReplyShapes(int protocol, string image)
    {
        var lease = await servers.LeaseAsync(image);
        await using var client = await RespireClient.ConnectAsync(lease.ConnectionString(protocol));
        await RequireArrayCommandsAsync(client, image);
        await ArrayFacetScenarios.SearchAggregateAndInfoKeepReplyShapes(client.WithKeyPrefix("SearchAggregateAndInfoKeepReplyShapes:" + Guid.NewGuid().ToString("N") + ":"));
    }

    private static async Task RequireArrayCommandsAsync(IRespireClient client, string image)
    {
        using var info = await client.ExecuteAsync(RespireCommands.Server.COMMAND, "INFO", "ARGREP");
        var available = !info[0].IsNull;
        // The requested acceptance server must implement the complete family.
        if (image == "redis:8.10-alpine") await Assert.That(available).IsTrue();
        Skip.Unless(available, "Redis 8.10 array commands are unavailable on this server.");
    }
}
