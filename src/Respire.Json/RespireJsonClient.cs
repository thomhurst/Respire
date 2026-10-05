using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Respire.Protocol;

namespace Respire.Json;

/// <summary>Typed RedisJSON operations using caller-supplied System.Text.Json metadata.</summary>
/// <remarks>
/// <para>
/// The metadata overloads do not use reflection and are suitable for Native AOT; the packaged Native AOT
/// smoke test publishes and runs them. The caller owns the underlying Respire client. RedisJSON key
/// arguments receive the client's configured key prefix. JSON.MGET and JSON.MSET keys must share a Redis
/// Cluster slot; Respire rejects mixed slots before sending the command.
/// </para>
/// <para>
/// The default <see cref="RespireJsonPath"/> is the legacy root path <c>.</c>, which returns one value.
/// Paths that start with <c>$</c> and Redis 8.10 projection expressions use JSONPath array replies.
/// </para>
/// </remarks>
public sealed class RespireJsonClient
{
    private readonly IRespireJsonCommands _commands;
    private readonly IRespireJsonModifierCommands _modifiers;

    /// <summary>Creates RedisJSON operations over an existing Respire client.</summary>
    public RespireJsonClient(IRespireClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _commands = new IRespireJsonCommandsImplementation(client);
        _modifiers = new IRespireJsonModifierCommandsImplementation(client);
    }

    /// <summary>Low-level source-generated commands for the documented RedisJSON command set.</summary>
    public IRespireJsonCommands Commands => _commands;

