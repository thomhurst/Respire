using System.Globalization;

namespace Respire.Testing;

public sealed partial class RespireFakeServer
{
    private sealed class FakeArray
    {
        internal SortedDictionary<ulong, byte[]> Items { get; } = [];
        internal ulong? LastInsert { get; set; }
        internal ulong Length => Items.Count == 0 ? 0 : Items.Last().Key + 1;
    }

    private static ulong ArrayIndex(byte[] value, bool allowMaximum = false)
    {
        if (!ulong.TryParse(System.Text.Encoding.ASCII.GetString(value), NumberStyles.None,
            CultureInfo.InvariantCulture, out var index) || !allowMaximum && index == ulong.MaxValue)
            throw new FormatException("Invalid array index.");
        return index;
    }

    private static FakeReply ArrayUnsigned(ulong value) => value <= long.MaxValue
        ? FakeReply.Integer((long)value) : FakeReply.Text(value.ToString(CultureInfo.InvariantCulture));

    private FakeArray? FindArray(byte[] key) => Find(key)?.Data is { } data
        ? data as FakeArray ?? throw new WrongTypeException() : null;

    private FakeArray WriteArray(byte[] key)
    {
        if (FindArray(key) is { } array) return array;
        var created = new FakeArray();
        SetEntry(key, new Entry(created));
        return created;
    }

    private FakeReply ArrayGet(byte[][] args)
    {
        var index = ArrayIndex(args[2]);
        return FakeReply.Bulk(FindArray(args[1])?.Items.GetValueOrDefault(index));
    }

    private FakeReply ArrayGetMany(byte[][] args)
    {
        var indexes = args.Skip(2).Select(value => ArrayIndex(value)).ToArray();
        var array = FindArray(args[1]);
        return FakeReply.Array(indexes.Select(index => FakeReply.Bulk(array?.Items.GetValueOrDefault(index))).ToArray());
    }

    private FakeReply ArraySet(byte[][] args, bool scattered)
    {
        if (scattered && (args.Length - 2) % 2 != 0) return WrongArity("ARMSET");
        var count = scattered ? (args.Length - 2) / 2 : args.Length - 3;
        var indexes = new ulong[count];
        if (scattered)
        {
            for (var i = 0; i < count; i++) indexes[i] = ArrayIndex(args[2 + i * 2]);
        }
        else
        {
            var start = ArrayIndex(args[2]);
            if ((ulong)(count - 1) >= ulong.MaxValue - start) return FakeReply.Error("ERR array index overflow");
            for (var i = 0; i < count; i++) indexes[i] = start + (ulong)i;
        }
        var array = WriteArray(args[1]);
        long added = 0;
        for (var i = 0; i < count; i++)
        {
            if (!array.Items.ContainsKey(indexes[i])) added++;
            array.Items[indexes[i]] = args[scattered ? 3 + i * 2 : 3 + i];
        }
        TouchWatchedKey(args[1]);
        return FakeReply.Integer(added);
    }

    private FakeReply ArrayDelete(byte[][] args, bool ranges)
    {
        if (ranges && (args.Length - 2) % 2 != 0) return WrongArity("ARDELRANGE");
        var indexes = args.Skip(2).Select(value => ArrayIndex(value)).ToArray();
        var array = FindArray(args[1]);
        if (array is null) return FakeReply.Integer(0);
        long removed = 0;
        if (ranges)
        {
            for (var i = 0; i < indexes.Length; i += 2)
            {
                var low = Math.Min(indexes[i], indexes[i + 1]);
                var high = Math.Max(indexes[i], indexes[i + 1]);
                foreach (var index in array.Items.Keys.Where(index => index >= low && index <= high).ToArray())
                    if (array.Items.Remove(index)) removed++;
            }
        }
        else foreach (var index in indexes) if (array.Items.Remove(index)) removed++;
        if (removed != 0)
        {
            if (array.Items.Count == 0) DeleteEntry(args[1]);
            else TouchWatchedKey(args[1]);
        }
        return FakeReply.Integer(removed);
    }

    private FakeReply ArrayRange(byte[][] args)
    {
        var start = ArrayIndex(args[2]);
        var end = ArrayIndex(args[3]);
        var array = FindArray(args[1]);
        var length = Math.Max(start, end) - Math.Min(start, end) + 1;
        if (length > 1_000_000) return FakeReply.Error("ERR range exceeds maximum of 1000000 items");
        var result = new FakeReply[(int)length];
        for (var i = 0; i < result.Length; i++)
        {
            var index = start > end ? start - (ulong)i : start + (ulong)i;
            result[i] = FakeReply.Bulk(array?.Items.GetValueOrDefault(index));
        }
        return FakeReply.Array(result);
    }

    private static IEnumerable<KeyValuePair<ulong, byte[]>> ArrayItems(FakeArray? array, ulong start, ulong end)
    {
        if (array is null) return [];
        var low = Math.Min(start, end);
        var high = Math.Max(start, end);
        var items = array.Items.Where(item => item.Key >= low && item.Key <= high);
        return start > end ? items.Reverse() : items;
    }

    private FakeReply ArrayScan(byte[][] args)
    {
        var start = ArrayIndex(args[2]);
        var end = ArrayIndex(args[3]);
        var limit = long.MaxValue;
        if (args.Length != 4)
        {
            if (args.Length != 6 || Token(args[4]) != "LIMIT") return Syntax("ARSCAN");
            limit = Integer(args[5]);
            if (limit <= 0) return FakeReply.Error("ERR LIMIT must be positive");
        }
        List<FakeReply> result = [];
        foreach (var item in ArrayItems(FindArray(args[1]), start, end))
        {
            result.Add(FakeReply.Array([ArrayUnsigned(item.Key), FakeReply.Bulk(item.Value)]));
            if (--limit == 0) break;
        }
        return FakeReply.Array(result.ToArray());
    }

