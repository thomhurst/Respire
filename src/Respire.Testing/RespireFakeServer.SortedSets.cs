namespace Respire.Testing;

public sealed partial class RespireFakeServer
{
    private enum SortedSetRangeKind { Auto, Rank, Score, Lex }

    private FakeReply SortedSetAdd(byte[][] args, bool increment = false)
    {
        bool nx = false, xx = false, gt = false, lt = false, changed = false;
        var start = 2;
        if (!increment)
        {
            while (start < args.Length)
            {
                var option = Token(args[start]);
                if (option is not ("NX" or "XX" or "GT" or "LT" or "CH" or "INCR")) break;
                switch (option)
                {
                    case "NX": nx = true; break;
                    case "XX": xx = true; break;
                    case "GT": gt = true; break;
                    case "LT": lt = true; break;
                    case "CH": changed = true; break;
                    case "INCR": increment = true; break;
                }
                start++;
            }
        }
        var count = (args.Length - start) / 2;
        if (count == 0 || (args.Length - start) % 2 != 0) return FakeReply.Error("ERR syntax error");
        if (nx && xx) return FakeReply.Error("ERR XX and NX options at the same time are not compatible");
        if ((nx && (gt || lt)) || (gt && lt)) return FakeReply.Error("ERR GT, LT, and/or NX options at the same time are not compatible");
        if (increment && count != 1) return FakeReply.Error("ERR INCR option supports a single increment-element pair");
        // Parse every score before looking up or mutating the key, including later duplicates.
        var scores = new double[count];
        for (var index = 0; index < count; index++)
            if (!RedisScore.TryParse(args[start + index * 2], out scores[index]))
                return FakeReply.Error("ERR value is not a valid float");
        var entry = Find(args[1]);
        var set = entry?.SortedSet;
        if (set is null && xx) return increment ? FakeReply.Null : FakeReply.Integer(0);
        set ??= new Dictionary<byte[], double>(BinaryKeyComparer.Instance);
        var added = 0;
        var updated = 0;
        double? result = null;
        for (var index = 0; index < count; index++)
        {
            var member = args[start + index * 2 + 1];
            var exists = set.TryGetValue(member, out var previous);
            if ((exists && nx) || (!exists && xx)) continue;
            var score = scores[index];
            if (exists && increment)
            {
                score += previous;
                if (double.IsNaN(score)) return FakeReply.Error("ERR resulting score is not a number (NaN)");
            }
            if (exists && ((gt && score <= previous) || (lt && score >= previous))) continue;
            if (!exists) added++;
            else if (score != previous) updated++;
            // Equal scores are a no-op, including +0 replacing an existing -0.
            if (!exists || score != previous) set[member] = score;
            result = score;
        }
        if (entry is null && set.Count != 0) _entries[args[1]] = new Entry(set);
        if (added + updated != 0) TouchWatchedKey(args[1]);
        if (increment) return result is { } value ? FakeReply.Double(value) : FakeReply.Null;
        return FakeReply.Integer(added + (changed ? updated : 0));
    }

    private FakeReply SortedSetScores(byte[][] args, bool many)
    {
        var set = Find(args[1])?.SortedSet;
        var replies = new FakeReply[args.Length - 2];
        for (var index = 2; index < args.Length; index++)
            replies[index - 2] = set is not null && set.TryGetValue(args[index], out var score) ? FakeReply.Double(score) : FakeReply.Null;
        return many ? FakeReply.Array(replies) : replies[0];
    }

    private FakeReply SortedSetRemove(byte[][] args)
    {
        var set = Find(args[1])?.SortedSet;
        if (set is null) return FakeReply.Integer(0);
        var removed = 0;
        for (var index = 2; index < args.Length; index++)
            if (set.Remove(args[index])) removed++;
        if (set.Count == 0) _entries.Remove(args[1]);
        if (removed != 0) TouchWatchedKey(args[1]);
        return FakeReply.Integer(removed);
    }

    private static KeyValuePair<byte[], double>[] OrderedSortedSet(Dictionary<byte[], double>? set, bool reverse = false)
    {
        if (set is null) return [];
        var entries = set.ToArray();
        System.Array.Sort(entries, CompareSortedSetEntries);
        if (reverse) System.Array.Reverse(entries);
        return entries;
    }

    private FakeReply SortedSetRank(byte[][] args, bool reverse)
    {
        var set = Find(args[1])?.SortedSet;
        if (set is null || !set.TryGetValue(args[2], out var score)) return FakeReply.Null;
        var target = new KeyValuePair<byte[], double>(args[2], score);
        var rank = 0;
        foreach (var entry in set)
        {
            var order = CompareSortedSetEntries(entry, target);
            if (reverse ? order > 0 : order < 0) rank++;
        }
        return FakeReply.Integer(rank);
    }

