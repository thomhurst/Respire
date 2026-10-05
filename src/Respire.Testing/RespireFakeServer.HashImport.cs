namespace Respire.Testing;

public sealed partial class RespireFakeServer
{
    private static FakeReply PrepareImport(Connection connection, byte[][] args)
    {
        var fields = args[3..];
        var distinct = new HashSet<byte[]>(BinaryKeyComparer.Instance);
        foreach (var field in fields)
            if (!distinct.Add(field)) return FakeReply.Error("ERR duplicate field name in fieldset");
        // Requests already own their binary arguments. Publish only after validation,
        // preserving an existing definition when a replacement contains duplicates.
        connection.ImportFieldsets[args[2]] = fields;
        return FakeReply.Ok;
    }

    private FakeReply SetImport(Connection connection, byte[][] args)
    {
        // Redis checks an existing key's type before resolving the fieldset.
        _ = Find(args[2])?.Hash;
        if (!connection.ImportFieldsets.TryGetValue(args[3], out var fields))
            return FakeReply.Error("ERR no such fieldset");
        if (args.Length - 4 != fields.Length)
            return FakeReply.Error("ERR value count does not match fieldset field count");
        var hash = new Dictionary<byte[], byte[]>(fields.Length, BinaryKeyComparer.Instance);
        for (var index = 0; index < fields.Length; index++) hash.Add(fields[index], args[index + 4]);
        // HIMPORT SET replaces the previous hash and expiry, rather than extending it.
        _entries[args[2]] = new Entry(hash);
        TouchWatchedKey(args[2]);
        return FakeReply.Ok;
    }

    private static FakeReply DiscardAllImports(Connection connection)
    {
        var removed = connection.ImportFieldsets.Count;
        connection.ImportFieldsets.Clear();
        return FakeReply.Integer(removed);
    }
}
