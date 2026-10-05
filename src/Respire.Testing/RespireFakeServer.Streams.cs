using System.Globalization;
using System.Text;

namespace Respire.Testing;

public sealed partial class RespireFakeServer
{
    private TaskCompletionSource _streamChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class FakeStream
    {
        internal SortedDictionary<RespireStreamId, byte[][]> Entries { get; } = [];
        internal Dictionary<byte[], FakeStreamGroup> Groups { get; } = new(BinaryKeyComparer.Instance);
        internal RespireStreamId Last;
    }

    private sealed class FakeStreamGroup(RespireStreamId last)
    {
        internal RespireStreamId Last = last;
        internal Dictionary<RespireStreamId, byte[]> Pending { get; } = [];
    }

    private sealed record StreamRequest(byte[]? Group, byte[]? Consumer, long Count, long MaxCount,
        long MaxSize, long? Block, byte[][] Keys, RespireStreamId[] Ids);

    private static RespireStreamId StreamId(byte[] bytes)
    {
        var id = new RespireStreamId(Encoding.UTF8.GetString(bytes));
        if (id == RespireStreamId.Min || id == RespireStreamId.Max || id == RespireStreamId.New)
            throw new FormatException();
        _ = id.CompareTo(RespireStreamId.Beginning);
        var parts = id.ToString().Split('-');
        var milliseconds = ulong.Parse(parts[0], CultureInfo.InvariantCulture);
        var sequence = parts.Length == 2 ? ulong.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
        return new RespireStreamId($"{milliseconds.ToString(CultureInfo.InvariantCulture)}-{sequence.ToString(CultureInfo.InvariantCulture)}");
    }

    private FakeReply StreamAdd(byte[][] args)
    {
        // Unsupported append options are rejected rather than silently approximated.
        if (args.Length % 2 == 0) return Syntax("XADD");
        var stream = Find(args[1])?.Stream ?? new FakeStream();
        RespireStreamId id;
        if (Token(args[2]) == "*")
        {
            var parts = stream.Last.ToString().Split('-');
            var lastTime = ulong.Parse(parts[0], CultureInfo.InvariantCulture);
            var time = Math.Max((ulong)Math.Max(0, Now), lastTime);
            var sequence = time == lastTime && parts.Length == 2 ? checked(ulong.Parse(parts[1], CultureInfo.InvariantCulture) + 1) : 0;
            if (time == 0 && sequence == 0) sequence = 1;
            id = new($"{time.ToString(CultureInfo.InvariantCulture)}-{sequence.ToString(CultureInfo.InvariantCulture)}");
        }
        else id = StreamId(args[2]);
        if (id <= stream.Last) return FakeReply.Error("ERR The ID specified in XADD is equal or smaller than the target stream top item");
        stream.Entries.Add(id, args[3..]);
        stream.Last = id;
        if (Find(args[1]) is null) SetEntry(args[1], new Entry(stream));
        else TouchWatchedKey(args[1]);
        var changed = _streamChanged;
        _streamChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        changed.TrySetResult();
        return FakeReply.Text(id.ToString());
    }

    private FakeReply StreamCreateGroup(byte[][] args)
    {
        if (Token(args[1]) != "CREATE" || (args.Length == 6 && Token(args[5]) != "MKSTREAM"))
            return Syntax("XGROUP");
        var stream = Find(args[2])?.Stream;
        if (stream is null && args.Length != 6) return FakeReply.Error("ERR The XGROUP subcommand requires the key to exist");
        stream ??= new FakeStream();
        var start = Token(args[4]) == "$" ? stream.Last : StreamId(args[4]);
        if (!stream.Groups.TryAdd(args[3], new(start))) return FakeReply.Error("BUSYGROUP Consumer Group name already exists");
        if (Find(args[2]) is null) SetEntry(args[2], new Entry(stream));
        else TouchWatchedKey(args[2]);
        return FakeReply.Ok;
    }

