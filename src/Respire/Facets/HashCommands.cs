using Respire.Commands;

namespace Respire;

/// <summary>Hash (field → value map) commands.</summary>
public interface IHashCommands
{
    /// <summary>Sets one field. Returns true when the field was newly created. Redis: HSET.</summary>
    ValueTask<bool> SetAsync(RespireKey key, string field, RespireValue value, CancellationToken cancellationToken = default);

    /// <summary>Sets many fields in one round trip; returns how many were newly created. Redis: HSET.</summary>
    ValueTask<long> SetAsync(RespireKey key, params ReadOnlySpan<(string Field, RespireValue Value)> fields);

    /// <summary>Gets a field as a string, or null when missing. Redis: HGET.</summary>
    ValueTask<string?> GetStringAsync(RespireKey key, string field, CancellationToken cancellationToken = default);

    /// <summary>Gets a field deserialized as <typeparamref name="T"/>. Redis: HGET.</summary>
    ValueTask<T?> GetAsync<T>(RespireKey key, string field, CancellationToken cancellationToken = default);

    /// <summary>Gets a field's raw bytes, or null when missing. Redis: HGET.</summary>
    ValueTask<byte[]?> GetBytesAsync(RespireKey key, string field, CancellationToken cancellationToken = default);

    /// <summary>Gets many fields in one round trip; missing fields yield null. Redis: HMGET.</summary>
    ValueTask<string?[]> GetManyAsync(RespireKey key, params ReadOnlySpan<string> fields);

    /// <summary>The whole hash as a dictionary. Redis: HGETALL.</summary>
    ValueTask<Dictionary<string, string>> GetAllAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Deletes fields; returns how many existed. Redis: HDEL.</summary>
    ValueTask<long> DeleteAsync(RespireKey key, params ReadOnlySpan<string> fields);

    /// <summary>Whether the field exists. Redis: HEXISTS.</summary>
    ValueTask<bool> ExistsAsync(RespireKey key, string field, CancellationToken cancellationToken = default);

    /// <summary>Number of fields in the hash. Redis: HLEN.</summary>
    ValueTask<long> LengthAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Atomically adds to a numeric field and returns the new value. Redis: HINCRBY.</summary>
    ValueTask<long> IncrementAsync(RespireKey key, string field, long by = 1, CancellationToken cancellationToken = default);

    /// <summary>Atomically adds a floating-point delta to a field. Redis: HINCRBYFLOAT.</summary>
    ValueTask<double> IncrementAsync(RespireKey key, string field, double by, CancellationToken cancellationToken = default);

    /// <summary>All field names. Redis: HKEYS.</summary>
    ValueTask<string[]> FieldsAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>All values. Redis: HVALS.</summary>
    ValueTask<string[]> ValuesAsync(RespireKey key, CancellationToken cancellationToken = default);
}

internal sealed class HashCommands(RespireClient client) : IHashCommands
{
    public ValueTask<bool> SetAsync(RespireKey key, string field, RespireValue value, CancellationToken cancellationToken = default)
        => client.FlagAsync("HSET", new Cmd3(Verbs.HSet, client.Key(in key), field, value), cancellationToken);

    public ValueTask<long> SetAsync(RespireKey key, params ReadOnlySpan<(string Field, RespireValue Value)> fields)
    {
        var args = new RespireValue[fields.Length * 2];
        for (var i = 0; i < fields.Length; i++)
        {
            args[i * 2] = fields[i].Field;
            args[i * 2 + 1] = fields[i].Value;
        }

        return client.IntegerAsync("HSET", new Cmd1N(Verbs.HSet, client.Key(in key), args), CancellationToken.None);
    }

    public ValueTask<string?> GetStringAsync(RespireKey key, string field, CancellationToken cancellationToken = default)
        => client.StringOrNullAsync("HGET", new Cmd2(Verbs.HGet, client.Key(in key), field), cancellationToken);

    public ValueTask<T?> GetAsync<T>(RespireKey key, string field, CancellationToken cancellationToken = default)
        => client.DeserializeAsync<T, Cmd2>("HGET", new Cmd2(Verbs.HGet, client.Key(in key), field), cancellationToken);

    public ValueTask<byte[]?> GetBytesAsync(RespireKey key, string field, CancellationToken cancellationToken = default)
        => client.BytesOrNullAsync("HGET", new Cmd2(Verbs.HGet, client.Key(in key), field), cancellationToken);

    public ValueTask<string?[]> GetManyAsync(RespireKey key, params ReadOnlySpan<string> fields)
        => client.NullableStringArrayAsync(
            "HMGET", new Cmd1N(Verbs.HMGet, client.Key(in key), ToValues(fields)), CancellationToken.None);

    public ValueTask<Dictionary<string, string>> GetAllAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.StringMapAsync("HGETALL", new Cmd1(Verbs.HGetAll, client.Key(in key)), cancellationToken);

    public ValueTask<long> DeleteAsync(RespireKey key, params ReadOnlySpan<string> fields)
        => client.IntegerAsync("HDEL", new Cmd1N(Verbs.HDel, client.Key(in key), ToValues(fields)), CancellationToken.None);

    public ValueTask<bool> ExistsAsync(RespireKey key, string field, CancellationToken cancellationToken = default)
        => client.FlagAsync("HEXISTS", new Cmd2(Verbs.HExists, client.Key(in key), field), cancellationToken);

    public ValueTask<long> LengthAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.IntegerAsync("HLEN", new Cmd1(Verbs.HLen, client.Key(in key)), cancellationToken);

    public ValueTask<long> IncrementAsync(RespireKey key, string field, long by = 1, CancellationToken cancellationToken = default)
        => client.IntegerAsync("HINCRBY", new Cmd3(Verbs.HIncrBy, client.Key(in key), field, by), cancellationToken);

    public ValueTask<double> IncrementAsync(RespireKey key, string field, double by, CancellationToken cancellationToken = default)
        => client.DoubleAsync("HINCRBYFLOAT", new Cmd3(Verbs.HIncrByFloat, client.Key(in key), field, by), cancellationToken);

    public ValueTask<string[]> FieldsAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.StringArrayAsync("HKEYS", new Cmd1(Verbs.HKeys, client.Key(in key)), cancellationToken);

    public ValueTask<string[]> ValuesAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.StringArrayAsync("HVALS", new Cmd1(Verbs.HVals, client.Key(in key)), cancellationToken);

    private static RespireValue[] ToValues(ReadOnlySpan<string> items)
    {
        var values = new RespireValue[items.Length];
        for (var i = 0; i < items.Length; i++)
        {
            values[i] = items[i];
        }

        return values;
    }
}
