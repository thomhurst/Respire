using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Respire.IntegrationTests;
using Respire.Search;
using TUnit.Core;

namespace Respire.AI.Tests;

[ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.PerTestSession)]
public class SemanticCacheTests(ModernRedisTestContainer fixture)
{
    [Test, Arguments(2), Arguments(3)]
    public async Task MissHitAndDistantMiss(int protocol)
    {
        await using var scenario = await Create(protocol);
        var original = await scenario.Ask("near one");
        var hit = await scenario.Ask("near two");
        hit.Text.Should().Be(original.Text);
        hit.ResponseId.Should().Be(original.ResponseId);
        hit.ModelId.Should().Be(original.ModelId);
        hit.FinishReason.Should().Be(ChatFinishReason.Stop);
        hit.Usage!.InputTokenCount.Should().Be(7);
        hit.Should().NotBeSameAs(original);
        (await scenario.Ask("far")).Text.Should().NotBe(original.Text);
        scenario.Provider.Calls.Should().Be(2);
        scenario.Embeddings.Calls.Should().Be(3);
        scenario.Embeddings.LastInput.Should().Be("far");
    }

    [Test, Arguments(2, 1.0, true), Arguments(3, 1.0, true), Arguments(2, 0.999, false), Arguments(3, 0.999, false)]
    public async Task ThresholdIsInclusive(int protocol, double threshold, bool expectedHit)
    {
        await using var scenario = await Create(protocol, new() { Dimensions = 2, DistanceThreshold = threshold });
        var first = await scenario.Ask("near");
        var second = await scenario.Ask("far"); // orthogonal unit vectors have cosine distance exactly 1
        (second.Text == first.Text).Should().Be(expectedHit);
        scenario.Provider.Calls.Should().Be(expectedHit ? 1 : 2);
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task ExactScopeModelSystemInstructionsOptionsAndHistoryIsolation(int protocol)
    {
        var user = "User | {é}";
        await using var scenario = await Create(protocol, new() { Dimensions = 2, ScopeSelector = (_, _) => user });
        var messages = new List<ChatMessage> { new(ChatRole.System, "policy A"), new(ChatRole.User, "near") };
        var options = new ChatOptions { ModelId = "model A", Temperature = 0 };
        var first = await scenario.Chat.GetResponseAsync(messages, options);
        (await scenario.Chat.GetResponseAsync(messages, options)).Text.Should().Be(first.Text);
        user = "user | {é}"; // ordinal, case-sensitive user scope
        await scenario.Chat.GetResponseAsync(messages, options);
        user = "User | {é}";
        options.ModelId = "model a";
        await scenario.Chat.GetResponseAsync(messages, options);
        options.ModelId = "model A";
        messages[0] = new(ChatRole.System, "policy a");
        await scenario.Chat.GetResponseAsync(messages, options);
        messages[0] = new(ChatRole.System, "policy A");
        options.Instructions = "extra policy";
        await scenario.Chat.GetResponseAsync(messages, options);
        options.Instructions = null;
        options.Temperature = 1;
        await scenario.Chat.GetResponseAsync(messages, options);
        options.Temperature = 0;
        messages.Insert(1, new(ChatRole.Assistant, "earlier answer"));
        await scenario.Chat.GetResponseAsync(messages, options);
        scenario.Provider.Calls.Should().Be(7);
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task OptOutBypassesReadsAndWritesWithoutChangingOptions(int protocol)
    {
        await using var scenario = await Create(protocol);
        var original = await scenario.Ask("near");
        var options = new ChatOptions { AdditionalProperties = new() { [RespireSemanticCacheOptions.DisableCacheKey] = true } };
        (await scenario.Ask("near", options)).Text.Should().NotBe(original.Text);
        (await scenario.Ask("far", options)).Text.Should().NotBe(original.Text);
        options.AdditionalProperties[RespireSemanticCacheOptions.DisableCacheKey].Should().Be(true);
        scenario.Embeddings.Calls.Should().Be(1);
        (await scenario.Ask("far")).Text.Should().Be("response 4"); // opted-out far response was not stored
        options.AdditionalProperties[RespireSemanticCacheOptions.DisableCacheKey] = false;
        (await scenario.Ask("near", options)).Text.Should().Be(original.Text);
        options.AdditionalProperties.Should().ContainKey(RespireSemanticCacheOptions.DisableCacheKey);
        scenario.Provider.Calls.Should().Be(4);
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task SelectorAndPredicateCanBypass(int protocol)
    {
        var bypass = true;
        string? scope = "user";
        await using var scenario = await Create(protocol, new()
        {
            Dimensions = 2, ShouldBypass = (_, _) => bypass, ScopeSelector = (_, _) => scope,
        });
        await scenario.Ask("near");
        bypass = false;
        scope = null;
        await scenario.Ask("near");
        scenario.Embeddings.Calls.Should().Be(0);
        scope = "user";
        await scenario.Ask("near");
        await scenario.Ask("near");
        scenario.Provider.Calls.Should().Be(3);
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task EntryHasAtomicAbsoluteExpiry(int protocol)
    {
        await using var scenario = await Create(protocol, new() { Dimensions = 2, TimeToLive = TimeSpan.FromMinutes(5), FailOpen = false });
        var first = await scenario.Ask("near");
        var keys = new List<RespireKey>();
        await foreach (var key in scenario.Redis.Keys.ScanAsync(match: scenario.Prefix + "*")) keys.Add(key);
        keys.Should().ContainSingle();
        foreach (var key in keys)
        {
            using var ttl = await scenario.Redis.ExecuteAsync("PTTL", [key]);
            ttl.AsInteger().Should().BeInRange(1, 300000);
        }
        using var expiry = await scenario.Redis.ExecuteAsync("PEXPIRETIME", [keys[0]]);
        (await scenario.Ask("near")).Text.Should().Be(first.Text);
        using (var afterHit = await scenario.Redis.ExecuteAsync("PEXPIRETIME", [keys[0]]))
            afterHit.AsInteger().Should().Be(expiry.AsInteger());
        scenario.Provider.Calls.Should().Be(1);
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task ExpiredEntryCannotSatisfyQuery(int protocol)
    {
        await using var scenario = await Create(protocol, new() { Dimensions = 2, TimeToLive = TimeSpan.FromSeconds(2), FailOpen = false });
        var first = await scenario.Ask("near");
        var keys = new List<RespireKey>();
        await foreach (var key in scenario.Redis.Keys.ScanAsync(match: scenario.Prefix + "*")) keys.Add(key);
        keys.Should().ContainSingle();
        using var expiry = await scenario.Redis.ExecuteAsync("PEXPIRETIME", [keys[0]]);
        var expiresAt = expiry.AsInteger();
        expiresAt.Should().BePositive("the cache must install an expiry when it writes the entry");

        // Redis owns the expiry clock. A fixed host delay can finish before the container
        // reaches this deadline. Observe TIME only: PTTL/HGET could passively expire the
        // hash before Search gets to see it, hiding a stale-result bug.
        var wait = Stopwatch.StartNew();
        long serverTime;
        while ((serverTime = (await scenario.Redis.Server.TimeAsync()).ToUnixTimeMilliseconds()) <= expiresAt)
        {
            wait.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10),
                $"Redis TIME must pass the entry deadline {expiresAt}; last server time was {serverTime}");
            await Task.Delay(25);
        }

        // Do not retry the cache lookup: even one stale response after expiry is a failure.
        (await scenario.Ask("near")).Text.Should().NotBe(first.Text,
            $"Redis TIME {serverTime} has passed the entry deadline {expiresAt}");
        scenario.Provider.Calls.Should().Be(2);
        (await scenario.Redis.Hashes.GetBytesAsync(keys[0], "response")).Should().BeNull();
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task StreamingNeverReadsOrPopulatesCache(int protocol)
    {
        await using var scenario = await Create(protocol);
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in scenario.Chat.GetStreamingResponseAsync([new(ChatRole.User, "near")])) updates.Add(update);
        updates.Single().Text.Should().Be("stream");
        scenario.Embeddings.Calls.Should().Be(0);
        await scenario.Ask("near");
        await foreach (var _ in scenario.Chat.GetStreamingResponseAsync([new(ChatRole.User, "near")])) { }
        scenario.Provider.Calls.Should().Be(1);
        scenario.Provider.StreamCalls.Should().Be(2);
        scenario.Embeddings.Calls.Should().Be(1);
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task StatefulToolsAndNonTextRequestsBypass(int protocol)
    {
        await using var scenario = await Create(protocol);
        await scenario.Ask("near", new() { ConversationId = "conversation" });
        await scenario.Ask("near", new() { AllowBackgroundResponses = true });
        await scenario.Ask("near", new() { RawRepresentationFactory = _ => new object() });
        await scenario.Ask("near", new() { Tools = [AIFunctionFactory.Create(() => "tool")] });
        await scenario.Chat.GetResponseAsync([new(ChatRole.User, [new DataContent(new byte[] { 1 }, "image/png")])]);
        await scenario.Chat.GetResponseAsync([new(ChatRole.Assistant, "near")]);
        scenario.Embeddings.Calls.Should().Be(0);
        scenario.Provider.Calls.Should().Be(6);
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task StatefulAndToolResponsesAreNotStored(int protocol)
    {
        await using var scenario = await Create(protocol);
        scenario.Provider.ResponseFactory = () => new(new ChatMessage(ChatRole.Assistant, "text")) { ConversationId = "state" };
        await scenario.Ask("near");
        await scenario.Ask("near");
        scenario.Provider.ResponseFactory = () => new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call", "tool")]));
        await scenario.Ask("near");
        await scenario.Ask("near");
        scenario.Provider.Calls.Should().Be(4);
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task FinalPromptAnnotationsBypassReadsAndWrites(int protocol)
    {
        await using var scenario = await Create(protocol);
        var original = await scenario.Ask("near");
        foreach (var prompt in new[] { "near paraphrase", "far" })
        {
            var content = new TextContent(prompt) { Annotations = [new AIAnnotation()] };
            var response = await scenario.Chat.GetResponseAsync([new(ChatRole.User, [content])]);
            response.Text.Should().NotBe(original.Text);
            content.Annotations.Should().ContainSingle();
        }
        scenario.Embeddings.Calls.Should().Be(1);
        (await scenario.Ask("near")).Text.Should().Be(original.Text);
        (await scenario.Ask("far")).Text.Should().Be("response 4");
        scenario.Provider.Calls.Should().Be(4);
    }

    [Test, Arguments(2, true), Arguments(3, true), Arguments(2, false), Arguments(3, false)]
    public async Task DroppedIndexIsRecreated(int protocol, bool deleteDocuments)
    {
        await using var scenario = await Create(protocol);
        var original = await scenario.Ask("near");
        var keys = new List<RespireKey>();
        await foreach (var key in scenario.Redis.Keys.ScanAsync(match: scenario.Prefix + "*")) keys.Add(key);
        await scenario.Redis.Search.DropIndexAsync(scenario.Prefix + "index", deleteDocuments);
        var response = await scenario.Ask("near");
        if (deleteDocuments) response.Text.Should().NotBe(original.Text);
        else
        {
            // Existing hashes are scanned asynchronously into the recreated index, so the first search can miss.
            (await scenario.Redis.Hashes.GetBytesAsync(keys.Single(), "response")).Should().NotBeNull();
            scenario.Provider.Calls.Should().BeInRange(1, 2);
        }
        await scenario.Redis.Search.GetIndexInfoAsync(scenario.Prefix + "index");
        if (deleteDocuments)
        {
            (await scenario.Ask("near paraphrase")).Text.Should().Be(response.Text);
            scenario.Provider.Calls.Should().Be(2);
        }
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task EmbeddingFailureFallsBackWithoutCachingAndCancellationPropagates(int protocol)
    {
        await using var scenario = await Create(protocol);
        scenario.Embeddings.Failure = new InvalidOperationException("embedding failed");
        (await scenario.Ask("near")).Text.Should().Be("response 1");
        scenario.Embeddings.Failure = new OperationCanceledException("embedding cancelled");
        Func<Task> cancelled = () => scenario.Ask("near");
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        scenario.Provider.Calls.Should().Be(1);
        scenario.Embeddings.Failure = null;
        (await scenario.Ask("near")).Text.Should().Be("response 2");
        (await scenario.Ask("near")).Text.Should().Be("response 2");
    }

    [Test, Arguments(2, true), Arguments(3, true), Arguments(2, false), Arguments(3, false)]
    public async Task ResponseSerializationFailureFollowsFailurePolicy(int protocol, bool failOpen)
    {
        await using var scenario = await Create(protocol, new() { Dimensions = 2, FailOpen = failOpen });
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "valid answer"))
        {
            AdditionalProperties = new() { ["unsupported"] = (Action)(() => { }) },
        };
        scenario.Provider.ResponseFactory = () => response;
        if (failOpen) (await scenario.Ask("near")).Should().BeSameAs(response);
        else
        {
            Func<Task> request = () => scenario.Ask("near");
            await request.Should().ThrowAsync<NotSupportedException>();
        }
        scenario.Provider.ResponseFactory = null;
        (await scenario.Ask("near")).Text.Should().Be("response 2");
    }

    [Test, Arguments(2, true, true), Arguments(3, true, true), Arguments(2, false, true), Arguments(3, false, true),
        Arguments(2, true, false), Arguments(3, true, false), Arguments(2, false, false), Arguments(3, false, false)]
    public async Task RedisLookupAndInsertionFailuresFollowFailurePolicy(int protocol, bool failOpen, bool denyLookup)
    {
        await using var admin = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString));
        var username = "semantic-" + Guid.NewGuid().ToString("N");
        using (var created = await admin.ExecuteAsync("ACL", denyLookup
            ? ["SETUSER", username, "on", ">test-password", "~*", "+@all", "-ft.search"]
            : ["SETUSER", username, "on", ">test-password", "~*", "+@all", "-eval", "-evalsha"])) { }
        try
        {
            var redis = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with
            {
                Username = username, Password = "test-password", Protocol = (RespireProtocol)protocol,
            });
            await using var scenario = new Scenario(redis, new()
            {
                Dimensions = 2, FailOpen = failOpen, CacheName = Guid.NewGuid().ToString("N"),
            });
            if (failOpen) (await scenario.Ask("near")).Text.Should().Be("response 1");
            else
            {
                Func<Task> request = () => scenario.Ask("near");
                await request.Should().ThrowAsync<RespireServerException>();
            }
            scenario.Provider.Calls.Should().Be(denyLookup && !failOpen ? 0 : 1);
            var keys = new List<RespireKey>();
            await foreach (var key in admin.Keys.ScanAsync(match: scenario.Prefix + "*")) keys.Add(key);
            keys.Should().BeEmpty();
        }
        finally
        {
            using var removed = await admin.ExecuteAsync("ACL", ["DELUSER", username]);
        }
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task SeparateMiddlewareInstancesShareEntriesAndDoNotOwnDependencies(int protocol)
    {
        await using var scenario = await Create(protocol);
        var original = await scenario.Ask("near");
        var otherProvider = new FakeChatClient();
        using (var other = new ChatClientBuilder(otherProvider).UseRespireSemanticCache(scenario.Redis, scenario.Embeddings, scenario.Options).Build())
            (await other.GetResponseAsync([new(ChatRole.User, "near paraphrase")])).Text.Should().Be(original.Text);
        otherProvider.Calls.Should().Be(0);
        otherProvider.Disposed.Should().BeTrue();
        scenario.Embeddings.Disposed.Should().BeFalse();
        await scenario.Redis.PingAsync();
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task ConcurrentFirstRequestsCreateOneCompatibleIndex(int protocol)
    {
        await using var scenario = await Create(protocol);
        var clients = Enumerable.Range(0, 8).Select(_ => new ChatClientBuilder(scenario.Provider)
            .UseRespireSemanticCache(scenario.Redis, scenario.Embeddings, scenario.Options).Build()).ToArray();
        try
        {
            await Task.WhenAll(clients.Select(chat => chat.GetResponseAsync([new(ChatRole.User, "near")])));
            scenario.Embeddings.Calls.Should().Be(8);
            var calls = scenario.Provider.Calls;
            calls.Should().BeInRange(1, 8);
            await scenario.Ask("near");
            scenario.Provider.Calls.Should().Be(calls);
        }
        finally { foreach (var chat in clients) chat.Dispose(); }
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task InvalidEmbeddingsFailBeforeCallingProvider(int protocol)
    {
        await using var scenario = await Create(protocol, new() { Dimensions = 2, FailOpen = false });
        foreach (var vector in new float[][] { [1], [0, 0], [float.NaN, 0], [float.PositiveInfinity, 0] })
        {
            scenario.Embeddings.Vector = vector;
            Func<Task> request = () => scenario.Ask("near");
            await request.Should().ThrowAsync<InvalidOperationException>();
        }
        scenario.Embeddings.ReturnEmpty = true;
        Func<Task> empty = () => scenario.Ask("near");
        await empty.Should().ThrowAsync<InvalidOperationException>().WithMessage("*exactly one*");
        scenario.Provider.Calls.Should().Be(0);
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task InvalidOptionsAreRejectedAtRegistration(int protocol)
    {
        await using var scenario = await Create(protocol);
        var valid = scenario.Options;
        foreach (var invalid in new[]
        {
            valid with { Dimensions = 0 }, valid with { Dimensions = -1 },
            valid with { DistanceThreshold = -0.1 }, valid with { DistanceThreshold = 2.1 },
            valid with { DistanceThreshold = double.NaN }, valid with { DistanceThreshold = double.PositiveInfinity },
            valid with { TimeToLive = TimeSpan.Zero }, valid with { TimeToLive = TimeSpan.FromTicks(1) },
            valid with { CacheName = " " }, valid with { ScopeSelector = null! },
        })
        {
            Action register = () => new ChatClientBuilder(scenario.Provider).UseRespireSemanticCache(scenario.Redis, scenario.Embeddings, invalid);
            register.Should().Throw<ArgumentException>();
        }
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task ProviderAndEmbeddingMetadataIsolateCache(int protocol)
    {
        await using var scenario = await Create(protocol);
        var first = await scenario.Ask("near");
        var otherProvider = new FakeChatClient { ModelId = "other-model" };
        using var other = new ChatClientBuilder(otherProvider).UseRespireSemanticCache(scenario.Redis, scenario.Embeddings, scenario.Options).Build();
        await other.GetResponseAsync([new(ChatRole.User, "near")]);
        otherProvider.Calls.Should().Be(1);
        scenario.Embeddings.ModelId = "other-embedding";
        (await scenario.Ask("near")).Text.Should().NotBe(first.Text);
        scenario.Provider.Calls.Should().Be(2);
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task RawProviderRepresentationsAreOmittedFromPortableCachedResponse(int protocol)
    {
        await using var scenario = await Create(protocol);
        scenario.Provider.ResponseFactory = () => new(new ChatMessage(ChatRole.Assistant,
            [new TextContent("portable") { RawRepresentation = new object() }]) { RawRepresentation = new object() })
        { RawRepresentation = new object() };
        var first = await scenario.Ask("near");
        var hit = await scenario.Ask("near");
        hit.Text.Should().Be(first.Text);
        hit.RawRepresentation.Should().BeNull();
        hit.Messages[0].RawRepresentation.Should().BeNull();
        hit.Messages[0].Contents[0].RawRepresentation.Should().BeNull();
        scenario.Provider.Calls.Should().Be(1);
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task CancellationAndProviderFailureDoNotPopulateCache(int protocol)
    {
        await using var scenario = await Create(protocol);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Func<Task> request = () => scenario.Chat.GetResponseAsync([new(ChatRole.User, "near")], cancellationToken: cancelled.Token);
        await request.Should().ThrowAsync<OperationCanceledException>();
        scenario.Embeddings.Calls.Should().Be(0);
        scenario.Provider.ResponseFactory = () => throw new InvalidOperationException("provider failed");
        Func<Task> failure = () => scenario.Ask("near");
        await failure.Should().ThrowAsync<InvalidOperationException>().WithMessage("provider failed");
        scenario.Provider.ResponseFactory = null;
        await scenario.Ask("near");
        scenario.Provider.Calls.Should().Be(2);
    }

    private async Task<Scenario> Create(int protocol, RespireSemanticCacheOptions? options = null)
    {
        var redis = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespireProtocol)protocol });
        return new(redis, (options ?? new() { Dimensions = 2 }) with { CacheName = Guid.NewGuid().ToString("N") });
    }

    private sealed class Scenario : IAsyncDisposable
    {
        public RespireClient Redis { get; }
        public RespireSemanticCacheOptions Options { get; }
        public FakeChatClient Provider { get; } = new();
        public FakeEmbeddingGenerator Embeddings { get; } = new();
        public IChatClient Chat { get; }
        public string Prefix { get; }
        public Scenario(RespireClient redis, RespireSemanticCacheOptions options)
        {
            Redis = redis;
            Options = options;
            Prefix = "respire:semantic:v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(options.CacheName))) + ":2:entry:";
            Chat = new ChatClientBuilder(Provider).UseRespireSemanticCache(Redis, Embeddings, Options).Build();
        }
        public Task<ChatResponse> Ask(string prompt, ChatOptions? options = null) => Chat.GetResponseAsync([new(ChatRole.User, prompt)], options);
        public async ValueTask DisposeAsync()
        {
            Chat.Dispose();
            try { await Redis.Search.DropIndexAsync(Prefix + "index", deleteDocuments: true); }
            catch (RespireServerException error) when (error.Message.Contains("Unknown Index name", StringComparison.Ordinal)
                || error.Message.Contains("SEARCH_INDEX_NOT_FOUND", StringComparison.Ordinal)) { }
            finally
            {
                await Redis.DisposeAsync();
                Embeddings.Dispose();
            }
        }
    }

    private sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public int Calls { get; private set; }
        public string? LastInput { get; private set; }
        public float[]? Vector { get; set; }
        public bool Disposed { get; private set; }
        public bool ReturnEmpty { get; set; }
        public Exception? Failure { get; set; }
        public string ModelId { get; set; } = "fake-embedding";
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null) throw Failure;
            Calls++;
            LastInput = values.Single();
            return Task.FromResult(ReturnEmpty ? new GeneratedEmbeddings<Embedding<float>>([])
                : new GeneratedEmbeddings<Embedding<float>>([new(Vector ?? (LastInput.StartsWith("near", StringComparison.Ordinal) ? [1, 0] : [0, 1]))]));
        }
        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(EmbeddingGeneratorMetadata) ? new EmbeddingGeneratorMetadata("fake", defaultModelId: ModelId, defaultModelDimensions: 2) : null;
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeChatClient : IChatClient
    {
        private int _calls;
        public int Calls => _calls;
        public int StreamCalls { get; private set; }
        public bool Disposed { get; private set; }
        public string ModelId { get; set; } = "fake-model";
        public Func<ChatResponse>? ResponseFactory { get; set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var calls = Interlocked.Increment(ref _calls);
            return Task.FromResult(ResponseFactory?.Invoke() ?? new ChatResponse(new ChatMessage(ChatRole.Assistant, "response " + calls))
            {
                ResponseId = "id-" + calls, ModelId = "fake-model", FinishReason = ChatFinishReason.Stop,
                Usage = new() { InputTokenCount = 7, OutputTokenCount = 3 },
            });
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StreamCalls++;
            await Task.CompletedTask;
            yield return new(ChatRole.Assistant, "stream");
        }
        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("fake", defaultModelId: ModelId) : null;
        public void Dispose() => Disposed = true;
    }
}
