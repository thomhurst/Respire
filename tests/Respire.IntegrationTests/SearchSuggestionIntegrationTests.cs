using FluentAssertions;
using Respire.Search;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.PerTestSession)]
public class SearchSuggestionIntegrationTests(ModernRedisTestContainer fixture)
{
    [ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.Keyed, Key = TestConstraints.ClientCacheServer)]
    public required ModernRedisTestContainer CacheServer { get; init; }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task DictionaryLifecycleOptionsAndOwnedResults(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var search = client.Search;
        var key = "suggest:" + Guid.NewGuid().ToString("N");
        try
        {
            (await search.GetSuggestionCountAsync(key)).Should().Be(0);
            (await search.GetSuggestionsAsync(key, "hel")).Should().BeEmpty();
            (await search.DeleteSuggestionAsync(key, "hello")).Should().BeFalse();
            (await search.AddSuggestionAsync(key, "hello", 2, new() { Payload = new byte[] { 0, 255, 128 } })).Should().Be(1);
            (await search.AddSuggestionAsync(key, "help", 1)).Should().Be(2);
            (await search.GetSuggestionCountAsync(key)).Should().Be(2);
            foreach (var scores in new[] { false, true })
            foreach (var payloads in new[] { false, true })
            {
                var options = new RespireSearchSuggestionOptions { WithScores = scores, WithPayloads = payloads };
                var entries = await search.GetSuggestionsAsync(key, "hel", options);
                entries.Select(entry => entry.Text).Should().BeEquivalentTo(["hello", "help"]);
                var hello = entries.Single(entry => entry.Text == "hello");
                hello.Score.HasValue.Should().Be(scores);
                hello.Payload.HasValue.Should().Be(payloads);
                if (payloads) hello.Payload!.Value.ToArray().Should().Equal(0, 255, 128);
                entries.Single(entry => entry.Text == "help").Payload.Should().BeNull();
            }
            var retained = (await search.GetSuggestionsAsync(key, "hel", new() { WithScores = true, WithPayloads = true }))
                .Single(entry => entry.Text == "hello");
            (await search.AddSuggestionAsync(key, "hello", 3, new() { Increment = true })).Should().Be(2);
            var incremented = (await search.GetSuggestionsAsync(key, "hel", new() { WithScores = true }))
                .Single(entry => entry.Text == "hello");
            incremented.Score.Should().BeGreaterThan(retained.Score!.Value);
            (await search.AddSuggestionAsync(key, "hello", 1)).Should().Be(2);
            var replaced = (await search.GetSuggestionsAsync(key, "hel", new() { WithScores = true }))
                .Single(entry => entry.Text == "hello");
            replaced.Score.Should().BeLessThan(incremented.Score!.Value);
            (await search.GetSuggestionsAsync(key, "hez")).Should().BeEmpty();
            (await search.GetSuggestionsAsync(key, "hez", new() { Fuzzy = true })).Should().NotBeEmpty();
            (await search.GetSuggestionsAsync(key, "", new() { Max = 1 })).Should().ContainSingle();
            (await search.DeleteSuggestionAsync(key, "hello")).Should().BeTrue();
            (await search.DeleteSuggestionAsync(key, "hello")).Should().BeFalse();
            (await search.GetSuggestionCountAsync(key)).Should().Be(1);
            retained.Text.Should().Be("hello");
            retained.Payload!.Value.ToArray().Should().Equal(0, 255, 128);
            // The dictionary can be managed as a Redis key and needs no FT.CREATE index.
            (await client.Keys.DeleteAsync(key)).Should().Be(1);
            (await search.GetSuggestionsAsync(key, "")).Should().BeEmpty();
        }
        finally { await client.Keys.DeleteAsync(key); }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task PrefixCancellationAndServerErrorsPreserveDictionary(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var key = "suggest:" + Guid.NewGuid().ToString("N");
        var wrongType = key + ":wrong";
        await using var prefixed = client.WithKeyPrefix("tenant:");
        Func<RespireSearchClient, string, CancellationToken, Task>[] calls = [
            (s, k, token) => s.AddSuggestionAsync(k, "hello", 1, cancellationToken: token).AsTask(),
            (s, k, token) => s.DeleteSuggestionAsync(k, "hello", token).AsTask(),
            (s, k, token) => s.GetSuggestionCountAsync(k, token).AsTask(),
            (s, k, token) => s.GetSuggestionsAsync(k, "hel", cancellationToken: token).AsTask()];
        try
        {
            await client.Search.AddSuggestionAsync(key, "hello", 1);
            await client.SetAsync(wrongType, "string");
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            foreach (var call in calls)
            {
                Func<Task> invalidPrefix = () => call(prefixed.Search, key, default);
                await invalidPrefix.Should().ThrowAsync<NotSupportedException>();
                Func<Task> cancel = () => call(client.Search, key, canceled.Token);
                var error = await cancel.Should().ThrowAsync<OperationCanceledException>();
                error.Which.CancellationToken.Should().Be(canceled.Token);
                Func<Task> invalidType = () => call(client.Search, wrongType, default);
                await invalidType.Should().ThrowAsync<RespireServerException>();
            }
            (await client.Search.GetSuggestionCountAsync(key)).Should().Be(1);
        }
        finally { await client.Keys.DeleteAsync(key, wrongType); }
    }

    [Test, NotInParallel(TestConstraints.ClientCacheHits)]
    public async Task ReadsRetainCacheAndMutationsInvalidateIt()
    {
        await using var client = await ConnectAsync(3, cache: true, CacheServer);
        var key = "suggest:" + Guid.NewGuid().ToString("N");
        var cached = key + ":cached";
        try
        {
            await client.SetAsync(cached, "cached");
            await client.Search.AddSuggestionAsync(key, "hello", 1);
            Func<Task>[] reads = [() => client.Search.GetSuggestionsAsync(key, "").AsTask(), () => client.Search.GetSuggestionCountAsync(key).AsTask()];
            foreach (var read in reads)
            {
                await client.GetStringAsync(cached);
                var hits = client.ClientSideCache!.GetStatistics().Hits;
                await read();
                (await client.GetStringAsync(cached)).Should().Be("cached");
                client.ClientSideCache.GetStatistics().Hits.Should().Be(hits + 1);
            }
            Func<Task>[] mutations = [() => client.Search.AddSuggestionAsync(key, "help", 2).AsTask(), () => client.Search.DeleteSuggestionAsync(key, "hello").AsTask()];
            foreach (var mutation in mutations)
            {
                await client.GetStringAsync(cached);
                var hits = client.ClientSideCache!.GetStatistics().Hits;
                await mutation();
                (await client.GetStringAsync(cached)).Should().Be("cached");
                client.ClientSideCache.GetStatistics().Hits.Should().Be(hits);
            }
        }
        finally { await client.Keys.DeleteAsync(key, cached); }
    }

    private ValueTask<RespireClient> ConnectAsync(int protocol, bool cache = false, StandaloneRedisTestContainer? server = null)
        => RespireClient.ConnectAsync(RespireOptions.Parse((server ?? fixture).ConnectionString) with
        { Protocol = (RespProtocol)protocol, ClientSideCache = cache ? new() : null });
}
