using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Respire.Testing;

public sealed partial class RespireFakeServer
{
    private TaskCompletionSource? _listMoveChanged;

    private FakeReply ListMoveMany(byte[][] args, bool blocking)
    {
        var firstOption = blocking ? 6 : 5;
        if (args.Length != firstOption && args.Length != firstOption + 3) return Syntax(Token(args[0]));
        var from = Token(args[3]);
        var to = Token(args[4]);
        if (from is not ("LEFT" or "RIGHT") || to is not ("LEFT" or "RIGHT")) return Syntax(Token(args[0]));
        if (blocking && !TryListMoveTimeout(args[5], out _))
            return FakeReply.Error("ERR timeout is not a valid nonnegative number or is out of range");
        var count = 1L;
        var exactly = false;
        var bulk = false;
        if (args.Length > firstOption)
        {
            var selector = Token(args[firstOption]);
            var ordering = Token(args[firstOption + 2]);
            if (selector is not ("COUNT" or "EXACTLY") || ordering is not ("OBO" or "BULK")) return Syntax(Token(args[0]));
            count = Integer(args[firstOption + 1]);
            if (count <= 0) return FakeReply.Error("ERR count should be greater than 0");
            exactly = selector == "EXACTLY";
            bulk = ordering == "BULK";
        }

        var source = Find(args[1])?.List;
        if (source is null || source.Count == 0 || exactly && source.Count < count) return FakeReply.NullArray;
        // Validate the destination before changing the source. Preserve the entry and its TTL
        // when moving within one list, even if the selected block includes every element.
        var destinationEntry = Find(args[2]);
        var destination = destinationEntry?.List ?? [];
        var movedCount = (int)Math.Min(count, source.Count);
        var start = from == "LEFT" ? 0 : source.Count - movedCount;
        var values = source.GetRange(start, movedCount);
        if (!bulk && from == to) values.Reverse();
        source.RemoveRange(start, movedCount);
        destination.InsertRange(to == "LEFT" ? 0 : destination.Count, values);
        if (destinationEntry is null) _entries[args[2]] = new Entry(destination);
        if (source.Count == 0) _entries.Remove(args[1]);
        TouchWatchedKey(args[1]);
        TouchWatchedKey(args[2]);
        return FakeReply.Array(values.Select(FakeReply.Bulk).ToArray());
    }

    private static bool TryListMoveTimeout(byte[] argument, out double seconds)
        => double.TryParse(Encoding.ASCII.GetString(argument), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)
            && double.IsFinite(seconds) && seconds >= 0 && seconds < long.MaxValue / 1000d;

    private async Task<Outbound?> ExecuteBlockingListMoveAsync(Connection connection, byte[][] arguments, RespireFakeFaultScope? scope)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            var reply = ExecuteLocked(connection, arguments, scope, out var changed, allowListMoveWait: true);
            scope = null; // A blocked command is one fault execution, however often it wakes.
            if (changed is null) return reply;
            // A null move reply proves the handler accepted every argument, including timeout.
            _ = TryListMoveTimeout(arguments[5], out var seconds);
            var remaining = seconds == 0 ? double.PositiveInfinity : seconds - Stopwatch.GetElapsedTime(started).TotalSeconds;
            if (remaining <= 0) return QueueListMoveTimeout(connection);
            try
            {
                // Bound each timer while retaining support for server-sized, very long waits.
                await changed.WaitAsync(TimeSpan.FromSeconds(Math.Min(remaining, 86_400)), connection.Lifetime.Token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                if (seconds != 0 && Stopwatch.GetElapsedTime(started).TotalSeconds >= seconds)
                    return QueueListMoveTimeout(connection);
            }
        }
    }

    private Outbound? QueueListMoveTimeout(Connection connection)
    {
        lock (_gate)
        {
            if (_disposed || connection.Closed) return null;
            return QueueOutputLocked(connection, FakeReply.NullArray.Encode(connection.Resp3), push: false);
        }
    }
}
