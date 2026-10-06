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
    [Arguments(2, false, "redis:8.10-alpine")]
    [Arguments(3, false, "redis:8.10-alpine")]
    [Arguments(2, true, "redis:8.10-alpine")]
    [Arguments(3, true, "redis:8.10-alpine")]
    [Arguments(2, false, "redis:8.8.3-alpine")]
    [Arguments(3, false, "redis:8.8.3-alpine")]
    [Arguments(2, true, "redis:8.8.3-alpine")]
    [Arguments(3, true, "redis:8.8.3-alpine")]
    public async Task EmptyRegularExpressionIsRejectedBeforeKeyLookup(int protocol, bool present, string image)
    {
        var lease = await servers.LeaseAsync(image);
        await using var client = await RespireClient.ConnectAsync(lease.ConnectionString(protocol));
        await RequireArrayCommandsAsync(client, image);
        await ArrayFacetScenarios.EmptyRegularExpressionIsRejectedBeforeKeyLookup(
            client.WithKeyPrefix("EmptyRegularExpressionIsRejectedBeforeKeyLookup:" + Guid.NewGuid().ToString("N") + ":"), present);
    }

    [Test]
    [Arguments(2, "redis:8.10-alpine")]
    [Arguments(3, "redis:8.10-alpine")]
    [Arguments(2, "redis:8.8.3-alpine")]
    [Arguments(3, "redis:8.8.3-alpine")]
    public async Task InfoPopulatesEveryReleasedField(int protocol, string image)
    {
        var lease = await servers.LeaseAsync(image);
        await using var client = await RespireClient.ConnectAsync(lease.ConnectionString(protocol));
        await RequireArrayCommandsAsync(client, image);
        await client.Arrays.InsertAsync("info", "first", "second", "third");
        var info = await client.Arrays.InfoAsync("info", full: true);
        using var raw = await client.ExecuteAsync(RespireCommands.Array.ARINFO, "info", "FULL");
        var fields = new Dictionary<string, RespireResult>(StringComparer.Ordinal);
        for (var index = 0; index < raw.Count; index += 2) fields.Add(raw[index].AsString(), raw[index + 1]);
        Dictionary<string, ulong> integers = new(StringComparer.Ordinal)
        {
            ["count"] = info.Count, ["len"] = info.Length, ["next-insert-index"] = info.NextInsertIndex,
            ["slices"] = info.Slices, ["directory-size"] = info.DirectorySize,
            ["super-dir-entries"] = info.SuperDirectoryEntries, ["slice-size"] = info.SliceSize,
        };
        foreach (var (name, value) in integers)
            await Assert.That(value).IsEqualTo((ulong)fields[name].AsInteger());
        await Assert.That(info.Slices).IsGreaterThan(0UL);
        await Assert.That(info.DirectorySize).IsGreaterThan(0UL);
        await Assert.That(info.SliceSize).IsGreaterThan(0UL);
        await Assert.That(info.DenseSlices).IsEqualTo((ulong)fields["dense-slices"].AsInteger());
        await Assert.That(info.SparseSlices).IsEqualTo((ulong)fields["sparse-slices"].AsInteger());
        await Assert.That(info.AverageDenseSize).IsEqualTo(fields["avg-dense-size"].AsDouble());
        await Assert.That(info.AverageDenseFill).IsEqualTo(fields["avg-dense-fill"].AsDouble());
        await Assert.That(info.AverageSparseSize).IsEqualTo(fields["avg-sparse-size"].AsDouble());
    }

    [Test]
    [Arguments(2, "redis:8.10-alpine")]
    [Arguments(3, "redis:8.10-alpine")]
    [Arguments(2, "redis:8.8.3-alpine")]
    [Arguments(3, "redis:8.8.3-alpine")]
    public async Task ArrayMutationsAndReadsHaveWatchParity(int protocol, string image)
    {
        var lease = await servers.LeaseAsync(image);
        await FakeTransactionParityTests.AssertWatchTracksMutationsAsync(
            RespireOptions.Parse(lease.ConnectionString(protocol)), useFake: false, requireArrays: true);
    }

    [Test]
    [Arguments(2, "redis:8.10-alpine")]
    [Arguments(3, "redis:8.10-alpine")]
    [Arguments(2, "redis:8.8.3-alpine")]
    [Arguments(3, "redis:8.8.3-alpine")]
    public async Task SerializationPredicatesAndValidation(int protocol, string image)
    {
        var lease = await servers.LeaseAsync(image);
        await using var client = await RespireClient.ConnectAsync(lease.ConnectionString(protocol));
        await RequireArrayCommandsAsync(client, image);
        await ArrayFacetScenarios.SerializationPredicatesAndValidation(client.WithKeyPrefix("validation:" + Guid.NewGuid().ToString("N") + ":"));
    }

    [Test]
    [Arguments(2, false, "redis:8.10-alpine")]
    [Arguments(3, false, "redis:8.10-alpine")]
    [Arguments(2, true, "redis:8.10-alpine")]
    [Arguments(3, true, "redis:8.10-alpine")]
    [Arguments(2, false, "redis:8.8.3-alpine")]
    [Arguments(3, false, "redis:8.8.3-alpine")]
    [Arguments(2, true, "redis:8.8.3-alpine")]
    [Arguments(3, true, "redis:8.8.3-alpine")]
    public async Task EveryCommandHasDeferredExecution(int protocol, bool transactional, string image)
    {
        var lease = await servers.LeaseAsync(image);
        await using var client = await RespireClient.ConnectAsync(lease.ConnectionString(protocol));
        await RequireArrayCommandsAsync(client, image);
        await ArrayFacetScenarios.DeferredCommands(client.WithKeyPrefix("deferred:" + Guid.NewGuid().ToString("N") + ":"), transactional);
    }

    [Test]
    [Arguments(2, "redis:8.10-alpine")]
    [Arguments(3, "redis:8.10-alpine")]
    [Arguments(2, "redis:7.4-alpine")]
    [Arguments(3, "redis:7.4-alpine")]
    [Arguments(2, "redis:8.8.3-alpine")]
    [Arguments(3, "redis:8.8.3-alpine")]
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
    [Arguments(2, "redis:8.8.3-alpine")]
    [Arguments(3, "redis:8.8.3-alpine")]
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
    [Arguments(2, "redis:8.8.3-alpine")]
    [Arguments(3, "redis:8.8.3-alpine")]
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
    [Arguments(2, "redis:8.8.3-alpine")]
    [Arguments(3, "redis:8.8.3-alpine")]
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
        // Both the minimum supported family and the requested 8.10 acceptance server are mandatory.
        if (image is "redis:8.10-alpine" or "redis:8.8.3-alpine") await Assert.That(available).IsTrue();
        Skip.Unless(available, "Redis array commands are unavailable on this server.");
    }
}