    private static int CompareSortedSetEntries(KeyValuePair<byte[], double> left, KeyValuePair<byte[], double> right)
    {
        var scoreOrder = left.Value.CompareTo(right.Value);
        return scoreOrder != 0 ? scoreOrder : left.Key.AsSpan().SequenceCompareTo(right.Key);
    }

    private FakeReply SortedSetPop(byte[][] args, bool reverse, bool resp3)
    {
        var count = args.Length == 3 ? Integer(args[2]) : 1;
        if (count < 0) return FakeReply.Error("ERR value is out of range, must be positive");
        var set = Find(args[1])?.SortedSet;
        var entries = OrderedSortedSet(set, reverse);
        var taken = entries.AsSpan(0, (int)Math.Min(count, entries.Length)).ToArray();
        if (set is not null)
        {
            foreach (var entry in taken) set.Remove(entry.Key);
            if (set.Count == 0) _entries.Remove(args[1]);
            if (taken.Length != 0) TouchWatchedKey(args[1]);
        }
        // RESP3 nests counted pops but preserves the flat, omitted-count reply.
        return SortedSetReply(taken, withScores: true, nested: resp3 && args.Length == 3);
    }

    private FakeReply SortedSetRange(byte[][] args, SortedSetRangeKind kind, bool reverse = false,
        bool resp3 = false, bool count = false, bool remove = false)
    {
        var modern = kind == SortedSetRangeKind.Auto;
        var withScores = false;
        long offset = 0, limit = -1;
        for (var index = 4; index < args.Length; index++)
        {
            switch (Token(args[index]))
            {
                case "WITHSCORES": withScores = true; break;
                case "LIMIT" when index + 2 < args.Length:
                    offset = Integer(args[++index]);
                    limit = Integer(args[++index]);
                    break;
                case "REV" when modern && !reverse: reverse = true; break;
                case "BYSCORE" when kind == SortedSetRangeKind.Auto: kind = SortedSetRangeKind.Score; break;
                case "BYLEX" when kind == SortedSetRangeKind.Auto: kind = SortedSetRangeKind.Lex; break;
                default: return FakeReply.Error("ERR syntax error");
            }
        }
        if (kind == SortedSetRangeKind.Auto) kind = SortedSetRangeKind.Rank;
        // Redis 7.x tests the final count sentinel, not whether LIMIT appeared.
        if (kind == SortedSetRangeKind.Rank && limit != -1)
            return FakeReply.Error("ERR syntax error, LIMIT is only supported in combination with either BYSCORE or BYLEX");
        if (kind == SortedSetRangeKind.Lex && withScores)
            return FakeReply.Error("ERR syntax error, WITHSCORES not supported in combination with BYLEX");
        var first = args[reverse && kind != SortedSetRangeKind.Rank ? 3 : 2];
        var last = args[reverse && kind != SortedSetRangeKind.Rank ? 2 : 3];
        long start = 0, stop = 0;
        ScoreBoundary minimumScore = default, maximumScore = default;
        LexBoundary minimumLex = default, maximumLex = default;
        switch (kind)
        {
            case SortedSetRangeKind.Rank: start = Integer(first); stop = Integer(last); break;
            case SortedSetRangeKind.Score:
                if (!ScoreBoundary.TryParse(first, out minimumScore) || !ScoreBoundary.TryParse(last, out maximumScore))
                    return FakeReply.Error("ERR min or max is not a float");
                break;
            case SortedSetRangeKind.Lex:
                if (!LexBoundary.TryParse(first, out minimumLex) || !LexBoundary.TryParse(last, out maximumLex))
                    return FakeReply.Error("ERR min or max not valid string range item");
                break;
        }
        var set = Find(args[1])?.SortedSet;
        var ordered = OrderedSortedSet(set, reverse);
        IEnumerable<KeyValuePair<byte[], double>> selected;
        if (kind == SortedSetRangeKind.Rank)
        {
            if (start < 0) start += ordered.Length;
            if (stop < 0) stop += ordered.Length;
            start = Math.Max(0, start);
            stop = Math.Min(ordered.Length - 1, stop);
            selected = stop < start ? [] : ordered.Skip((int)start).Take((int)(stop - start + 1));
        }
        else
        {
            selected = kind == SortedSetRangeKind.Score
                ? ordered.Where(entry => minimumScore.Allows(entry.Value, minimum: true) && maximumScore.Allows(entry.Value, minimum: false))
                : ordered.Where(entry => minimumLex.Allows(entry.Key, minimum: true) && maximumLex.Allows(entry.Key, minimum: false));
            if (offset < 0 || offset >= ordered.Length || limit == 0) selected = [];
            else
            {
                selected = selected.Skip((int)offset);
                if (limit >= 0) selected = selected.Take((int)Math.Min(limit, ordered.Length));
            }
        }
        var entries = selected.ToArray();
        if (remove && set is not null)
        {
            foreach (var entry in entries) set.Remove(entry.Key);
            if (set.Count == 0) _entries.Remove(args[1]);
            if (entries.Length != 0) TouchWatchedKey(args[1]);
        }
        return count || remove ? FakeReply.Integer(entries.Length) : SortedSetReply(entries, withScores, resp3);
    }

