using StackExchange.Redis;

namespace Respire.StackExchangeCompat;

internal abstract partial class CompatDatabaseAsync
{
    public async Task<RedisResult> ScriptEvaluateAsync(string script, RedisKey[]? keys, RedisValue[]? values, CommandFlags flags)
    {
        ArgumentNullException.ThrowIfNull(script);
        // A hexadecimal SHA1 string has the same EVALSHA meaning as in StackExchange.Redis.
        var command = script.Length == 40 && script.All(char.IsAsciiHexDigit)
            ? RespireCommands.Scripting.EVALSHA : RespireCommands.Scripting.EVAL;
        RedisValue[] arguments = [script, keys?.Length ?? 0, .. (keys ?? []).Select(Key), .. (values ?? []).Select(Value)];
        var result = await Send(command, arguments, flags, static reply => reply).ConfigureAwait(false);
        return result ?? RedisResult.Create(RedisValue.Null);
    }

    public Task<RedisValue[]> SortedSetRangeByValueAsync(RedisKey key, RedisValue min, RedisValue max, Exclude exclude,
        long skip, long take, CommandFlags flags)
        => SortedSetRangeByValueAsync(key, min, max, exclude, Order.Ascending, skip, take, flags);

    public Task<RedisValue[]> SortedSetRangeByValueAsync(RedisKey key, RedisValue min, RedisValue max, Exclude exclude,
        Order order, long skip, long take, CommandFlags flags)
    {
        if (order is not (Order.Ascending or Order.Descending)) throw new ArgumentOutOfRangeException(nameof(order));
        // Preserve upstream's bound reversal, including null/open bounds and numeric RedisValues.
        if ((order == Order.Ascending) == (max != default && min.CompareTo(max) > 0))
        {
            (min, max) = (max, min);
            exclude = exclude switch
            {
                Exclude.Start => Exclude.Stop,
                Exclude.Stop => Exclude.Start,
                _ => exclude,
            };
        }
        RedisValue[] arguments = [Key(key), LexBound(min, exclude, Exclude.Start, order), LexBound(max, exclude, Exclude.Stop, order)];
        if (skip != 0 || take != -1) arguments = [.. arguments, "LIMIT", skip, take];
        return Send(order == Order.Ascending ? RespireCommands.SortedSet.ZRANGEBYLEX : RespireCommands.SortedSet.ZREVRANGEBYLEX,
            arguments, flags, Values);
    }

    public Task<long> SortedSetRemoveRangeByValueAsync(RedisKey key, RedisValue min, RedisValue max, Exclude exclude, CommandFlags flags)
        => Send(RespireCommands.SortedSet.ZREMRANGEBYLEX,
            [Key(key), LexBound(min, exclude, Exclude.Start, Order.Ascending), LexBound(max, exclude, Exclude.Stop, Order.Ascending)],
            flags, static result => (long)result);

    private static RedisValue LexBound(RedisValue value, Exclude exclude, Exclude edge, Order order)
    {
        if ((exclude & ~Exclude.Both) != 0) throw new ArgumentOutOfRangeException(nameof(exclude));
        if (value.IsNull) return (edge == Exclude.Start) == (order == Order.Ascending) ? "-" : "+";
        byte[] bytes = ((byte[]?)value)!;
        var bound = new byte[bytes.Length + 1];
        bound[0] = (exclude & edge) == 0 ? (byte)'[' : (byte)'(';
        bytes.CopyTo(bound, 1);
        return bound;
    }
}
