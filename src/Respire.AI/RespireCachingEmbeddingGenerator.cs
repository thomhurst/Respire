using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Respire.AI;

internal sealed class RespireCachingEmbeddingGenerator(
    IEmbeddingGenerator<string, Embedding<float>> inner,
    IRespireClient client,
    RespireEmbeddingCacheOptions cacheOptions) : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner)
{
    private const char ReplacementCharacter = '\uFFFD';
    private const byte NoModelId = 0;
    private const byte ModelIdPresent = 1;
    private const byte ModelIdWithReplacementCharacter = 2;

    private static readonly Encoding s_strictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
    private static readonly Meter Meter = new("Respire.AI");
    private static readonly Counter<long> Hits = Meter.CreateCounter<long>("respire.ai.embedding_cache.hits", "{input}");
    private static readonly Counter<long> Misses = Meter.CreateCounter<long>("respire.ai.embedding_cache.misses", "{input}");

    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        cancellationToken.ThrowIfCancellationRequested();
        var inputs = values.ToArray();

        foreach (var input in inputs)
        {
            ArgumentNullException.ThrowIfNull(input);
        }

        if (inputs.Length == 0)
        {
            return new([]);
        }

        var metadata = InnerGenerator.GetService(typeof(EmbeddingGeneratorMetadata)) as EmbeddingGeneratorMetadata;
        // Default dimensions describe only the default model, not an explicitly selected alternative.
        var usesDefaultModel = options?.ModelId is null
            || string.Equals(options.ModelId, metadata?.DefaultModelId, StringComparison.Ordinal);
        var effectiveDimensions = options?.Dimensions ?? (usesDefaultModel ? metadata?.DefaultModelDimensions : null);
        var scope = GetScope(options, metadata, effectiveDimensions);

        if (scope is null)
        {
            var uncached = await InnerGenerator.GenerateAsync(inputs, options, cancellationToken).ConfigureAwait(false);
            ValidateGeneratedEmbeddings(uncached, inputs.Length, effectiveDimensions);
            return uncached;
        }

        var unique = inputs.Distinct(StringComparer.Ordinal).ToArray();
        var keys = unique.ToDictionary(input => input,
            input => (RespireKey)(scope + HashInput(input)), StringComparer.Ordinal);
        var embeddings = new Dictionary<string, Embedding<float>>(StringComparer.Ordinal);

        try
        {
            using var batch = client.CreateBatch();
            var pending = unique.Select(input => batch.Strings.GetBytes(keys[input])).ToArray();
            await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);

            for (var i = 0; i < unique.Length; i++)
            {
                if (pending[i].Result is { } bytes && Decode(bytes, effectiveDimensions) is { } embedding)
                {
                    embeddings.Add(unique[i], embedding);
                }
            }
        }
        catch (Exception error) when (cacheOptions.FailOpen && error is not OperationCanceledException)
        {
            Trace.TraceWarning("Embedding cache lookup failed: {0}", error);
            embeddings.Clear();
            cancellationToken.ThrowIfCancellationRequested();
        }

        var hitCount = inputs.Count(embeddings.ContainsKey);
        Hits.Add(hitCount);
        Misses.Add(inputs.Length - hitCount);
        var misses = unique.Where(input => !embeddings.ContainsKey(input)).ToArray();
        GeneratedEmbeddings<Embedding<float>>? generated = null;

        if (misses.Length > 0)
        {
            generated = await InnerGenerator.GenerateAsync(misses, options, cancellationToken).ConfigureAwait(false);
            ValidateGeneratedEmbeddings(generated, misses.Length, effectiveDimensions);

            for (var i = 0; i < misses.Length; i++)
            {
                embeddings.Add(misses[i], generated[i]);
            }

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var batch = client.CreateBatch();
                var writes = 0;
                var expiry = cacheOptions.TimeToLive is { } ttl ? RespireExpiry.In(ttl) : RespireExpiry.None;

                for (var i = 0; i < misses.Length; i++)
                {
                    var embedding = generated[i];
                    // Skip metadata that cannot round-trip through the cache format.
                    if (embedding.AdditionalProperties is { Count: > 0 }
                        || !EmbeddingCacheIdentity.IsWellFormed(embedding.ModelId))
                    {
                        continue;
                    }

                    _ = batch.Strings.Set(keys[misses[i]], (RespireValue)Encode(embedding), expiry);
                    writes++;
                }

                if (writes > 0)
                {
                    await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (cacheOptions.FailOpen && error is not OperationCanceledException)
            {
                Trace.TraceWarning("Embedding cache insertion failed: {0}", error);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        // Each position owns its vector, including repeated inputs within the same batch.
        cancellationToken.ThrowIfCancellationRequested();
        return new(inputs.Select(input => Copy(embeddings[input])))
        {
            Usage = generated?.Usage,
            AdditionalProperties = generated?.AdditionalProperties,
        };
    }

    private static void ValidateGeneratedEmbeddings(
        GeneratedEmbeddings<Embedding<float>> generated, int inputCount, int? effectiveDimensions)
    {
        if (generated.Count != inputCount)
        {
            throw new InvalidOperationException("The embedding generator must return one embedding per input.");
        }

        foreach (var embedding in generated)
        {
            int length = embedding.Vector.Length;

            if (length == 0 || effectiveDimensions is { } dimensions && length != dimensions)
            {
                throw new InvalidOperationException("The embedding generator returned invalid dimensions.");
            }
        }
    }

    private string? GetScope(EmbeddingGenerationOptions? options, EmbeddingGeneratorMetadata? metadata, int? effectiveDimensions)
    {
        // Unknown options may change the vector in ways that cannot be represented safely in a key.
        if (options is not null && (options.GetType() != typeof(EmbeddingGenerationOptions)
            || options.RawRepresentationFactory is not null || options.AdditionalProperties is { Count: > 0 }))
        {
            return null;
        }

        var providerName = metadata?.ProviderName;
        var providerUri = metadata?.ProviderUri?.ToString();
        var modelId = options?.ModelId ?? metadata?.DefaultModelId;
        // JSON replaces unpaired surrogates, which would alias distinct provider/model identities.
        // Bypass before Redis access while preserving v1 keys for all well-formed identities.
        if (!EmbeddingCacheIdentity.IsWellFormed(providerName) || !EmbeddingCacheIdentity.IsWellFormed(providerUri)
            || !EmbeddingCacheIdentity.IsWellFormed(metadata?.ProviderUri?.OriginalString)
            || !EmbeddingCacheIdentity.IsWellFormed(modelId))
        {
            return null;
        }

        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(cacheOptions.CacheName);
            writer.WriteStringValue(providerName);
            writer.WriteStringValue(providerUri);
            writer.WriteStringValue(modelId);
            writer.WriteStringValue(effectiveDimensions?.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndArray();
        }

        return "respire:embedding:v1:" + Hash(stream.ToArray()) + ":";
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string HashInput(string input)
    {
        // Hash exact UTF-16 code units, including unpaired surrogates. UTF-8 replacement fallback
        // would otherwise map distinct .NET strings to the same key.
        var bytes = new byte[checked(input.Length * sizeof(char))];

        for (var i = 0; i < input.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * sizeof(char)), input[i]);
        }

        return Hash(bytes);
    }

    private static Embedding<float> Copy(Embedding<float> embedding) => new(embedding.Vector.ToArray())
    {
        ModelId = embedding.ModelId,
        CreatedAt = embedding.CreatedAt,
        AdditionalProperties = embedding.AdditionalProperties is { } properties ? new(properties) : null,
    };

    private static byte[] Encode(Embedding<float> embedding)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        if (embedding.ModelId is { } model)
        {
            // Mark literal U+FFFD so readers can distinguish it from loss in legacy entries.
            // Older readers interpret either nonzero tag as model present.
            writer.Write(model.Contains(ReplacementCharacter) ? ModelIdWithReplacementCharacter : ModelIdPresent);
            writer.Write(model);
        }
        else
        {
            writer.Write(NoModelId);
        }

        writer.Write(embedding.CreatedAt.HasValue);

        if (embedding.CreatedAt is { } created)
        {
            writer.Write(created.Ticks);
            writer.Write((short)created.Offset.TotalMinutes);
        }
        // BinaryWriter stores IEEE 754 FLOAT32 values in little-endian order on all platforms.
        writer.Write(embedding.Vector.Length);

        foreach (var value in embedding.Vector.Span)
        {
            writer.Write(value);
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static Embedding<float>? Decode(byte[] bytes, int? dimensions)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(stream, s_strictUtf8);
            byte modelTag = reader.ReadByte();

            if (modelTag > ModelIdWithReplacementCharacter)
            {
                return null;
            }

            string? model = modelTag == NoModelId ? null : reader.ReadString();

            if (modelTag == ModelIdPresent && model?.Contains(ReplacementCharacter) == true)
            {
                return null;
            }

            DateTimeOffset? created = reader.ReadBoolean()
                ? new DateTimeOffset(reader.ReadInt64(), TimeSpan.FromMinutes(reader.ReadInt16())) : null;
            var length = reader.ReadInt32();
            var remaining = stream.Length - stream.Position;

            if (length <= 0 || remaining != (long)length * sizeof(float) || dimensions is { } count && length != count)
            {
                return null;
            }

            var vector = new float[length];

            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] = reader.ReadSingle();
            }

            return new(vector) { ModelId = model, CreatedAt = created };
        }
        catch (Exception error) when (error is IOException or ArgumentException or FormatException)
        {
            // A truncated or foreign entry is a miss and is replaced after successful generation.
            return null;
        }
    }
}