    private static FakeReply SortedSetReply(KeyValuePair<byte[], double>[] entries, bool withScores, bool nested)
    {
        var replies = new FakeReply[entries.Length * (withScores && !nested ? 2 : 1)];
        var index = 0;
        foreach (var entry in entries)
        {
            var member = FakeReply.Bulk(entry.Key);
            if (!withScores) replies[index++] = member;
            else if (nested) replies[index++] = FakeReply.Array([member, FakeReply.Double(entry.Value)]);
            else
            {
                replies[index++] = member;
                replies[index++] = FakeReply.Double(entry.Value);
            }
        }
        return FakeReply.Array(replies);
    }

    private FakeReply SortedSetIntersectCount(byte[][] args)
    {
        var count = Integer(args[1]);
        if (count < 1) return FakeReply.Error("ERR at least 1 input key is needed for 'zintercard' command");
        if (count > args.Length - 2) return FakeReply.Error("ERR syntax error");
        var sets = new HashSet<byte[]>?[(int)count];
        // Redis checks all key types before parsing LIMIT or taking an empty-set shortcut.
        for (var index = 0; index < sets.Length; index++)
        {
            sets[index] = Find(args[index + 2])?.Data switch
            {
                null => null,
                Dictionary<byte[], double> sorted => new HashSet<byte[]>(sorted.Keys, BinaryKeyComparer.Instance),
                HashSet<byte[]> set => set,
                _ => throw new WrongTypeException(),
            };
        }
        long limit = 0;
        for (var index = 2 + sets.Length; index < args.Length; index += 2)
        {
            if (Token(args[index]) != "LIMIT" || index + 1 == args.Length) return FakeReply.Error("ERR syntax error");
            limit = Integer(args[index + 1]);
            if (limit < 0) return FakeReply.Error("ERR LIMIT can't be negative");
        }
        var smallest = sets.MinBy(set => set?.Count ?? 0);
        if (smallest is null) return FakeReply.Integer(0);
        long matches = 0;
        foreach (var member in smallest)
        {
            if (!sets.All(set => set!.Contains(member))) continue;
            if (++matches == limit) break;
        }
        return FakeReply.Integer(matches);
    }

    private readonly record struct ScoreBoundary(double Value, bool Exclusive)
    {
        internal static bool TryParse(byte[] bytes, out ScoreBoundary boundary)
        {
            boundary = default;
            var exclusive = bytes.Length != 0 && bytes[0] == '(';
            var text = exclusive ? bytes.AsSpan(1) : bytes.AsSpan();
            if (!RedisScore.TryParse(text, out var value)) return false;
            boundary = new(value, exclusive);
            return true;
        }
        internal bool Allows(double score, bool minimum)
        {
            if (minimum) return Exclusive ? score > Value : score >= Value;
            return Exclusive ? score < Value : score <= Value;
        }
    }

    private readonly record struct LexBoundary(byte[]? Value, bool Exclusive, int Infinity)
    {
        internal static bool TryParse(byte[] bytes, out LexBoundary boundary)
        {
            boundary = default;
            if (bytes.Length == 1 && bytes[0] is (byte)'+' or (byte)'-')
            {
                boundary = new(null, false, bytes[0] == '+' ? 1 : -1);
                return true;
            }
            if (bytes.Length == 0 || bytes[0] is not ((byte)'[' or (byte)'('))
                return false;
            boundary = new(bytes[1..], bytes[0] == '(', 0);
            return true;
        }
        internal bool Allows(byte[] member, bool minimum)
        {
            if (Infinity != 0) return minimum ? Infinity < 0 : Infinity > 0;
            var order = member.AsSpan().SequenceCompareTo(Value);
            if (minimum) return Exclusive ? order > 0 : order >= 0;
            return Exclusive ? order < 0 : order <= 0;
        }
    }
}
