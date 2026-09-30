namespace Respire.Testing;

public sealed partial class RespireFakeServer
{
    private enum SetOperation { Intersection, Union, Difference }

    private FakeReply SetAdd(byte[][] args)
    {
        var entry = Find(args[1]);
        var set = entry?.Set ?? new HashSet<byte[]>(BinaryKeyComparer.Instance);
        var added = 0;
        for (var index = 2; index < args.Length; index++)
            if (set.Add(args[index])) added++;
        if (entry is null) _entries[args[1]] = new Entry(set);
        if (added != 0) TouchWatchedKey(args[1]);
        return FakeReply.Integer(added);
    }

    private FakeReply SetRemove(byte[][] args)
    {
        var set = Find(args[1])?.Set;
        if (set is null) return FakeReply.Integer(0);
        var removed = 0;
        for (var index = 2; index < args.Length; index++)
            if (set.Remove(args[index])) removed++;
        if (set.Count == 0) _entries.Remove(args[1]);
        if (removed != 0) TouchWatchedKey(args[1]);
        return FakeReply.Integer(removed);
    }

    private FakeReply SetContainsMany(byte[][] args)
    {
        var set = Find(args[1])?.Set;
        var replies = new FakeReply[args.Length - 2];
        for (var index = 2; index < args.Length; index++)
            replies[index - 2] = FakeReply.Integer(set?.Contains(args[index]) == true ? 1 : 0);
        return FakeReply.Array(replies);
    }

    private FakeReply SetMove(byte[][] args)
    {
        var source = Find(args[1]);
        if (source is null) return FakeReply.Integer(0);
        var sourceSet = source.Set;
        var destination = Find(args[2]);
        var destinationSet = destination?.Set; // Validate both types before removing anything.
        if (ReferenceEquals(source, destination))
            return FakeReply.Integer(sourceSet.Contains(args[3]) ? 1 : 0);
        if (!sourceSet.Remove(args[3])) return FakeReply.Integer(0);
        if (sourceSet.Count == 0) _entries.Remove(args[1]);
        if (destinationSet is null)
        {
            destinationSet = new HashSet<byte[]>(BinaryKeyComparer.Instance);
            _entries[args[2]] = new Entry(destinationSet);
        }
        TouchWatchedKey(args[1]);
        if (destinationSet.Add(args[3])) TouchWatchedKey(args[2]);
        return FakeReply.Integer(1);
    }

    private HashSet<byte[]>?[] ReadSets(byte[][] args, int start, int count)
    {
        var sets = new HashSet<byte[]>?[count];
        // Validate even inputs after an empty set, before a STORE can change its destination.
        for (var index = 0; index < count; index++) sets[index] = Find(args[start + index])?.Set;
        return sets;
    }

    private FakeReply SetCombine(byte[][] args, SetOperation operation, bool store = false)
    {
        var start = store ? 2 : 1;
        var sets = ReadSets(args, start, args.Length - start);
        var result = CombineSets(sets, operation);
        if (!store) return FakeReply.Set(result.Select(FakeReply.Bulk).ToArray());
        // Materialize first: destination may alias any source. STORE replaces its type and TTL.
        if (result.Count == 0) DeleteEntry(args[1]);
        else SetEntry(args[1], new Entry(result));
        return FakeReply.Integer(result.Count);
    }

    private static HashSet<byte[]> CombineSets(HashSet<byte[]>?[] sets, SetOperation operation)
    {
        var result = new HashSet<byte[]>(sets[0] ?? [], BinaryKeyComparer.Instance);
        for (var index = 1; index < sets.Length; index++)
        {
            var set = sets[index];
            switch (operation)
            {
                case SetOperation.Intersection: result.IntersectWith(set ?? []); break;
                case SetOperation.Union: result.UnionWith(set ?? []); break;
                case SetOperation.Difference: result.ExceptWith(set ?? []); break;
            }
        }
        return result;
    }

    private FakeReply SetIntersectCount(byte[][] args)
    {
        var count = Integer(args[1]);
        if (count <= 0) return FakeReply.Error("ERR numkeys should be greater than 0");
        if (count > args.Length - 2) return FakeReply.Error("ERR Number of keys can't be greater than number of args");
        long limit = 0;
        for (var index = 2 + (int)count; index < args.Length; index += 2)
        {
            if (Token(args[index]) != "LIMIT" || index + 1 == args.Length) return FakeReply.Error("ERR syntax error");
            limit = Integer(args[index + 1]);
            if (limit < 0) return FakeReply.Error("ERR LIMIT can't be negative");
        }
        var sets = ReadSets(args, 2, (int)count);
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
}
