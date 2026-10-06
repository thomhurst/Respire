namespace Respire;

internal static class ArrayCommandArguments
{
    // Redis 8.10 src/t_array.c: ARGREP_MAX_PREDICATES.
    private const int MaximumGrepPredicates = 250;
    internal static void ValidateIndex(ulong index)
    {
        if (index == ulong.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(index), "UInt64.MaxValue is reserved by Redis.");
    }

    internal static RespireValue[] Range(ulong start, ulong end, long? limit = null)
    {
        ValidateIndex(start);
        ValidateIndex(end);
        if (limit is <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        return limit is { } count ? [start, end, "LIMIT", count] : [start, end];
    }

    internal static RespireValue[] Indexes(ReadOnlySpan<ulong> indexes)
    {
        RequireItems(indexes.Length);
        var args = new RespireValue[indexes.Length];
        for (var i = 0; i < indexes.Length; i++)
        {
            ValidateIndex(indexes[i]);
            args[i] = indexes[i];
        }
        return args;
    }

    internal static RespireValue[] Ranges(ReadOnlySpan<RespireArrayRange> ranges)
    {
        RequireItems(ranges.Length);
        var args = new RespireValue[checked(ranges.Length * 2)];
        for (var i = 0; i < ranges.Length; i++)
        {
            ValidateIndex(ranges[i].Start);
            ValidateIndex(ranges[i].End);
            args[i * 2] = ranges[i].Start;
            args[i * 2 + 1] = ranges[i].End;
        }
        return args;
    }

    internal static RespireValue[] Set(ulong index, ReadOnlySpan<RespireValue> values)
    {
        ValidateIndex(index);
        RequireItems(values.Length);
        if ((ulong)(values.Length - 1) >= ulong.MaxValue - index)
            throw new ArgumentOutOfRangeException(nameof(values), "Array index overflow.");
        return Prefix(index, values);
    }

    internal static RespireValue[] SetMany(ReadOnlySpan<RespireArrayItem> items)
    {
        RequireItems(items.Length);
        var args = new RespireValue[checked(items.Length * 2)];
        for (var i = 0; i < items.Length; i++)
        {
            ValidateIndex(items[i].Index);
            args[i * 2] = items[i].Index;
            args[i * 2 + 1] = items[i].Value;
        }
        return args;
    }

    internal static RespireValue[] Values(ReadOnlySpan<RespireValue> values)
    {
        RequireItems(values.Length);
        return values.ToArray();
    }

    internal static RespireValue[] Ring(long size, ReadOnlySpan<RespireValue> values)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        RequireItems(values.Length);
        return Prefix(size, values);
    }

    private static RespireValue[] Prefix(RespireValue first, ReadOnlySpan<RespireValue> values)
    {
        var args = new RespireValue[checked(values.Length + 1)];
        args[0] = first;
        values.CopyTo(args.AsSpan(1));
        return args;
    }

    internal static RespireValue[] LastItems(long count, bool reverse) => reverse ? [count, "REV"] : [count];
    internal static RespireValue[] Info(bool full) => full ? ["FULL"] : [];

    internal static RespireValue[] Aggregate(ulong start, ulong end, RespireArrayOperation operation, RespireValue? match)
    {
        ValidateIndex(start);
        ValidateIndex(end);
        var token = operation switch
        {
            RespireArrayOperation.Sum => "SUM", RespireArrayOperation.Min => "MIN", RespireArrayOperation.Max => "MAX",
            RespireArrayOperation.And => "AND", RespireArrayOperation.Or => "OR", RespireArrayOperation.Xor => "XOR",
            RespireArrayOperation.Match => "MATCH", RespireArrayOperation.Used => "USED",
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        if (operation == RespireArrayOperation.Match)
        {
            if (match is not { } value) throw new ArgumentException("MATCH requires a value.", nameof(match));
            return [start, end, token, value];
        }
        if (match.HasValue) throw new ArgumentException("Only MATCH accepts a match value.", nameof(match));
        return [start, end, token];
    }

    internal static RespireValue[] Grep(RespireArrayBound start, RespireArrayBound end,
        ReadOnlySpan<RespireArrayPredicate> predicates, RespireArrayGrepOptions options, bool withValues)
    {
        RequireItems(predicates.Length);
        if (predicates.Length > MaximumGrepPredicates) throw new ArgumentOutOfRangeException(nameof(predicates));
        if (options.Limit is <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        var args = new RespireValue[2 + predicates.Length * 2 + (options.MatchAll ? 1 : 0)
            + (options.IgnoreCase ? 1 : 0) + (options.Limit.HasValue ? 2 : 0) + (withValues ? 1 : 0)];
        args[0] = start.Argument;
        args[1] = end.Argument;
        var index = 2;
        foreach (var predicate in predicates)
        {
            args[index++] = predicate.Kind switch
            {
                RespireArrayPredicateKind.Exact => "EXACT", RespireArrayPredicateKind.Contains => "MATCH",
                RespireArrayPredicateKind.Glob => "GLOB", RespireArrayPredicateKind.Regex => "RE",
                _ => throw new ArgumentOutOfRangeException(nameof(predicates)),
            };
            args[index++] = predicate.Pattern;
        }
        if (options.MatchAll) args[index++] = "AND";
        if (options.IgnoreCase) args[index++] = "NOCASE";
        if (options.Limit is { } limit)
        {
            args[index++] = "LIMIT";
            args[index++] = limit;
        }
        if (withValues) args[index] = "WITHVALUES";
        return args;
    }

    private static void RequireItems(int count)
    {
        if (count == 0) throw new ArgumentException("At least one item is required.");
    }
}