    /// <summary>Applies an RFC 7396 merge patch using caller-supplied serialization metadata.</summary>
    /// <remarks>
    /// Object members set to JSON null are removed; arrays and other non-object values replace the
    /// matching value. The metadata controls property names, converters, and null serialization.
    /// Argument and serialization failures are reported through the returned task.
    /// </remarks>
    public async ValueTask MergeAsync<T>(
        RespireKey key,
        T patch,
        JsonTypeInfo<T> jsonTypeInfo,
        RespireJsonPath path = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        var utf8Json = JsonSerializer.SerializeToUtf8Bytes(patch, jsonTypeInfo);
        using var result = await _commands.MergeAsync(key, path.Value, utf8Json, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets one typed JSON value.</summary>
    /// <remarks>
    /// <para>
    /// <see cref="RespireJsonValue{T}.Found"/> is false when the key does not exist or when a JSONPath
    /// matches nothing; the two cases are not distinguished. A stored JSON <c>null</c> returns
    /// <c>Found = true</c> with a default <see cref="RespireJsonValue{T}.Value"/>, even when
    /// <typeparamref name="T"/> is a non-nullable reference type.
    /// </para>
    /// <para>
    /// With the default legacy path, a missing path inside an existing document is reported by Redis as a
    /// server error rather than <c>Found = false</c>. Use a <c>$</c> path for a not-found result instead.
    /// To read several paths at once, use <see cref="GetJsonAsync"/> with <see cref="RespireJsonGetOptions.Paths"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// A JSONPath (<c>$</c>) path matched more than one value. Use <see cref="GetManyAsync{T}"/> for paths
    /// that can match several values, such as wildcards.
    /// </exception>
    public async ValueTask<RespireJsonValue<T>> GetAsync<T>(
        RespireKey key,
        JsonTypeInfo<T> jsonTypeInfo,
        RespireJsonPath path = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        using var result = await _commands.GetPathAsync(key, path.Value, cancellationToken).ConfigureAwait(false);
        var values = DeserializeValues(result, path, jsonTypeInfo);
        if (values.Length > 1)
            throw new InvalidOperationException("The JSONPath matched multiple values; use GetManyAsync.");
        return values.Length == 0 ? default : values[0];
    }

    /// <summary>Gets every value matched by one path.</summary>
    /// <remarks>
    /// Returns an empty array when the key does not exist or a JSONPath matches nothing. A legacy path
    /// returns at most one value.
    /// </remarks>
    public async ValueTask<RespireJsonValue<T>[]> GetManyAsync<T>(
        RespireKey key,
        JsonTypeInfo<T> jsonTypeInfo,
        RespireJsonPath path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        using var result = await _commands.GetPathAsync(key, path.Value, cancellationToken).ConfigureAwait(false);
        return DeserializeValues(result, path, jsonTypeInfo);
    }

    /// <summary>Gets formatted JSON text for one or more paths, or null when the key does not exist.</summary>
    /// <remarks>
    /// Redis rejects requests that mix legacy and JSONPath paths; that error surfaces as a
    /// <see cref="RespireServerException"/>.
    /// </remarks>
    public async ValueTask<string?> GetJsonAsync(
        RespireKey key,
        RespireJsonGetOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var result = await _commands.GetAsync(
            key, (options ?? new RespireJsonGetOptions()).ToArguments(), cancellationToken).ConfigureAwait(false);
        return result.IsNull ? null : result.AsString();
    }

    /// <summary>Serializes and sets a typed document using caller-supplied metadata.</summary>
    /// <remarks>
    /// The value is serialized directly to UTF-8. A null reference is stored as the JSON literal <c>null</c>.
    /// </remarks>
    /// <returns>False when <see cref="RespireJsonSetCondition.Nx"/> or <see cref="RespireJsonSetCondition.Xx"/> rejected the write.</returns>
    /// <remarks>Argument and serialization failures are reported through the returned task, not thrown synchronously.</remarks>
    public async ValueTask<bool> SetAsync<T>(
        RespireKey key,
        T value,
        JsonTypeInfo<T> jsonTypeInfo,
        RespireJsonPath path = default,
        RespireJsonSetCondition condition = RespireJsonSetCondition.None,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        var token = ConditionToken(condition);
        var utf8Json = JsonSerializer.SerializeToUtf8Bytes(value, jsonTypeInfo);
        return await SetCoreAsync(key, utf8Json, path, token, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sets pre-serialized JSON text.</summary>
    /// <remarks>
    /// The text <c>"null"</c> is valid JSON and stores a JSON null. Apart from rejecting empty or whitespace-only
    /// text, the JSON is not validated on the client: Redis rejects invalid JSON with a
    /// <see cref="RespireServerException"/>. Argument failures are reported through the returned task.
    /// </remarks>
    /// <returns>False when <see cref="RespireJsonSetCondition.Nx"/> or <see cref="RespireJsonSetCondition.Xx"/> rejected the write.</returns>
    public async ValueTask<bool> SetJsonAsync(
        RespireKey key,
        string json,
        RespireJsonPath path = default,
        RespireJsonSetCondition condition = RespireJsonSetCondition.None,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var token = ConditionToken(condition);
        return await SetCoreAsync(key, json, path, token, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sets pre-serialized UTF-8 JSON without a UTF-16 round trip.</summary>
    /// <remarks>
    /// The JSON is not validated on the client: Redis rejects invalid JSON with a
    /// <see cref="RespireServerException"/>. Argument failures are reported through the returned task.
    /// </remarks>
    /// <returns>False when <see cref="RespireJsonSetCondition.Nx"/> or <see cref="RespireJsonSetCondition.Xx"/> rejected the write.</returns>
    public async ValueTask<bool> SetJsonAsync(
        RespireKey key,
        ReadOnlyMemory<byte> utf8Json,
        RespireJsonPath path = default,
        RespireJsonSetCondition condition = RespireJsonSetCondition.None,
        CancellationToken cancellationToken = default)
    {
        if (utf8Json.IsEmpty) throw new ArgumentException("JSON must not be empty.", nameof(utf8Json));
        var token = ConditionToken(condition);
        return await SetCoreAsync(key, utf8Json, path, token, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets one path from several keys. All keys must share a Cluster slot.</summary>
    /// <returns>
    /// One entry per key, in order. An entry is null when its key does not exist. Otherwise it holds the
    /// values matched by <paramref name="path"/>: one value for a legacy path, and every match (possibly
    /// none) for a JSONPath.
    /// </returns>
    public async ValueTask<RespireJsonValue<T>[]?[]> MultiGetAsync<T>(
        IReadOnlyList<RespireKey> keys,
        JsonTypeInfo<T> jsonTypeInfo,
        RespireJsonPath path = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        if (keys.Count == 0) throw new ArgumentException("At least one key is required.", nameof(keys));
        // Generated commands only read the array, so an array input is passed through without a copy.
        if (keys is not RespireKey[] keyArray)
        {
            keyArray = new RespireKey[keys.Count];
            for (var index = 0; index < keyArray.Length; index++) keyArray[index] = keys[index];
        }

        using var result = await _commands.MultiGetAsync(keyArray, path.Value, cancellationToken).ConfigureAwait(false);
        var values = new RespireJsonValue<T>[]?[result.Count];
        for (var index = 0; index < values.Length; index++)
        {
            var item = result[index];
            values[index] = item.IsNull ? null : DeserializeJsonText(item.AsSpan(), path, jsonTypeInfo);
        }
        return values;
    }

    /// <summary>Serializes and atomically writes multiple key/path/value triples with JSON.MSET.</summary>
    /// <remarks>
    /// Every entry is serialized before anything is sent, so a serialization failure writes nothing.
    /// JSON.MSET has no conditional form and replies OK or an error, so the method has no result.
    /// </remarks>
    public async ValueTask MultiSetAsync<T>(
        IReadOnlyList<RespireJsonSetEntry<T>> entries,
        JsonTypeInfo<T> jsonTypeInfo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        if (entries.Count == 0) throw new ArgumentException("At least one entry is required.", nameof(entries));
        var arguments = new RespireValue[checked(entries.Count * 3)];
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            arguments[index * 3] = entry.Key;
            arguments[index * 3 + 1] = entry.Path.Value;
            arguments[index * 3 + 2] = JsonSerializer.SerializeToUtf8Bytes(entry.Value, jsonTypeInfo);
        }
        using var result = await _commands.MultiSetAsync(arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes a document or path and returns the number of deleted values.</summary>
    public async ValueTask<long> DeleteAsync(
        RespireKey key, RespireJsonPath path = default, CancellationToken cancellationToken = default)
    {
        using var result = await _commands.DeleteAsync(key, path.Value, cancellationToken).ConfigureAwait(false);
        return result.AsInteger();
    }

    /// <summary>Deletes a document or path using JSON.FORGET.</summary>
    public async ValueTask<long> ForgetAsync(
        RespireKey key, RespireJsonPath path = default, CancellationToken cancellationToken = default)
    {
        using var result = await _commands.ForgetAsync(key, path.Value, cancellationToken).ConfigureAwait(false);
        return result.AsInteger();
    }

    /// <summary>Reports the memory used by the values at a path, in bytes, with JSON.DEBUG MEMORY.</summary>
    /// <returns>One size for a legacy path, or one size per JSONPath match.</returns>
    public async ValueTask<long[]> GetMemoryUsageAsync(
        RespireKey key, RespireJsonPath path = default, CancellationToken cancellationToken = default)
    {
        using var result = await _modifiers.DebugAsync("MEMORY", key, path.Value, cancellationToken).ConfigureAwait(false);
        if (result.IsNull) return [];
        if (result.Type is not (RespDataType.Array or RespDataType.Set))
            return [result.AsInteger()];
        var sizes = new long[result.Count];
        for (var index = 0; index < sizes.Length; index++) sizes[index] = result[index].AsInteger();
        return sizes;
    }

    private async ValueTask<bool> SetCoreAsync(
        RespireKey key, RespireValue json, RespireJsonPath path, string? condition, CancellationToken cancellationToken)
    {
        using var result = condition is null
            ? await _commands.SetAsync(key, path.Value, json, cancellationToken).ConfigureAwait(false)
            : await _modifiers.SetConditionalAsync(key, path.Value, json, condition, cancellationToken).ConfigureAwait(false);
        // NX and XX rejections are the only null replies; unconditional writes reply OK or fail.
        return !result.IsNull;
    }

    private static string? ConditionToken(RespireJsonSetCondition condition) => condition switch
    {
        RespireJsonSetCondition.None => null,
        RespireJsonSetCondition.Nx => "NX",
        RespireJsonSetCondition.Xx => "XX",
        _ => throw new ArgumentOutOfRangeException(nameof(condition)),
    };

    private static RespireJsonValue<T>[] DeserializeValues<T>(
        RespireResult result, RespireJsonPath path, JsonTypeInfo<T> jsonTypeInfo)
        => result.IsNull ? [] : DeserializeJsonText(result.AsSpan(), path, jsonTypeInfo);

    // System.Text.Json uses this depth when JsonSerializerOptions.MaxDepth is left at 0.
    private const int DefaultMaxDepth = 64;

    // Reads the borrowed reply bytes in one pass: no byte[] copy and no intermediate JsonDocument.
    private static RespireJsonValue<T>[] DeserializeJsonText<T>(
        ReadOnlySpan<byte> json, RespireJsonPath path, JsonTypeInfo<T> jsonTypeInfo)
    {
        if (!path.UsesJsonPath)
            return [new RespireJsonValue<T>(true, JsonSerializer.Deserialize(json, jsonTypeInfo))];

        // The reply wraps the matches in an array, which uses one depth level on top of the caller's limit.
        var maxDepth = jsonTypeInfo.Options.MaxDepth;
        var reader = new Utf8JsonReader(json, new JsonReaderOptions
        {
            MaxDepth = (int)Math.Min((long)(maxDepth == 0 ? DefaultMaxDepth : maxDepth) + 1, int.MaxValue),
        });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("RedisJSON returned a non-array response for a JSONPath request.");

        // The match count is unknown until the end of the array, so matches are collected in a pooled buffer
        // and copied once into an exactly sized result.
        var pool = ArrayPool<RespireJsonValue<T>>.Shared;
        var buffer = pool.Rent(4);
        var count = 0;
        try
        {
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (count == buffer.Length)
                {
                    var larger = pool.Rent(checked(count * 2));
                    buffer.AsSpan(0, count).CopyTo(larger);
                    ReturnBuffer(pool, buffer);
                    buffer = larger;
                }
                buffer[count++] = new RespireJsonValue<T>(true, JsonSerializer.Deserialize(ref reader, jsonTypeInfo));
            }
            if (reader.TokenType != JsonTokenType.EndArray)
                throw new JsonException("RedisJSON returned an incomplete JSONPath array.");
            return buffer.AsSpan(0, count).ToArray();
        }
        finally
        {
            ReturnBuffer(pool, buffer);
        }
    }

    private static void ReturnBuffer<T>(ArrayPool<RespireJsonValue<T>> pool, RespireJsonValue<T>[] buffer)
        => pool.Return(buffer, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());
}
