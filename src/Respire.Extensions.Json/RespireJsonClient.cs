using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Respire.Extensions.Json;

/// <summary>Typed RedisJSON operations using caller-supplied System.Text.Json metadata.</summary>
/// <remarks>
/// The metadata overloads do not use reflection and are suitable for Native AOT. The caller owns
/// the underlying Respire client. RedisJSON key arguments receive the client's configured key
/// prefix. JSON.MGET and JSON.MSET keys must share a Redis Cluster slot.
/// </remarks>
public sealed class RespireJsonClient
{
    private readonly IRespireJsonCommands _commands;

    /// <summary>Creates RedisJSON operations over an existing Respire client.</summary>
    public RespireJsonClient(IRespireClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _commands = new IRespireJsonCommandsImplementation(client);
    }

    /// <summary>Low-level source-generated commands for the documented RedisJSON command set.</summary>
    public IRespireJsonCommands Commands => _commands;

    /// <summary>Gets one typed JSON value. JSONPath responses must contain at most one match.</summary>
    public async ValueTask<RespireJsonValue<T>> GetAsync<T>(
        RespireKey key,
        JsonTypeInfo<T> jsonTypeInfo,
        RespireJsonPath path = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        using var result = await _commands.GetAsync(key, [path.Value], cancellationToken).ConfigureAwait(false);
        var values = DeserializeValues(result, path, jsonTypeInfo);
        if (values.Length > 1)
            throw new InvalidOperationException("The JSONPath matched multiple values; use GetManyAsync.");
        return values.Length == 0 ? default : values[0];
    }

    /// <summary>Gets every value matched by one path.</summary>
    public async ValueTask<RespireJsonValue<T>[]> GetManyAsync<T>(
        RespireKey key,
        JsonTypeInfo<T> jsonTypeInfo,
        RespireJsonPath path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        using var result = await _commands.GetAsync(key, [path.Value], cancellationToken).ConfigureAwait(false);
        return DeserializeValues(result, path, jsonTypeInfo);
    }

    /// <summary>Gets formatted JSON text for one or more paths.</summary>
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
    public async ValueTask<bool> SetAsync<T>(
        RespireKey key,
        T value,
        JsonTypeInfo<T> jsonTypeInfo,
        RespireJsonPath path = default,
        RespireJsonSetCondition condition = RespireJsonSetCondition.None,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        var json = JsonSerializer.Serialize(value, jsonTypeInfo);
        return await SetJsonAsync(key, json, path, condition, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sets pre-serialized JSON. Returns false when NX or XX rejects the write.</summary>
    public async ValueTask<bool> SetJsonAsync(
        RespireKey key,
        string json,
        RespireJsonPath path = default,
        RespireJsonSetCondition condition = RespireJsonSetCondition.None,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var result = condition switch
        {
            RespireJsonSetCondition.None => await _commands.SetAsync(key, path.Value, json, cancellationToken)
                .ConfigureAwait(false),
            RespireJsonSetCondition.Nx => await _commands.SetConditionalAsync(key, path.Value, json, "NX", cancellationToken)
                .ConfigureAwait(false),
            RespireJsonSetCondition.Xx => await _commands.SetConditionalAsync(key, path.Value, json, "XX", cancellationToken)
                .ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(condition)),
        };
        return !result.IsNull;
    }

    /// <summary>Gets one path from several keys. All keys must share a Cluster slot.</summary>
    public async ValueTask<RespireJsonValue<T>[][]> MultiGetAsync<T>(
        IReadOnlyList<RespireKey> keys,
        JsonTypeInfo<T> jsonTypeInfo,
        RespireJsonPath path = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        if (keys.Count == 0) throw new ArgumentException("At least one key is required.", nameof(keys));
        var keyArray = new RespireKey[keys.Count];
        for (var index = 0; index < keyArray.Length; index++) keyArray[index] = keys[index];

        using var result = await _commands.MultiGetAsync(keyArray, path.Value, cancellationToken).ConfigureAwait(false);
        var values = new RespireJsonValue<T>[result.Count][];
        for (var index = 0; index < values.Length; index++)
        {
            var item = result[index];
            values[index] = item.IsNull ? [] : DeserializeJsonText(item.AsBytes(), path, jsonTypeInfo);
        }
        return values;
    }

    /// <summary>Serializes and writes multiple key/path/value triples.</summary>
    public async ValueTask<bool> MultiSetAsync<T>(
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
            arguments[index * 3 + 2] = JsonSerializer.Serialize(entry.Value, jsonTypeInfo);
        }
        using var result = await _commands.MultiSetAsync(arguments, cancellationToken).ConfigureAwait(false);
        return !result.IsNull;
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

    private static RespireJsonValue<T>[] DeserializeValues<T>(
        RespireResult result, RespireJsonPath path, JsonTypeInfo<T> jsonTypeInfo)
        => result.IsNull ? [] : DeserializeJsonText(result.AsBytes(), path, jsonTypeInfo);

    private static RespireJsonValue<T>[] DeserializeJsonText<T>(
        byte[] json, RespireJsonPath path, JsonTypeInfo<T> jsonTypeInfo)
    {
        if (!path.UsesJsonPath)
            return [new RespireJsonValue<T>(true, JsonSerializer.Deserialize(json, jsonTypeInfo))];

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("RedisJSON returned a non-array response for a JSONPath request.");
        var values = new RespireJsonValue<T>[document.RootElement.GetArrayLength()];
        var index = 0;
        foreach (var element in document.RootElement.EnumerateArray())
            values[index++] = new RespireJsonValue<T>(true, element.Deserialize(jsonTypeInfo));
        return values;
    }
}
