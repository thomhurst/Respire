using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.PerTestSession)]
public class HashFieldScanIntegrationTests(ModernRedisTestContainer fixture)
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task FieldsAndPagesWorkAcrossExecutionForms(int protocol)
    {
        await using var owner = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = (RespProtocol)protocol,
            Endpoints = { new RespireEndpoint(fixture.Host, fixture.Port) },
        });
        var client = owner.WithKeyPrefix($"field-scan:{Guid.NewGuid():N}:");
        var expected = Enumerable.Range(0, 1200).Select(index => $"field:{index:D4}").ToArray();
        var values = expected.Select(field => (field, (RespireValue)"value")).ToArray();
        await client.Hashes.SetAsync("hash", values);
        foreach (var execution in new[] { 0, 1, 2 })
        {
            var empty = await ReadPageAsync(client, "missing", 0, execution);
            empty.IsComplete.Should().BeTrue();
            empty.Fields.Should().BeEmpty();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            ulong cursor = 0;
            var pages = 0;
            do
            {
                var page = await ReadPageAsync(client, "hash", cursor, execution);
                cursor = page.Cursor;
                seen.UnionWith(page.Fields);
                (++pages).Should().BeLessThan(10_000);
            } while (cursor != 0);
            pages.Should().BeGreaterThan(1);
            seen.Should().BeEquivalentTo(expected.Where(field => field.StartsWith("field:0", StringComparison.Ordinal)));
        }
        var enumerated = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var field in client.Hashes.ScanFieldsAsync("hash", countHint: 13)) enumerated.Add(field);
        enumerated.Should().BeEquivalentTo(expected);
        // Existing field/value scans retain their original shape.
        var pairs = new List<KeyValuePair<string, string>>();
        await foreach (var pair in client.Hashes.ScanAsync("hash", "field:000?")) pairs.Add(pair);
        pairs.Should().HaveCount(10);
        pairs.Should().OnlyContain(pair => pair.Value == "value");
    }

    private static async Task<RespireHashScanPage> ReadPageAsync(RespireClient client, string key, ulong cursor, int execution)
    {
        if (execution == 0) return await client.Hashes.ScanFieldsPageAsync(key, cursor, "field:0*", 13);
        using var batch = client.CreateBatch();
        await using var tx = client.CreateTransaction();
        IRespireCommandQueue queue = execution == 2 ? tx : batch;
        var pending = queue.Hashes.ScanFieldsPage(key, cursor, "field:0*", 13);
        if (execution == 2) await tx.CommitAsync(); else await batch.ExecuteAsync();
        return pending.Result;
    }
}
