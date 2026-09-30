using System.Globalization;
using System.Text;

namespace Respire.Testing;

public sealed partial class RespireFakeServer
{
    private FakeReply HashSet(string command, byte[][] args)
    {
        if (args.Length % 2 != 0) return WrongArity(command);
        var entry = Find(args[1]);
        var hash = entry?.Hash ?? new Dictionary<byte[], byte[]>(BinaryKeyComparer.Instance);
        if (command == "HSETNX" && hash.ContainsKey(args[2])) return FakeReply.Integer(0);
        var added = 0;
        for (var index = 2; index < args.Length; index += 2)
        {
            if (!hash.ContainsKey(args[index])) added++;
            hash[args[index]] = args[index + 1];
        }
        if (entry is null) _entries[args[1]] = new Entry(hash);
        return command == "HMSET" ? FakeReply.Ok : FakeReply.Integer(added);
    }

    private FakeReply HashGetMany(byte[][] args)
    {
        var hash = Find(args[1])?.Hash;
        return FakeReply.Array(args.Skip(2).Select(field => FakeReply.Bulk(hash?.GetValueOrDefault(field))).ToArray());
    }

    private FakeReply HashGetAll(byte[] key, bool resp3)
    {
        var hash = Find(key)?.Hash;
        var pairs = hash?.SelectMany(pair => new[] { FakeReply.Bulk(pair.Key), FakeReply.Bulk(pair.Value) }).ToArray() ?? [];
        return resp3 ? FakeReply.Map(pairs) : FakeReply.Array(pairs);
    }

    private FakeReply HashDelete(byte[][] args)
    {
        var hash = Find(args[1])?.Hash;
        if (hash is null) return FakeReply.Integer(0);
        var removed = args.Skip(2).Count(hash.Remove);
        if (hash.Count == 0) _entries.Remove(args[1]);
        return FakeReply.Integer(removed);
    }

    private FakeReply HashIncrement(byte[][] args)
    {
        var amount = Integer(args[3]);
        var entry = Find(args[1]);
        var hash = entry?.Hash;
        var previous = hash?.GetValueOrDefault(args[2]);
        long result;
        try { result = checked((previous is null ? 0 : Integer(previous)) + amount); }
        catch (FormatException) { return FakeReply.Error("ERR hash value is not an integer"); }
        catch (OverflowException) { return FakeReply.Error("ERR increment or decrement would overflow"); }
        // Parse and check overflow before publishing a new hash or changing an existing field.
        var value = Encoding.ASCII.GetBytes(result.ToString(CultureInfo.InvariantCulture));
        if (hash is null)
        {
            hash = new Dictionary<byte[], byte[]>(BinaryKeyComparer.Instance);
            _entries[args[1]] = new Entry(hash);
        }
        hash[args[2]] = value;
        return FakeReply.Integer(result);
    }
}
