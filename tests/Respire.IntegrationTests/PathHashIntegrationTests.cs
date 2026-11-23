using System.Text;
using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ClassDataSource<VersionedServerFixture>(Shared = SharedType.PerTestSession)]
public class PathHashIntegrationTests(VersionedServerFixture servers)
{
    private const string ReleasedImage = "valkey/valkey:9.2.0-alpine";

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task TraversalOnReleasedValkeyReturnsOwnedPaths(int protocol)
    {
        var lease = await servers.LeaseAsync(ReleasedImage);
        await using var client = await RespireClient.ConnectAsync(lease.ConnectionString(protocol));
        var view = client.WithKeyPrefix("tenant:");
        var paths = view.PathHashes;
        byte[] prefix = [0xff, 0];
        byte[] firstPath = [0xff, 0, 1];
        byte[] lastPath = [0xff, 0, 2];
        var missing = await paths.ScanPageAsync("tree");
        missing.IsComplete.Should().BeTrue();
        missing.Entries.Should().BeEmpty();

        await paths.SetAsync("tree", lastPath, "field", "last");
        await paths.SetAsync("tree", firstPath, "field", "first");
        await paths.SetAsync("tree", "outside", "field", "outside");

        var first = await paths.ScanPageAsync("tree", prefix: prefix, count: 1);
        first.IsComplete.Should().BeFalse();
        var last = await paths.ScanPageAsync("tree", first.Cursor, prefix, count: 1);
        last.IsComplete.Should().BeTrue();
        first.Entries.Single().Path.Should().Equal(firstPath);
        last.Entries.Single().Path.Should().Equal(lastPath);
        first.Entries.Single().Fields.Should().BeEmpty();
        (await paths.GetStringAsync("tree", firstPath, "field")).Should().Be("first");

        var entries = new List<RespirePathHashEntry>();

        await foreach (var entry in paths.ScanAsync("tree", prefix: prefix, count: 1))
        {
            entries.Add(entry);
        }

        entries.Select(entry => entry.Path).Should().BeEquivalentTo(new[] { firstPath, lastPath },
            options => options.WithStrictOrdering());
        entries.Should().OnlyContain(entry => entry.Fields.Length == 0);

        using var batch = view.CreateBatch();
        var batchPage = batch.PathHashes.ScanPage("tree", prefix: prefix, count: 1);
        await batch.ExecuteAsync();
        batchPage.Result.Entries.Single().Path.Should().Equal(firstPath);
        batchPage.Result.Entries.Single().Fields.Should().BeEmpty();

        await using var transaction = view.CreateTransaction();
        var transactionPage = transaction.PathHashes.ScanPage("tree", batchPage.Result.Cursor, prefix, count: 1);
        await transaction.CommitAsync();
        transactionPage.Result.IsComplete.Should().BeTrue();
        transactionPage.Result.Entries.Single().Path.Should().Equal(lastPath);
        transactionPage.Result.Entries.Single().Fields.Should().BeEmpty();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ExactOperationsAndAncestorLookup(int protocol)
    {
        var lease = await servers.LeaseAsync(ReleasedImage);
        await using var client = await RespireClient.ConnectAsync(lease.ConnectionString(protocol));
        var paths = client.WithKeyPrefix("tenant:").PathHashes;
        (await paths.CountAsync("tree")).Should().Be(0);
        (await paths.GetAllAsync("tree", "missing")).Should().BeEmpty();
        (await paths.LongestPrefixAsync("tree", "missing")).Should().BeNull();
        (await paths.PrefixesAsync("tree", "missing")).Should().BeEmpty();
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
        (await paths.ExistsAsync("tree", "missing")).Should().BeFalse();
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
        var deepest = await paths.PrefixesAsync("tree", "abcd", count: 2);
        deepest.Select(entry => Encoding.UTF8.GetString(entry.Path)).Should().Equal("ab", "abc");
        (await paths.RemoveAsync("tree", "a", ["g"])).Should().Be(1);
        (await paths.RemoveAsync("tree", "z")).Should().Be(1);
        (await paths.RemovePrefixAsync("tree", "ab")).Should().Be(2);
        (await paths.ExistsAsync("tree", "a")).Should().BeTrue();
        (await paths.ExistsAsync("tree", "ab")).Should().BeFalse();
        (await paths.RemovePrefixAsync("tree", "")).Should().Be(2);
        (await paths.CountAsync("tree")).Should().Be(0);
        (await paths.RemovePrefixAsync("tree", "")).Should().Be(0);
    }

    [Test]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task DeferredTypedAndBinaryValues(int protocol, bool transaction)
    {
        var lease = await servers.LeaseAsync(ReleasedImage);
        await using var client = await RespireClient.ConnectAsync(lease.ConnectionString(protocol));
        var view = client.WithKeyPrefix("tenant:");
        using var batch = transaction ? null : view.CreateBatch();
        await using var tx = transaction ? view.CreateTransaction() : null;
        IRespireCommandQueue queue = tx ?? (IRespireCommandQueue)batch!;
        byte[] path = [0, 255, (byte)'a'];
        byte[] field = [0, 254];
        byte[] value = [255, 0, 128];
        byte[] ancestor = [0];
        var set = queue.PathHashes.Set("binary", path, field, value);
        queue.PathHashes.Set("binary", ancestor, field, "ancestor");
        var get = queue.PathHashes.GetBytes("binary", path, field);
        var all = queue.PathHashes.GetAll("binary", path);
        var longest = queue.PathHashes.LongestPrefix("binary", new byte[] { 0, 255, (byte)'a', 1 });
        var noMatch = queue.PathHashes.LongestPrefix("binary", new byte[] { 1, 254 });
        var prefixes = queue.PathHashes.Prefixes("binary", new byte[] { 0, 255, (byte)'a', 1 });
        var count = queue.PathHashes.Count("binary");
        var exists = queue.PathHashes.Exists("binary", path);
        var missingPath = queue.PathHashes.Exists("binary", new byte[] { 0, 255 });
        var removed = queue.PathHashes.RemovePrefix("binary", new byte[] { 0, 255 });
        var remaining = queue.PathHashes.Count("binary");
        var number = queue.PathHashes.Set<int>("typed", "p", "f", 0);
        var typed = queue.PathHashes.Get<int>("typed", "p", "f");
        var found = queue.PathHashes.TryGet<int>("typed", "p", "f");
        var missing = queue.PathHashes.TryGet<int>("typed", "p", "missing");

        if (tx is not null)
        {
            await tx.CommitAsync();
        }
        else
        {
            await batch!.ExecuteAsync();
        }

        set.Result.Should().BeTrue();
        get.Result.Should().Equal(value);
        all.Result.Single().Field.Should().Equal(field);
        all.Result.Single().Value.Should().Equal(value);
        longest.Result!.Path.Should().Equal(path);
        noMatch.Result.Should().BeNull();
        prefixes.Result.Select(entry => entry.Path).Should().BeEquivalentTo(new[] { ancestor, path },
            options => options.WithStrictOrdering());
        count.Result.Should().Be(2);
        exists.Result.Should().BeTrue();
        missingPath.Result.Should().BeFalse();
        removed.Result.Should().Be(1);
        remaining.Result.Should().Be(1);
        number.Result.Should().BeTrue();
        typed.Result.Should().Be(0);
        found.Result.Found.Should().BeTrue();
        missing.Result.Found.Should().BeFalse();
        await view.PathHashes.SetAsync<int>("typed", "p", "f", 42);
        (await view.PathHashes.GetAsync<int>("typed", "p", "f")).Should().Be(42);
        (await view.PathHashes.TryGetAsync<int>("typed", "p", "f")).Found.Should().BeTrue();
    }

    [Test]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task TraversalReturnsCompleteQueuedPage(int protocol, bool transaction)
    {
        var lease = await servers.LeaseAsync(ReleasedImage);
        await using var client = await RespireClient.ConnectAsync(lease.ConnectionString(protocol));
        var paths = client.PathHashes;
        byte[] prefix = [0, 255];
        byte[] firstPath = [0, 255, (byte)'a'];
        byte[] lastPath = [0, 255, (byte)'b'];
        await paths.SetAsync("tree", firstPath, "f", "first");
        await paths.SetAsync("tree", lastPath, "f", "second");
        await paths.SetAsync("tree", "outside", "f", "other");

        using var batch = transaction ? null : client.CreateBatch();
        await using var tx = transaction ? client.CreateTransaction() : null;
        IRespireCommandQueue queue = tx ?? (IRespireCommandQueue)batch!;
        var page = queue.PathHashes.ScanPage("tree", prefix: prefix, count: 10);

        if (tx is not null)
        {
            await tx.CommitAsync();
        }
        else
        {
            await batch!.ExecuteAsync();
        }

        page.Result.Entries.Select(entry => entry.Path).Should().BeEquivalentTo(new[] { firstPath, lastPath },
            options => options.WithStrictOrdering());
        page.Result.IsComplete.Should().BeTrue();
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