    private static StreamRequest ParseStreamRequest(byte[][] args)
    {
        var index = 1;
        byte[]? group = null, consumer = null;
        if (Token(args[0]) == "XREADGROUP")
        {
            if (args.Length < 7 || Token(args[index++]) != "GROUP") throw new FormatException();
            group = args[index++];
            consumer = args[index++];
        }
        long? count = null, maxCount = null, maxSize = null;
        long? block = null;
        while (index < args.Length && Token(args[index]) != "STREAMS")
        {
            var option = Token(args[index++]);
            if (index == args.Length) throw new FormatException();
            var value = Integer(args[index++]);
            if (value < 0 || (value == 0 && option != "BLOCK")) throw new FormatException();
            switch (option)
            {
                case "COUNT": count = value; break;
                case "MAXCOUNT": maxCount = value; break;
                case "MAXSIZE": maxSize = value; break;
                case "BLOCK": block = value; break;
                default: throw new FormatException();
            }
        }
        if (maxCount is { } total && count is { } perStream && total < perStream) throw new FormatException();
        if (index >= args.Length || (args.Length - ++index) % 2 != 0 || index == args.Length) throw new FormatException();
        var length = (args.Length - index) / 2;
        var keys = args[index..(index + length)];
        var ids = new RespireStreamId[length];
        for (var i = 0; i < length; i++)
        {
            var token = Token(args[index + length + i]);
            ids[i] = (group is null && token == "$") || (group is not null && token == ">")
                ? new RespireStreamId(token) : StreamId(args[index + length + i]);
        }
        return new(group, consumer, count ?? long.MaxValue, maxCount ?? long.MaxValue, maxSize ?? long.MaxValue, block, keys, ids);
    }

    private FakeReply StreamRead(Connection connection, byte[][] args)
    {
        var request = ParseStreamRequest(args);
        // Validate every key and group before changing any PEL.
        var streams = request.Keys.Select(key => Find(key)?.Stream).ToArray();
        if (request.Group is not null && streams.Any(stream => stream is null || !stream.Groups.ContainsKey(request.Group)))
            return FakeReply.Error("NOGROUP No such consumer group");
        var pairs = new List<FakeReply>();
        long total = 0, bytes = 0;
        for (var i = 0; i < streams.Length; i++)
        {
            if (total >= request.MaxCount || (total > 0 && bytes >= request.MaxSize)) break;
            if (streams[i] is not { } stream) continue;
            var group = request.Group is null ? null : stream.Groups[request.Group];
            var history = group is not null && request.Ids[i].ToString() != ">";
            var cursor = group is not null && !history ? group.Last
                : request.Ids[i] == RespireStreamId.New ? stream.Last : request.Ids[i];
            using var candidates = stream.Entries.Where(entry => entry.Key > cursor
                && (!history || group!.Pending.TryGetValue(entry.Key, out var owner) && owner.AsSpan().SequenceEqual(request.Consumer))).GetEnumerator();
            var hasEntry = candidates.MoveNext();
            if (!hasEntry && !history) continue;
            var keyReply = FakeReply.Bulk(request.Keys[i]);
            // Redis defers outer/entry-list headers; account the stream pair and bulk key now.
            // FakeMatchesRedisAcrossReplyBudgets verifies this framing arithmetic against Redis in RESP2/RESP3.
            bytes += keyReply.Encode(connection.Resp3).Length + (connection.Resp3 ? 0 : 4);
            var entries = new List<FakeReply>();
            while (hasEntry)
            {
                if (entries.Count >= request.Count || total >= request.MaxCount || (total > 0 && bytes >= request.MaxSize)) break;
                var entry = candidates.Current;
                var reply = FakeReply.Array([FakeReply.Text(entry.Key.ToString()), FakeReply.Array(entry.Value.Select(FakeReply.Bulk).ToArray())]);
                entries.Add(reply);
                bytes += reply.Encode(connection.Resp3).Length;
                total++;
                if (group is not null && !history)
                {
                    group.Last = entry.Key;
                    group.Pending[entry.Key] = request.Consumer!;
                    TouchWatchedKey(request.Keys[i]);
                }
                // Do not advance through any remaining history once the page's budget is exhausted.
                hasEntry = entries.Count < request.Count && total < request.MaxCount && bytes < request.MaxSize
                    && candidates.MoveNext();
            }
            var entryReply = FakeReply.Array(entries.ToArray());
            bytes += 3 + entries.Count.ToString(CultureInfo.InvariantCulture).Length;
            if (connection.Resp3) { pairs.Add(keyReply); pairs.Add(entryReply); }
            else pairs.Add(FakeReply.Array([keyReply, entryReply]));
        }
        return pairs.Count == 0 ? FakeReply.NullArray
            : connection.Resp3 ? FakeReply.Map(pairs.ToArray()) : FakeReply.Array(pairs.ToArray());
    }

