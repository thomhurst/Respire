namespace Respire.Testing;

public sealed partial class RespireFakeServer
{
    private FakeReply ListPush(byte[][] args, bool left, bool onlyExisting)
    {
        var entry = Find(args[1]);
        if (entry is null && onlyExisting) return FakeReply.Integer(0);
        var list = entry?.List ?? new List<byte[]>();
        if (left) list.InsertRange(0, args.Skip(2).Reverse());
        else list.AddRange(args.Skip(2));
        if (entry is null) _entries[args[1]] = new Entry(list);
        return FakeReply.Integer(list.Count);
    }

    private FakeReply ListPop(byte[][] args, bool left)
    {
        var counted = args.Length == 3;
        var requested = counted ? Integer(args[2]) : 1;
        if (requested < 0) throw new FormatException();
        var list = Find(args[1])?.List;
        if (list is null) return counted ? FakeReply.NullArray : FakeReply.Null;
        var count = (int)Math.Min(requested, list.Count);
        var values = new FakeReply[count];
        for (var index = 0; index < count; index++)
            values[index] = FakeReply.Bulk(list[left ? index : list.Count - index - 1]);
        list.RemoveRange(left ? 0 : list.Count - count, count);
        if (list.Count == 0) _entries.Remove(args[1]);
        return counted ? FakeReply.Array(values) : values[0];
    }

    private FakeReply ListIndex(byte[][] args, bool replace)
    {
        // Redis checks existence/type before parsing the index for LINDEX and LSET.
        var list = Find(args[1])?.List;
        if (list is null) return replace ? FakeReply.Error("ERR no such key") : FakeReply.Null;
        var index = Integer(args[2]);
        if (index < 0) index += list.Count;
        if (index < 0 || index >= list.Count)
            return replace ? FakeReply.Error("ERR index out of range") : FakeReply.Null;
        if (!replace) return FakeReply.Bulk(list[(int)index]);
        list[(int)index] = args[3];
        return FakeReply.Ok;
    }

    private FakeReply ListRange(byte[][] args, bool trim)
    {
        var start = Integer(args[2]);
        var stop = Integer(args[3]);
        var list = Find(args[1])?.List;
        if (list is null) return trim ? FakeReply.Ok : FakeReply.Array([]);
        if (start < 0) start += list.Count;
        if (stop < 0) stop += list.Count;
        start = Math.Max(0, start);
        stop = Math.Min(list.Count - 1, stop);
        var count = stop < start ? 0 : (int)(stop - start + 1);
        if (!trim)
        {
            var values = new FakeReply[count];
            for (var index = 0; index < count; index++) values[index] = FakeReply.Bulk(list[(int)start + index]);
            return FakeReply.Array(values);
        }
        if (count == 0) _entries.Remove(args[1]);
        else
        {
            list.RemoveRange((int)start + count, list.Count - (int)start - count);
            list.RemoveRange(0, (int)start);
        }
        return FakeReply.Ok;
    }

    private FakeReply ListRemove(byte[][] args)
    {
        var requested = Integer(args[2]);
        var list = Find(args[1])?.List;
        if (list is null) return FakeReply.Integer(0);
        // long.MinValue requests more matches than any managed list can contain.
        var limit = requested is 0 or long.MinValue ? long.MaxValue : Math.Abs(requested);
        var skip = requested < 0 ? Math.Max(0, list.Count(value => value.AsSpan().SequenceEqual(args[3])) - limit) : 0;
        var remaining = limit;
        // Compact once, preserving order. Repeated RemoveAt would be quadratic for many matches.
        var removed = list.RemoveAll(value =>
        {
            if (!value.AsSpan().SequenceEqual(args[3]) || remaining == 0) return false;
            if (skip > 0) { skip--; return false; }
            remaining--;
            return true;
        });
        if (list.Count == 0) _entries.Remove(args[1]);
        return FakeReply.Integer(removed);
    }

    private FakeReply ListInsert(byte[][] args)
    {
        var position = Token(args[2]);
        if (position is not ("BEFORE" or "AFTER")) return Syntax("LINSERT");
        var list = Find(args[1])?.List;
        if (list is null) return FakeReply.Integer(0);
        var pivot = list.FindIndex(value => value.AsSpan().SequenceEqual(args[3]));
        if (pivot < 0) return FakeReply.Integer(-1);
        list.Insert(position == "BEFORE" ? pivot : pivot + 1, args[4]);
        return FakeReply.Integer(list.Count);
    }

    private FakeReply ListPosition(byte[][] args)
    {
        long rank = 1, count = -1, maxLength = 0;
        for (var index = 3; index < args.Length; index += 2)
        {
            if (index + 1 == args.Length) return Syntax("LPOS");
            switch (Token(args[index]))
            {
                case "RANK":
                    rank = Integer(args[index + 1]);
                    if (rank == long.MinValue) throw new FormatException();
                    if (rank == 0) return FakeReply.Error("ERR RANK can't be zero");
                    break;
                case "COUNT":
                    count = Integer(args[index + 1]);
                    if (count < 0) return FakeReply.Error("ERR COUNT can't be negative");
                    break;
                case "MAXLEN":
                    maxLength = Integer(args[index + 1]);
                    if (maxLength < 0) return FakeReply.Error("ERR MAXLEN can't be negative");
                    break;
                default: return Syntax("LPOS");
            }
        }
        var list = Find(args[1])?.List;
        List<FakeReply>? positions = count >= 0 ? new() : null;
        if (list is not null)
        {
            var matches = 0L;
            for (var scanned = 0; scanned < list.Count && (maxLength == 0 || scanned < maxLength); scanned++)
            {
                var index = rank > 0 ? scanned : list.Count - scanned - 1;
                if (!list[index].AsSpan().SequenceEqual(args[2]) || ++matches < Math.Abs(rank)) continue;
                if (positions is null) return FakeReply.Integer(index);
                positions.Add(FakeReply.Integer(index));
                if (count > 0 && positions.Count == count) break;
            }
        }
        return positions is null ? FakeReply.Null : FakeReply.Array(positions.ToArray());
    }
}
