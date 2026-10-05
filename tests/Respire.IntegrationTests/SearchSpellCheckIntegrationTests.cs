using FluentAssertions;
using Respire.Search;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.PerTestSession)]
public class SearchSpellCheckIntegrationTests(ModernRedisTestContainer fixture)
{
    [ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.Keyed, Key = TestConstraints.ClientCacheServer)]
    public required ModernRedisTestContainer CacheServer { get; init; }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task DictionariesChangeSuggestionsAndExclusions(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var search = client.Search;
        var index = "spell:" + Guid.NewGuid().ToString("N");
        var dictionary = index + ":words";
        var excluded = index + ":excluded";
        await search.CreateIndexAsync(index, new()
        { Prefixes = [index + ":doc:"], Fields = [new("title", RespireSearchFieldType.Text) { NoStem = true }] });
        try
        {
            (await search.DumpDictionaryAsync(dictionary)).Should().BeEmpty();
            (await search.DeleteDictionaryTermsAsync(dictionary, ["hello"])).Should().Be(0);
            (await search.AddDictionaryTermsAsync(dictionary, ["hello", "help", "hello"])).Should().Be(2);
            (await search.AddDictionaryTermsAsync(dictionary, ["hello"])).Should().Be(0);
            var owned = await search.DumpDictionaryAsync(dictionary);
            owned.Should().BeEquivalentTo(["hello", "help"]);
            var basic = await search.SpellCheckAsync(index, "helo");
            basic.Should().ContainSingle().Which.Suggestions.Should().BeEmpty();

            foreach (var dialect in new[] { 1, 2, 3, 4 })
            {
                var corrections = await search.SpellCheckAsync(index, "helo", new()
                { Distance = 1, IncludeDictionaries = [dictionary], Dialect = dialect });
                corrections.Should().ContainSingle().Which.Term.Should().Be("helo");
                corrections[0].Suggestions.Select(s => s.Term).Should().BeEquivalentTo(["hello", "help"]);
                corrections[0].Suggestions.Should().OnlyContain(s => s.Score == 0);
            }
            await search.AddDictionaryTermsAsync(excluded, ["helo"]);
            (await search.SpellCheckAsync(index, "helo", new()
            { IncludeDictionaries = [dictionary], ExcludeDictionaries = [excluded], Distance = 4, Dialect = 2 })).Should().BeEmpty();
            (await search.DeleteDictionaryTermsAsync(dictionary, ["hello", "missing", "hello"])).Should().Be(1);
            (await search.DumpDictionaryAsync(dictionary)).Should().Equal("help");
            (await search.SpellCheckAsync(index, "helo", new() { IncludeDictionaries = [dictionary] }))
                .Should().ContainSingle().Which.Suggestions.Should().ContainSingle().Which.Term.Should().Be("help");
            owned.Should().BeEquivalentTo(["hello", "help"]);

            await client.Hashes.SetAsync(index + ":doc:1", ("title", "hello"));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while ((await search.SearchAsync(index, new(RespireSearchExpression.FromRaw("hello")), deadline.Token)).Total != 1)
                await Task.Delay(20, deadline.Token);
            (await search.SpellCheckAsync(index, "hello")).Should().BeEmpty();
            var indexed = await search.SpellCheckAsync(index, "helo", new() { IncludeDictionaries = [dictionary] });
            indexed.Single().Suggestions.Single(s => s.Term == "hello").Score.Should().Be(1);
            indexed.Single().Suggestions.Single(s => s.Term == "help").Score.Should().Be(0);
            Func<Task> missingIndex = () => search.SpellCheckAsync(index + ":missing", "helo").AsTask();
            await missingIndex.Should().ThrowAsync<RespireServerException>();
            Func<Task> missingDictionary = () => search.SpellCheckAsync(index, "helo", new() { IncludeDictionaries = [index + ":missing"] }).AsTask();
            await missingDictionary.Should().ThrowAsync<RespireServerException>();
            (await search.DeleteDictionaryTermsAsync(dictionary, ["help"])).Should().Be(1);
            (await search.DumpDictionaryAsync(dictionary)).Should().BeEmpty();
        }
        finally
        {
            await search.DeleteDictionaryTermsAsync(dictionary, ["hello", "help"]);
            await search.DeleteDictionaryTermsAsync(excluded, ["helo"]);
            await search.DropIndexAsync(index, deleteDocuments: true);
        }
    }

    [Test, NotInParallel(TestConstraints.ClientCacheHits)]
    public async Task ReadsRetainCacheAndDictionaryWritesInvalidateIt()
    {
        await using var client = await ConnectAsync(3, CacheServer, cache: true);
        var index = "spell:" + Guid.NewGuid().ToString("N");
        var dictionary = index + ":words";
        var cached = index + ":cached";
        await client.Search.CreateIndexAsync(index, new()
        { Prefixes = [index + ":doc:"], Fields = [new("title", RespireSearchFieldType.Text)] });
        try
        {
            await client.SetAsync(cached, "cached");
            await client.Search.AddDictionaryTermsAsync(dictionary, ["hello"]);
            Func<Task>[] reads = [() => client.Search.DumpDictionaryAsync(dictionary).AsTask(),
                () => client.Search.SpellCheckAsync(index, "helo", new() { IncludeDictionaries = [dictionary] }).AsTask()];
            foreach (var read in reads)
            {
                await client.GetStringAsync(cached);
                var hits = client.ClientSideCache!.GetStatistics().Hits;
                await read();
                (await client.GetStringAsync(cached)).Should().Be("cached");
                client.ClientSideCache.GetStatistics().Hits.Should().Be(hits + 1);
            }
            Func<Task>[] mutations = [() => client.Search.AddDictionaryTermsAsync(dictionary, ["help"]).AsTask(),
                () => client.Search.DeleteDictionaryTermsAsync(dictionary, ["hello"]).AsTask()];
            foreach (var mutation in mutations)
            {
                await client.GetStringAsync(cached);
                var hits = client.ClientSideCache!.GetStatistics().Hits;
                await mutation();
                (await client.GetStringAsync(cached)).Should().Be("cached");
                client.ClientSideCache.GetStatistics().Hits.Should().Be(hits);
            }
        }
        finally
        {
            await client.Search.DeleteDictionaryTermsAsync(dictionary, ["hello", "help"]);
            await client.Search.DropIndexAsync(index, true);
            await client.Keys.DeleteAsync(cached);
        }
    }

    private ValueTask<RespireClient> ConnectAsync(int protocol, ModernRedisTestContainer? server = null, bool cache = false)
        => RespireClient.ConnectAsync(RespireOptions.Parse((server ?? fixture).ConnectionString) with
        { Protocol = (RespProtocol)protocol, ClientSideCache = cache ? new() : null });
}