    private FakeReply StreamAcknowledge(byte[][] args)
    {
        var stream = Find(args[1])?.Stream;
        var ids = args[3..].Select(StreamId).ToArray();
        if (stream is null || !stream.Groups.TryGetValue(args[2], out var group)) return FakeReply.Integer(0);
        var removed = ids.Count(group.Pending.Remove);
        if (removed != 0) TouchWatchedKey(args[1]);
        return FakeReply.Integer(removed);
    }

    private async Task<Outbound?> ExecuteStreamReadAsync(Connection connection, byte[][] args, RespireFakeFaultScope? scope)
    {
        StreamRequest request;
        lock (_gate)
        {
            if (connection.Transaction is not null) return ExecuteLocked(connection, args, scope);
            try { request = ParseStreamRequest(args); }
            catch (Exception error) when (error is FormatException or OverflowException)
            {
                return ExecuteLocked(connection, args, scope);
            }
            if (request.Block is null) return ExecuteLocked(connection, args, scope);
            // Resolve $ once for the whole blocking call. Retrying after a wake must not skip new entries.
            args = args.ToArray();
            for (var i = 0; i < request.Ids.Length; i++)
            {
                if (request.Ids[i] != RespireStreamId.New) continue;
                try
                {
                    _commandTime = _clock.GetUtcNow().ToUnixTimeMilliseconds();
                    var last = Find(request.Keys[i])?.Stream.Last ?? RespireStreamId.Beginning;
                    args[args.Length - request.Ids.Length + i] = Encoding.UTF8.GetBytes(last.ToString());
                }
                catch (WrongTypeException) { return ExecuteLocked(connection, args, scope); }
                catch { connection.Failed = true; throw; }
            }
        }
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var observed = false;
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_disposed || connection.Closed) return null;
                try
                {
                    if (!observed) { scope?.ObserveExecution(); observed = true; }
                    _commandTime = _clock.GetUtcNow().ToUnixTimeMilliseconds();
                    var reply = Execute(connection, args);
                    var expired = request.Block > 0 && elapsed.Elapsed.TotalMilliseconds >= request.Block.Value;
                    if (reply != FakeReply.NullArray || expired)
                        return QueueOutputLocked(connection, reply.Encode(connection.Resp3), push: false);
                    changed = _streamChanged.Task;
                }
                catch { connection.Failed = true; throw; }
            }
            try
            {
                if (request.Block == 0) await changed.WaitAsync(connection.Lifetime.Token).ConfigureAwait(false);
                else
                {
                    var remaining = Math.Max(1, request.Block!.Value - elapsed.Elapsed.TotalMilliseconds);
                    await changed.WaitAsync(TimeSpan.FromMilliseconds(Math.Min(remaining, int.MaxValue)), connection.Lifetime.Token)
                        .ConfigureAwait(false);
                }
            }
            catch (TimeoutException) { }
        }
    }
}
