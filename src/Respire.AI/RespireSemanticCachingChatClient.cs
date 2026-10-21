using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Respire.Search;

namespace Respire.AI;

internal sealed class RespireSemanticCachingChatClient : DelegatingChatClient
{
    private static readonly RespireScript Insert = RespireScript.Create("""
        redis.call('HSET', KEYS[1], 'scope', ARGV[1], 'vector', ARGV[2], 'response', ARGV[3])
        redis.call('PEXPIRE', KEYS[1], ARGV[4])
        return 1
        """);

    private readonly IRespireClient _client;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly RespireSemanticCacheOptions _options;
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private readonly string _prefix;
    private readonly string _index;
    private volatile bool _indexReady;

    internal RespireSemanticCachingChatClient(IChatClient inner, IRespireClient client,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator, RespireSemanticCacheOptions options) : base(inner)
    {
        _client = client;
        _embeddingGenerator = embeddingGenerator;
        _options = options;
        _prefix = "respire:semantic:v1:" + Hash(StrictUnicodeJsonEncoder.Utf8.GetBytes(options.CacheName))
            + ":" + options.Dimensions.ToString(CultureInfo.InvariantCulture) + ":entry:";
        _index = _prefix + "index";
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        cancellationToken.ThrowIfCancellationRequested();
        // Materialize once: embedding/scoping and the provider must see the same conversation.
        var conversation = messages.ToList();
        if (ShouldBypass(conversation, options))
            return await InnerClient.GetResponseAsync(conversation, options, cancellationToken).ConfigureAwait(false);

        var userScope = _options.ScopeSelector(conversation, options);
        if (userScope is null)
            return await InnerClient.GetResponseAsync(conversation, options, cancellationToken).ConfigureAwait(false);

        string scope;
        byte[] vector;
        try
        {
            scope = GetScope(conversation, options, userScope);
            var cached = await LookupAsync(conversation[^1].Text, scope, cancellationToken).ConfigureAwait(false);
            if (cached.Response is not null) return cached.Response;
            vector = cached.Vector;
        }
        catch (Exception error) when (_options.FailOpen && error is not OperationCanceledException)
        {
            Trace.TraceWarning("Semantic cache lookup failed: {0}", error);
            cancellationToken.ThrowIfCancellationRequested();
            return await InnerClient.GetResponseAsync(conversation, options, cancellationToken).ConfigureAwait(false);
        }

        var response = await InnerClient.GetResponseAsync(conversation, options, cancellationToken).ConfigureAwait(false);
        if (CanStore(response))
        {
            try
            {
                var json = JsonSerializer.SerializeToUtf8Bytes(response, AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ChatResponse)));
                using var inserted = await _client.Scripts.ExecuteLeaseAsync(Insert,
                    [_prefix + Guid.NewGuid().ToString("N")],
                    [scope, vector, json, _options.TimeToLive.Ticks / TimeSpan.TicksPerMillisecond], cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (_options.FailOpen && error is not OperationCanceledException)
            {
                Trace.TraceWarning("Semantic cache insertion failed: {0}", error);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        return response;
    }

    private async Task<(ChatResponse? Response, byte[] Vector)> LookupAsync(string prompt, string scope, CancellationToken cancellationToken)
    {
        var embeddings = await _embeddingGenerator.GenerateAsync([prompt], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (embeddings.Count != 1)
            throw new InvalidOperationException("The embedding generator must return exactly one embedding.");
        var vector = EncodeVector(embeddings[0].Vector);
        await EnsureIndexAsync(cancellationToken).ConfigureAwait(false);
        RespireSearchResult result;
        try
        {
            result = await SearchAsync().ConfigureAwait(false);
        }
        catch (RespireServerException error) when (IsMissingIndex(error))
        {
            _indexReady = false;
            await EnsureIndexAsync(cancellationToken).ConfigureAwait(false);
            // Retry once only. Repeated index loss and all other errors follow the failure policy.
            result = await SearchAsync().ConfigureAwait(false);
        }

        ValueTask<RespireSearchResult> SearchAsync() => _client.Search.VectorSearchAsync(_index,
            new("vector", vector, 1) { Filter = RespireSearchQueryBuilder.Tag("scope", scope) },
            new() { ReturnFields = ["vector_score"], Limit = (0, 1) }, cancellationToken);

        if (result.Documents.Count == 0) return (null, vector);
        var document = result.Documents[0];
        if (!document.Id.StartsWith(_prefix, StringComparison.Ordinal))
            throw new InvalidOperationException("Semantic search returned a key outside the cache namespace.");
        if (!document.Fields.TryGetValue("vector_score", out var scoreText)
            || !double.TryParse(scoreText, NumberStyles.Float, CultureInfo.InvariantCulture, out var distance)
            || !double.IsFinite(distance))
            throw new InvalidOperationException("Semantic search returned an invalid cosine distance.");
        if (distance > _options.DistanceThreshold) return (null, vector);
        // Re-read the hash so an entry that expires between Search and retrieval is a miss.
        var json = await _client.Hashes.GetBytesAsync(document.DocumentKey, "response", cancellationToken).ConfigureAwait(false);
        if (json is null) return (null, vector);
        return ((ChatResponse?)JsonSerializer.Deserialize(json, AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ChatResponse)))
            ?? throw new InvalidOperationException("The cached chat response is null."), vector);
    }

    // DelegatingChatClient forwards streaming directly; it never reads, embeds or populates the cache.

    private bool ShouldBypass(List<ChatMessage> messages, ChatOptions? options)
    {
        if (options?.AdditionalProperties?.TryGetValue(RespireSemanticCacheOptions.DisableCacheKey, out var disabled) == true && disabled is true
            || _options.ShouldBypass?.Invoke(messages, options) == true)
            return true;
        if (messages.Count == 0 || messages[^1].Role != ChatRole.User || string.IsNullOrWhiteSpace(messages[^1].Text))
            return true;
        if (messages.Any(message => !IsPlainText(message))
            || messages[^1].Contents.Any(content => content.AdditionalProperties is { Count: > 0 }
                || content.Annotations is { Count: > 0 }))
            return true;
        return options is not null && (options.GetType() != typeof(ChatOptions)
            || options.Tools is { Count: > 0 } || options.RawRepresentationFactory is not null
            || options.ConversationId is not null || options.ContinuationToken is not null
            || options.AllowBackgroundResponses == true);
    }

    private static bool IsPlainText(ChatMessage message)
        => message.GetType() == typeof(ChatMessage) && message.RawRepresentation is null
            && message.Contents.All(content => content.GetType() == typeof(TextContent) && content.RawRepresentation is null);

    private static bool CanStore(ChatResponse response)
        => response.GetType() == typeof(ChatResponse) && response.ConversationId is null && response.ContinuationToken is null
            && response.Messages.Count > 0 && response.Messages.All(message => message.Role == ChatRole.Assistant
                && message.GetType() == typeof(ChatMessage)
                && message.Contents.All(content => content.GetType() == typeof(TextContent)));

    private string GetScope(List<ChatMessage> messages, ChatOptions? options, string userScope)
    {
        var scopedOptions = options?.Clone() ?? new ChatOptions();
        if (scopedOptions.AdditionalProperties is { } properties)
        {
            scopedOptions.AdditionalProperties = new(properties);
            scopedOptions.AdditionalProperties.Remove(RespireSemanticCacheOptions.DisableCacheKey);
            if (scopedOptions.AdditionalProperties.Count == 0) scopedOptions.AdditionalProperties = null;
        }
        // JSON arrays keep each boundary unambiguous. Include the exact conversation history,
        // provider identity and options, but replace only the final user text with semantic matching.
        var metadata = InnerClient.GetService(typeof(ChatClientMetadata)) as ChatClientMetadata;
        var embeddingMetadata = _embeddingGenerator.GetService(typeof(EmbeddingGeneratorMetadata)) as EmbeddingGeneratorMetadata;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new() { Encoder = StrictUnicodeJsonEncoder.Instance }))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(userScope);
            writer.WriteStringValue(metadata?.ProviderName);
            writer.WriteStringValue(metadata?.ProviderUri?.ToString());
            writer.WriteStringValue(metadata?.DefaultModelId);
            writer.WriteStringValue(embeddingMetadata?.ProviderName);
            writer.WriteStringValue(embeddingMetadata?.ProviderUri?.ToString());
            writer.WriteStringValue(embeddingMetadata?.DefaultModelId);
            JsonSerializer.Serialize(writer, scopedOptions, AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ChatOptions)));
            foreach (var message in messages.Take(messages.Count - 1))
                JsonSerializer.Serialize(writer, message, AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ChatMessage)));
            var finalMessage = messages[^1].Clone();
            finalMessage.Contents = [];
            JsonSerializer.Serialize(writer, finalMessage, AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ChatMessage)));
            writer.WriteEndArray();
        }
        return Hash(stream.ToArray());
    }

    private byte[] EncodeVector(ReadOnlyMemory<float> vector)
    {
        if (vector.Length != _options.Dimensions)
            throw new InvalidOperationException("The embedding dimensions do not match the semantic cache options.");
        double norm = 0;
        foreach (var value in vector.Span)
        {
            if (!float.IsFinite(value)) throw new InvalidOperationException("Embeddings must contain finite values.");
            norm += (double)value * value;
        }
        if (norm == 0) throw new InvalidOperationException("Cosine embeddings must have a nonzero norm.");
        return MemoryMarshal.AsBytes(vector.Span).ToArray();
    }

    private async Task EnsureIndexAsync(CancellationToken cancellationToken)
    {
        if (_indexReady) return;
        await _indexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_indexReady) return;
            try
            {
                await _client.Search.CreateIndexAsync(_index, new()
                {
                    Prefixes = [_prefix],
                    Fields = [new("scope", RespireSearchFieldType.Tag) { CaseSensitive = true },
                        new("vector", RespireSearchFieldType.Vector)
                        {
                            Vector = new(RespireSearchVectorAlgorithm.Flat, RespireSearchVectorType.Float32,
                                _options.Dimensions, RespireSearchDistanceMetric.Cosine),
                        }],
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (RespireServerException)
            {
                // Verify the index exists instead of depending on the server's duplicate-index wording.
                await _client.Search.GetIndexInfoAsync(_index, cancellationToken).ConfigureAwait(false);
            }
            _indexReady = true;
        }
        finally { _indexGate.Release(); }
    }

    private static bool IsMissingIndex(RespireServerException error)
        => error.Message.Contains("Unknown Index name", StringComparison.OrdinalIgnoreCase)
            || error.Code == "SEARCH_INDEX_NOT_FOUND"
            || error.Message.Contains("no such index", StringComparison.OrdinalIgnoreCase);

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    protected override void Dispose(bool disposing)
    {
        if (disposing) _indexGate.Dispose();
        base.Dispose(disposing);
    }
}
