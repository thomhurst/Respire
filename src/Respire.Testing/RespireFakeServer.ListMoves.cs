using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Respire.Testing;

public sealed partial class RespireFakeServer
{
    private readonly Dictionary<byte[], LinkedList<ListMoveWaiter>> _listMoveWaiters = new(BinaryKeyComparer.Instance);
    private readonly HashSet<byte[]> _readyListMoveKeys = new(BinaryKeyComparer.Instance);
    private readonly Queue<byte[]> _readyListMoves = new();

    private sealed class ListMoveWaiter(Connection connection, byte[][] arguments, long started, double seconds)
    {
        internal Connection Connection
        {
            get;
        } = connection;

        internal byte[][] Arguments
        {
            get;
        } = arguments;

        internal TaskCompletionSource<Outbound?> Completion
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal double RemainingSeconds => seconds == 0 ? double.PositiveInfinity : seconds - Stopwatch.GetElapsedTime(started).TotalSeconds;
    }

    private FakeReply ListMoveMany(byte[][] args, bool blocking)
    {
        var firstOption = blocking ? 6 : 5;

        if (args.Length != firstOption && args.Length != firstOption + 3)
        {
            return Syntax(Token(args[0]));
        }

        var from = Token(args[3]);
        var to = Token(args[4]);

        if (from is not ("LEFT" or "RIGHT") || to is not ("LEFT" or "RIGHT"))
        {
            return Syntax(Token(args[0]));
        }

        if (blocking && !TryListMoveTimeout(args[5], out _))
        {
            return FakeReply.Error("ERR timeout is not a valid nonnegative number or is out of range");
        }

        var count = 1L;
        var exactly = false;
        var bulk = false;

        if (args.Length > firstOption)
        {
            var selector = Token(args[firstOption]);
            var ordering = Token(args[firstOption + 2]);

            if (selector is not ("COUNT" or "EXACTLY") || ordering is not ("OBO" or "BULK"))
            {
                return Syntax(Token(args[0]));
            }

            count = Integer(args[firstOption + 1]);

            if (count <= 0)
            {
                return FakeReply.Error("ERR count should be greater than 0");
            }

            exactly = selector == "EXACTLY";
            bulk = ordering == "BULK";
        }

        var source = Find(args[1])?.List;

        if (source is null || source.Count == 0 || exactly && source.Count < count)
        {
            return FakeReply.NullArray;
        }
        // Validate the destination before changing the source. Preserve the entry and its TTL
        // when moving within one list, even if the selected block includes every element.
        var destinationEntry = Find(args[2]);
        var destination = destinationEntry?.List ?? [];
        var movedCount = (int)Math.Min(count, source.Count); // Bounded by the list's int-sized count.
        var start = from == "LEFT" ? 0 : source.Count - movedCount;
        var values = source.GetRange(start, movedCount);

        if (!bulk && from == to)
        {
            values.Reverse();
        }

        source.RemoveRange(start, movedCount);
        destination.InsertRange(to == "LEFT" ? 0 : destination.Count, values);

        if (destinationEntry is null)
        {
            _entries[args[2]] = new Entry(destination);
        }

        if (source.Count == 0)
        {
            _entries.Remove(args[1]);
        }

        TouchWatchedKey(args[1]);
        TouchWatchedKey(args[2]);
        return FakeReply.Array(values.Select(FakeReply.Bulk).ToArray());
    }

    private static bool TryListMoveTimeout(byte[] argument, out double seconds)
        => double.TryParse(Encoding.ASCII.GetString(argument), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)
            && double.IsFinite(seconds) && seconds >= 0 && seconds < long.MaxValue / 1000d;