    private FakeReply ArraySeek(byte[][] args)
    {
        var next = ArrayIndex(args[2], allowMaximum: true);
        var array = FindArray(args[1]);
        if (array is null) return FakeReply.Integer(0);
        array.LastInsert = next == 0 ? null : next - 1;
        TouchWatchedKey(args[1]);
        return FakeReply.Integer(1);
    }

    private FakeReply ArrayNext(byte[] key)
    {
        var last = FindArray(key)?.LastInsert;
        return last == ulong.MaxValue - 1 ? FakeReply.Null : ArrayUnsigned(last is { } index ? index + 1 : 0);
    }

    private FakeReply ArrayInsert(byte[][] args)
    {
        var existing = FindArray(args[1]);
        var last = existing?.LastInsert;
        if (last is >= ulong.MaxValue - 1) return FakeReply.Error("ERR insert index overflow");
        var start = last is { } index ? index + 1 : 0;
        var count = args.Length - 2;
        if ((ulong)(count - 1) >= ulong.MaxValue - start) return FakeReply.Error("ERR insert index overflow");
        var array = existing ?? WriteArray(args[1]);
        for (var i = 0; i < count; i++) array.Items[start + (ulong)i] = args[2 + i];
        array.LastInsert = start + (ulong)count - 1;
        TouchWatchedKey(args[1]);
        return ArrayUnsigned(array.LastInsert.Value);
    }

    private FakeReply ArrayRing(byte[][] args)
    {
        var signedSize = Integer(args[2]);
        if (signedSize <= 0) return FakeReply.Error("ERR size must be positive");
        var size = (ulong)signedSize;
        var array = WriteArray(args[1]);
        var span = array.Length;
        var next = array.LastInsert is { } last ? last + 1 : 0;
        if (size < span || size > span && array.LastInsert.HasValue && next < span)
        {
            var keep = Math.Min(size, span);
            var current = array.LastInsert is { } anchor ? anchor % span : span - 1;
            List<byte[]> tail = [];
            while ((ulong)tail.Count < keep && array.Items.TryGetValue(current, out var value))
            {
                tail.Add(value);
                current = current == 0 ? span - 1 : current - 1;
            }
            array.Items.Clear();
            tail.Reverse();
            for (var i = 0; i < tail.Count; i++) array.Items[(ulong)i] = tail[i];
            array.LastInsert = tail.Count == 0 ? null : (ulong)tail.Count - 1;
        }
        for (var i = 3; i < args.Length; i++)
        {
            var cursor = (array.LastInsert is { } index ? index + 1 : 0) % size;
            array.Items[cursor] = args[i];
            array.LastInsert = cursor;
        }
        TouchWatchedKey(args[1]);
        return ArrayUnsigned(array.LastInsert!.Value);
    }

    private FakeReply ArrayLastItems(byte[][] args)
    {
        var count = Integer(args[2]);
        if (count <= 0) return FakeReply.Array([]);
        if (args.Length > 4 || args.Length == 4 && Token(args[3]) != "REV") return Syntax("ARLASTITEMS");
        var array = FindArray(args[1]);
        if (array is null) return FakeReply.Array([]);
        var result = new FakeReply[(int)Math.Min(count, array.Items.Count)];
        var current = array.LastInsert ?? array.Length - 1;
        for (var i = 0; i < result.Length; i++)
        {
            result[args.Length == 4 ? i : result.Length - i - 1] = FakeReply.Bulk(array.Items.GetValueOrDefault(current));
            current = current == 0 ? array.Length - 1 : current - 1;
        }
        return FakeReply.Array(result);
    }

    private FakeReply ArrayInfo(byte[][] args, bool resp3)
    {
        if (args.Length > 3 || args.Length == 3 && Token(args[2]) != "FULL") return Syntax("ARINFO");
        var array = FindArray(args[1]);
        if (array is null) return FakeReply.Error("ERR no such key");
        // This fake stores a dictionary, not Redis slices. Logical fields are exact;
        // storage statistics describe the absence of Redis slices in the fake.
        List<FakeReply> fields =
        [
            FakeReply.Text("count"), ArrayUnsigned((ulong)array.Items.Count),
            FakeReply.Text("len"), ArrayUnsigned(array.Length),
            FakeReply.Text("next-insert-index"), ArrayUnsigned(array.LastInsert is null or ulong.MaxValue - 1 ? 0 : array.LastInsert.Value + 1),
            FakeReply.Text("slices"), FakeReply.Integer(0),
            FakeReply.Text("directory-size"), FakeReply.Integer(0),
            FakeReply.Text("super-dir-entries"), FakeReply.Integer(0),
            FakeReply.Text("slice-size"), FakeReply.Integer(0),
        ];
        if (args.Length == 3) fields.AddRange(
        [
            FakeReply.Text("dense-slices"), FakeReply.Integer(0),
            FakeReply.Text("sparse-slices"), FakeReply.Integer(0),
            FakeReply.Text("avg-dense-size"), FakeReply.Double(0),
            FakeReply.Text("avg-dense-fill"), FakeReply.Double(0),
            FakeReply.Text("avg-sparse-size"), FakeReply.Double(0),
        ]);
        return resp3 ? FakeReply.Map(fields.ToArray()) : FakeReply.Array(fields.ToArray());
    }
}
