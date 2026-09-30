namespace Respire.Testing;

public sealed partial class RespireFakeServer
{
    private const long MaximumTransactionBytes = 16 * 1024 * 1024;
    private readonly Dictionary<byte[], HashSet<Connection>> _watchers = new(BinaryKeyComparer.Instance);

    // Every method in this partial runs synchronously under _gate.
    private static FakeReply BeginTransaction(Connection connection)
    {
        if (connection.Transaction is not null) return FakeReply.Error("ERR MULTI calls can not be nested");
        connection.Transaction = [];
        return FakeReply.Ok;
    }

    private FakeReply QueueTransaction(Connection connection, byte[][] args)
    {
        // Subscription control and protocol changes inside EXEC need mixed framing, which
        // this bounded transaction subset deliberately rejects instead of inventing replies.
        var command = Token(args[0]);
        if (command is "SUBSCRIBE" or "UNSUBSCRIBE" or "HELLO")
            return RejectCommand(connection, command, FakeReply.Error($"ERR Respire.Testing does not support {command} inside MULTI"));
        if (connection.TransactionError || connection.WatchChanged) return FakeReply.Simple("QUEUED");
        long bytes = 16 + args.Length * 8L;
        foreach (var argument in args) bytes += argument.Length;
        if (bytes > MaximumTransactionBytes - connection.QueuedBytes)
            return RejectCommand(connection, command, FakeReply.Error("ERR Respire.Testing transaction queue exceeds 16 MiB"));
        // ReadArguments already copied every argument out of reusable request storage.
        connection.Transaction!.Add(args);
        connection.QueuedBytes += bytes;
        return FakeReply.Simple("QUEUED");
    }

    private FakeReply ExecuteTransaction(Connection connection)
    {
        if (connection.Transaction is not { } commands) return FakeReply.Error("ERR EXEC without MULTI");
        // Lazy expiry must invalidate a watch even when no other command accessed the key.
        foreach (var key in connection.WatchedKeys) Find(key);
        var invalid = connection.TransactionError;
        var changed = connection.WatchChanged;
        ClearTransaction(connection);
        if (invalid) return FakeReply.Error("EXECABORT Transaction discarded because of previous errors.");
        if (changed) return FakeReply.NullArray;
        var replies = new FakeReply[commands.Count];
        // _gate remains held for the complete sequence; expected command errors are elements,
        // not rollbacks. Faults apply to received commands/EXEC, never this internal traversal.
        for (var index = 0; index < commands.Count; index++) replies[index] = Execute(connection, commands[index]);
        return FakeReply.Array(replies);
    }

    private FakeReply DiscardTransaction(Connection connection)
    {
        if (connection.Transaction is null) return FakeReply.Error("ERR DISCARD without MULTI");
        ClearTransaction(connection);
        return FakeReply.Ok;
    }

    private FakeReply RejectCommand(Connection connection, string command, FakeReply error)
    {
        if (connection.Transaction is null) return error;
        connection.TransactionError = true;
        connection.Transaction.Clear();
        connection.QueuedBytes = 0;
        if (command != "EXEC") return error;
        ClearTransaction(connection);
        return FakeReply.Error($"EXECABORT Transaction discarded because of: {error.Value}");
    }

    private FakeReply Watch(Connection connection, byte[][] args)
    {
        if (connection.Transaction is not null) return FakeReply.Error("ERR WATCH inside MULTI is not allowed");
        // An invalidated watch stays invalid until UNWATCH, DISCARD, or EXEC clears it.
        if (connection.WatchChanged) return FakeReply.Ok;
        for (var index = 1; index < args.Length; index++)
        {
            var key = args[index];
            // An already expired key is logically absent before its new watch starts.
            Find(key);
            if (!connection.WatchedKeys.Add(key)) continue;
            if (!_watchers.TryGetValue(key, out var watchers)) _watchers[key] = watchers = [];
            watchers.Add(connection);
        }
        return FakeReply.Ok;
    }

    private FakeReply Unwatch(Connection connection)
    {
        foreach (var key in connection.WatchedKeys)
        {
            var watchers = _watchers[key];
            watchers.Remove(connection);
            if (watchers.Count == 0) _watchers.Remove(key);
        }
        connection.WatchedKeys.Clear();
        connection.WatchChanged = false;
        return FakeReply.Ok;
    }

    private void ClearTransaction(Connection connection)
    {
        connection.Transaction = null;
        connection.TransactionError = false;
        connection.QueuedBytes = 0;
        // Redis clears both the queue and all WATCH registrations after EXEC or DISCARD.
        Unwatch(connection);
    }

    private void TouchWatchedKey(byte[] key)
    {
        if (!_watchers.TryGetValue(key, out var watchers)) return;
        foreach (var watcher in watchers) watcher.WatchChanged = true;
    }
}