    private LinkedListNode<ListMoveWaiter> RegisterListMoveLocked(Connection connection, byte[][] arguments, long started)
    {
        // A null move reply proves the handler accepted every argument, including timeout.
        _ = TryListMoveTimeout(arguments[5], out var seconds);
        var key = arguments[1];

        if (!_listMoveWaiters.TryGetValue(key, out var waiters))
        {
            _listMoveWaiters[key] = waiters = new LinkedList<ListMoveWaiter>();
        }

        return waiters.AddLast(new ListMoveWaiter(connection, arguments, started, seconds));
    }

    private void MarkListMoveSourceChangedLocked(byte[] key)
    {
        if (_listMoveWaiters.ContainsKey(key) && _readyListMoveKeys.Add(key))
        {
            _readyListMoves.Enqueue(key);
        }
    }

    private void ServeReadyListMovesLocked()
    {
        // Process mutations after the whole command (including EXEC) and before another
        // connection can consume its items. Moving into another list can make that key ready.
        while (_readyListMoves.TryDequeue(out var key))
        {
            if (_listMoveWaiters.TryGetValue(key, out var waiters))
            {
                for (var node = waiters.First; node is not null;)
                {
                    var next = node.Next;
                    ServeListMoveLocked(node);
                    node = next;
                }
            }

            // Moves within this pass cannot grow their source. Keep it in the set until
            // the pass ends so its own mutations do not schedule redundant passes.
            _readyListMoveKeys.Remove(key);
        }
    }

    private void ServeListMoveLocked(LinkedListNode<ListMoveWaiter> node)
    {
        var waiter = node.Value;
        var connection = waiter.Connection;

        if (connection.Closed || connection.Lifetime.IsCancellationRequested)
        {
            CompleteListMoveLocked(node, null);
            return;
        }

        if (waiter.RemainingSeconds <= 0)
        {
            CompleteListMoveLocked(node, FakeReply.NullArray);
            return;
        }

        // Already-blocked moves wait through changes to the source's type.
        if (Find(waiter.Arguments[1])?.Data is not List<byte[]>)
        {
            return;
        }

        // An EXACTLY request can remain queued while a later request with a smaller
        // count is eligible. The original node retains its place for the next mutation.
        var result = Execute(connection, waiter.Arguments);

        if (!ReferenceEquals(result, FakeReply.NullArray))
        {
            CompleteListMoveLocked(node, result);
        }
    }

    private void RemoveListMoveLocked(LinkedListNode<ListMoveWaiter> node)
    {
        if (node.List is not { } waiters)
        {
            return;
        }

        waiters.Remove(node);

        if (waiters.Count == 0)
        {
            _listMoveWaiters.Remove(node.Value.Arguments[1]);
        }
    }

    private void CompleteListMoveLocked(LinkedListNode<ListMoveWaiter> node, FakeReply? result)
    {
        var waiter = node.Value;
        RemoveListMoveLocked(node);

        var output = result is null ? null : QueueOutputLocked(waiter.Connection, result.Encode(waiter.Connection.Resp3), push: false);
        waiter.Completion.TrySetResult(output);
    }

    private async Task<Outbound?> ExecuteBlockingListMoveAsync(Connection connection, byte[][] arguments, RespireFakeFaultScope? scope)
    {
        var reply = ExecuteLocked(connection, arguments, scope, out var node, listMoveStarted: Stopwatch.GetTimestamp());

        if (node is null)
        {
            return reply;
        }

        var waiter = node.Value;
        var completion = waiter.Completion.Task;

        try
        {
            var remaining = waiter.RemainingSeconds;

            while (remaining > 0)
            {
                try
                {
                    // Bound each timer while retaining support for server-sized, very long waits.
                    return await completion.WaitAsync(TimeSpan.FromSeconds(Math.Min(remaining, 86_400)),
                        connection.Lifetime.Token).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // A bounded timer may end before the requested deadline.
                    remaining = waiter.RemainingSeconds;
                }
            }

            lock (_gate)
            {
                if (node.List is not null)
                {
                    CompleteListMoveLocked(node, FakeReply.NullArray);
                }
            }

            return await completion.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                RemoveListMoveLocked(node);
            }
        }
    }
}
