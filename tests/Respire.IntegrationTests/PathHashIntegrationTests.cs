using System.Text;
using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ClassDataSource<VersionedServerFixture>(Shared = SharedType.PerTestSession)]
public class PathHashIntegrationTests(VersionedServerFixture servers)
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ExactOperationsAndPrefixTraversal(int protocol)
    {
        var lease = await servers.LeaseAsync(Valkey92IntegrationTests.Image);
        await using var client = await RespireClient.ConnectAsync(lease.ConnectionString(protocol));
        var paths = client.WithKeyPrefix("tenant:").PathHashes;
        (await paths.CountAsync("tree")).Should().Be(0);
        (await paths.GetAllAsync("tree", "missing")).Should().BeEmpty();
        (await paths.LongestPrefixAsync("tree", "missing")).Should().BeNull();
        (await paths.PrefixesAsync("tree", "missing")).Should().BeEmpty();
        (await paths.ScanPageAsync("tree")).Cursor.Should().Be("0");
        (await paths.SetAsync("tree", "", "root", "root-value")).Should().BeTrue();
        (await paths.SetFieldsAsync("tree", "a", [("f", "one"), ("g", "two")])).Should().BeTrue();
        (await paths.SetFieldsAsync("tree", "a", [("f", "ignored"), ("new", "ignored")], RespireSetWhen.NotExists)).Should().BeFalse();
        (await paths.SetFieldsAsync("tree", "a", [("f", "ignored"), ("new", "ignored")], RespireSetWhen.Exists)).Should().BeFalse();
        (await paths.GetStringAsync("tree", "a", "f")).Should().Be("one");
        (await paths.GetBytesAsync("tree", "a", "new")).Should().BeNull();
        await paths.SetManyAsync("tree", [new("ab", [("f", "deep")]), new("abc", [("f", "deepest")]), new("z", [("f", "other")])]);
        (await paths.CountAsync("tree")).Should().Be(5);
        (await paths.ExistsAsync("tree", "ab")).Should().BeTrue();
        (await paths.ExistsAsync("tree", "abcd")).Should().BeFalse();
        var values = await paths.GetFieldsAsync("tree", "a", ["g", "missing", "f"]);
        values.Select(value => value is null ? null : Encoding.UTF8.GetString(value)).Should().Equal("two", null, "one");
        var many = await paths.GetManyAsync("tree", [new("a", ["f"]), new("ab", ["f", "missing"])]);
        many.Select(value => value is null ? null : Encoding.UTF8.GetString(value)).Should().Equal("one", "deep", null);
        var all = await paths.GetAllAsync("tree", "a");
        all.Select(field => Encoding.UTF8.GetString(field.Field)).Should().BeEquivalentTo("f", "g");
        var match = await paths.LongestPrefixAsync("tree", "abcd");
        Encoding.UTF8.GetString(match!.Path).Should().Be("abc");
        Encoding.UTF8.GetString(match.Fields.Single().Value).Should().Be("deepest");
        var ancestors = await paths.PrefixesAsync("tree", "abcd", count: 2, maxPathLength: 2);
        ancestors.Select(entry => Encoding.UTF8.GetString(entry.Path)).Should().Equal("a", "ab");
        var descendants = new List<string>();
        await foreach (var entry in paths.ScanAsync("tree", prefix: "a", count: 1)) descendants.Add(Encoding.UTF8.GetString(entry.Path));
        descendants.Should().Equal("a", "ab", "abc");
        (await paths.RemoveAsync("tree", "a", ["g"])).Should().Be(1);
        (await paths.RemoveAsync("tree", "z")).Should().Be(1);
        (await paths.RemovePrefixAsync("tree", "ab")).Should().Be(2);
        (await paths.RemovePrefixAsync("tree", "")).Should().Be(2);
        (await paths.CountAsync("tree")).Should().Be(0);
    }

    [Test]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task DeferredTypedAndBinaryValues(int protocol, bool transaction)
    {
        var lease = await servers.LeaseAsync(Valkey92IntegrationTests.Image);
        await using var client = await RespireClient.ConnectAsync(lease.ConnectionString(protocol));
        var view = client.WithKeyPrefix("tenant:");
        using var batch = transaction ? null : view.CreateBatch();
        await using var tx = transaction ? view.CreateTransaction() : null;
        IRespireCommandQueue queue = tx ?? (IRespireCommandQueue)batch!;
        byte[] path = [0, 255, (byte)'a']; byte[] field = [0, 254]; byte[] value = [255, 0, 128];
        var set = queue.PathHashes.Set("binary", path, field, value);
        var get = queue.PathHashes.GetBytes("binary", path, field);
        var all = queue.PathHashes.GetAll("binary", path);
        var longest = queue.PathHashes.LongestPrefix("binary", new byte[] { 0, 255, (byte)'a', 1 });
        var page = queue.PathHashes.ScanPage("binary", prefix: new byte[] { 0, 255 }, count: 1);
        var number = queue.PathHashes.Set<int>("typed", "p", "f", 0);
        var typed = queue.PathHashes.Get<int>("typed", "p", "f");
        var found = queue.PathHashes.TryGet<int>("typed", "p", "f");
        var missing = queue.PathHashes.TryGet<int>("typed", "p", "missing");
        if (tx is not null) await tx.CommitAsync(); else await batch!.ExecuteAsync();
        set.Result.Should().BeTrue();
        get.Result.Should().Equal(value);
        all.Result.Single().Field.Should().Equal(field);
        all.Result.Single().Value.Should().Equal(value);
        longest.Result!.Path.Should().Equal(path);
        page.Result.Entries.Single().Path.Should().Equal(path);
        page.Result.Cursor.Should().Be("0");
        number.Result.Should().BeTrue(); typed.Result.Should().Be(0);
        found.Result.Found.Should().BeTrue(); missing.Result.Found.Should().BeFalse();
        await view.PathHashes.SetAsync<int>("typed", "p", "f", 42);
        (await view.PathHashes.GetAsync<int>("typed", "p", "f")).Should().Be(42);
        (await view.PathHashes.TryGetAsync<int>("typed", "p", "f")).Found.Should().BeTrue();
    }

    [Test]
    public async Task UnsupportedServerPreservesCommandError()
    {
        var lease = await servers.LeaseAsync("redis:7.2-alpine");
        await using var client = await RespireClient.ConnectAsync(lease.ConnectionString(2));
        var action = async () => await client.PathHashes.CountAsync("tree");
        await action.Should().ThrowAsync<RespireServerException>().WithMessage("*unknown command*");
    }
}
