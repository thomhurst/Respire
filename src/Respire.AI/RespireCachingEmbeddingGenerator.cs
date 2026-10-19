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
    private static readonly Meter Meter = new("Respire.AI");
    private static readonly Counter<long> Hits = Meter.CreateCounter<long>("respire.ai.embedding_cache.hits", "{input}");
    private static readonly Counter<long> Misses = Meter.CreateCounter<long>("respire.ai.embedding_cache.misses", "{input}");

    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        cancellationToken.ThrowIfCancellationRequested();
        var inputs = values.ToArray();
        foreach (var input in inputs) ArgumentNullException.ThrowIfNull(input);
        if (inputs.Length == 0) return new([]);
        // Unknown options may change the vector in ways that cannot be represented safely in a key.
        if (options is not null && (options.GetType() != typeof(EmbeddingGenerationOptions)
            || options.RawRepresentationFactory is not null || options.AdditionalProperties is { Count: > 0 }))
            return await InnerGenerator.GenerateAsync(inputs, options, cancellationToken).ConfigureAwait(false);

        var metadata = InnerGenerator.GetService(typeof(EmbeddingGeneratorMetadata)) as EmbeddingGeneratorMetadata;
        // Default dimensions describe only the default model, not an explicitly selected alternative.
        var usesDefaultModel = options?.ModelId is null
            || string.Equals(options.ModelId, metadata?.DefaultModelId, StringComparison.Ordinal);
        var effectiveDimensions = options?.Dimensions ?? (usesDefaultModel ? metadata?.DefaultModelDimensions : null);
        var scope = GetScope(options, metadata, effectiveDimensions);
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
                    embeddings.Add(unique[i], embedding);
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
            if (generated.Count != misses.Length)
                throw new InvalidOperationException("The embedding generator must return one embedding per input.");
            // Validate the complete response before inserting any part of it.
            foreach (var embedding in generated)
            {
                if (embedding.Vector.Length == 0 || effectiveDimensions is { } dimensions && embedding.Vector.Length != dimensions)
                    throw new InvalidOperationException("The embedding generator returned invalid dimensions.");
            }
            for (var i = 0; i < misses.Length; i++) embeddings.Add(misses[i], generated[i]);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var batch = client.CreateBatch();
                var writes = 0;
                var expiry = cacheOptions.TimeToLive is { } ttl ? RespireExpiry.In(ttl) : RespireExpiry.None;
                for (var i = 0; i < misses.Length; i++)
                {
                    var embedding = generated[i];
                    // Provider-specific properties have no portable binary representation.
                    if (embedding.AdditionalProperties is { Count: > 0 }) continue;
                    _ = batch.Strings.Set(keys[misses[i]], (RespireValue)Encode(embedding), expiry);
                    writes++;
                }
                if (writes > 0) await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
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

    private string GetScope(EmbeddingGenerationOptions? options, EmbeddingGeneratorMetadata? metadata, int? effectiveDimensions)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(cacheOptions.CacheName);
            writer.WriteStringValue(metadata?.ProviderName);
            writer.WriteStringValue(metadata?.ProviderUri?.ToString());
            writer.WriteStringValue(options?.ModelId ?? metadata?.DefaultModelId);
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
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * sizeof(char)), input[i]);
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
        writer.Write(embedding.ModelId is not null);
        if (embedding.ModelId is { } model) writer.Write(model);
        writer.Write(embedding.CreatedAt.HasValue);
        if (embedding.CreatedAt is { } created)
        {
            writer.Write(created.Ticks);
            writer.Write((short)created.Offset.TotalMinutes);
        }
        // BinaryWriter stores IEEE 754 FLOAT32 values in little-endian order on all platforms.
        writer.Write(embedding.Vector.Length);
        foreach (var value in embedding.Vector.Span) writer.Write(value);
        writer.Flush();
        return stream.ToArray();
    }

    private static Embedding<float>? Decode(byte[] bytes, int? dimensions)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            var model = reader.ReadBoolean() ? reader.ReadString() : null;
            DateTimeOffset? created = reader.ReadBoolean()
                ? new DateTimeOffset(reader.ReadInt64(), TimeSpan.FromMinutes(reader.ReadInt16())) : null;
            var length = reader.ReadInt32();
            var remaining = stream.Length - stream.Position;
            if (length <= 0 || remaining != (long)length * sizeof(float) || dimensions is { } count && length != count)
                return null;
            var vector = new float[length];
            for (var i = 0; i < vector.Length; i++) vector[i] = reader.ReadSingle();
            return new(vector) { ModelId = model, CreatedAt = created };
        }
        catch (Exception error) when (error is IOException or ArgumentException or FormatException)
        {
            // A truncated or foreign entry is a miss and is replaced after successful generation.
            return null;
        }
    }
}
