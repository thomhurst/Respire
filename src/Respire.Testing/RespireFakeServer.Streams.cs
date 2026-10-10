using System.Globalization;
using System.Text;

namespace Respire.Testing;

public sealed partial class RespireFakeServer
{
    private TaskCompletionSource _streamChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly IComparer<byte[]> StreamConsumerOrder = Comparer<byte[]>.Create(
        static (left, right) => left.AsSpan().SequenceCompareTo(right));

    private sealed class FakeStream
    {
        internal SortedDictionary<RespireStreamId, byte[][]> Entries { get; } = [];
        internal Dictionary<byte[], FakeStreamGroup> Groups { get; } = new(BinaryKeyComparer.Instance);
        internal RespireStreamId Last = new("0-0");
        internal long EntriesAdded;
        internal int Duration = 100;
        internal int MaxSize = 100;
        internal Dictionary<byte[], List<StreamIdentity>> Producers { get; } = new(BinaryKeyComparer.Instance);
    }

    private sealed record StreamIdentity(byte[] Identity, string Id, ulong Milliseconds);

    private void ExpireStreamIdentities(FakeStream stream)
    {
        if (stream.Producers.Count == 0) return;
        // Sweep all producers on access, including reads and ordinary appends.
        var threshold = Now - stream.Duration * 1000L;
        List<byte[]>? emptyProducers = null;
        foreach (var (producer, identities) in stream.Producers)
        {
            identities.RemoveAll(item => threshold >= 0 && item.Milliseconds <= (ulong)threshold);
            if (identities.Count == 0) (emptyProducers ??= []).Add(producer);
        }
        if (emptyProducers is not null)
            foreach (var producer in emptyProducers) stream.Producers.Remove(producer);
    }

    private FakeReply StreamConfigure(byte[][] args)
    {
        var stream = Find(args[1])?.Stream;
        if (stream is null) return FakeReply.Error("ERR no such key");
        int? duration = null, size = null;
        for (var index = 2; index < args.Length; index += 2)
        {
            var token = Token(args[index]);
            if (index + 1 == args.Length || token is not ("IDMP-DURATION" or "IDMP-MAXSIZE")) return Syntax("XCFGSET");
            if (token == "IDMP-DURATION" && duration.HasValue || token == "IDMP-MAXSIZE" && size.HasValue)
                return FakeReply.Error($"ERR {token} specified multiple times");
            var value = Integer(args[index + 1]);
            var maximum = token == "IDMP-DURATION"
                ? StreamConfigurationOptions.MaximumIdempotencyDurationSeconds
                : StreamConfigurationOptions.MaximumIdempotencyMaxSize;
            if (value < 1 || value > maximum)
                return FakeReply.Error($"ERR {token} must be between 1 and {maximum}");
            if (token == "IDMP-DURATION") duration = (int)value;
            else size = (int)value;
        }
        if (duration is null && size is null) return FakeReply.Error("ERR At least one parameter must be specified");
        if (duration is { } seconds && seconds != stream.Duration || size is { } maximumSize && maximumSize != stream.MaxSize)
        {
            stream.Duration = duration ?? stream.Duration;
            stream.MaxSize = size ?? stream.MaxSize;
            stream.Producers.Clear();
            // Redis metadata changes do not invalidate WATCH, even when identities are cleared.
        }
        return FakeReply.Ok;
    }

    private sealed class FakeStreamGroup(RespireStreamId last)
    {
        internal RespireStreamId Last = last;
        internal long? EntriesRead;
        internal HashSet<byte[]> Consumers { get; } = new(BinaryKeyComparer.Instance);
        internal Dictionary<RespireStreamId, FakeStreamPending> Pending { get; } = [];
    }

    private sealed class FakeStreamPending(byte[] consumer, long deliveredAt)
    {
        internal byte[] Consumer = consumer;
        internal long DeliveredAt = deliveredAt;
        internal long DeliveryCount = 1;
        internal bool IsReleased;
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
        var index = 2;
        var onlyExisting = false;
        (byte[] Producer, byte[] Identity)? idempotency = null;
        while (index < args.Length)
        {
            var option = Token(args[index]);
            if (option == "NOMKSTREAM") { onlyExisting = true; index++; }
            else if (option == "IDMP" && idempotency is null && index + 2 < args.Length)
            {
                var producer = args[index + 1];
                var identity = args[index + 2];
                if (producer.Length == 0 || identity.Length == 0) return Syntax("XADD");
                idempotency = (producer, identity);
                index += 3;
            }
            else break;
        }
        if (index >= args.Length || args.Length - index < 3 || (args.Length - index) % 2 == 0)
            return WrongArity("XADD");
        var automaticId = Token(args[index]) == "*";
        // Manual idempotency, like Redis, requires an automatically generated entry ID.
        if (idempotency is not null && !automaticId) return Syntax("XADD");
        var explicitId = automaticId ? default : StreamId(args[index]);
        var entry = Find(args[1]);
        if (entry is null && onlyExisting) return FakeReply.Null;
        var stream = entry?.Stream ?? new FakeStream();
        List<StreamIdentity>? identities = null;
        if (idempotency is { } lookup && stream.Producers.TryGetValue(lookup.Producer, out identities))
        {
            var existing = identities.Find(item => item.Identity.AsSpan().SequenceEqual(lookup.Identity));
            if (existing is not null) return FakeReply.Text(existing.Id);
        }
        RespireStreamId id;
        ulong generatedMilliseconds = 0;
        if (automaticId)
        {
            var parts = stream.Last.ToString().Split('-');
            var lastTime = ulong.Parse(parts[0], CultureInfo.InvariantCulture);
            var time = Math.Max((ulong)Math.Max(0, Now), lastTime);
            generatedMilliseconds = time;
            var sequence = time == lastTime && parts.Length == 2 ? checked(ulong.Parse(parts[1], CultureInfo.InvariantCulture) + 1) : 0;
            if (time == 0 && sequence == 0) sequence = 1;
            id = new($"{time.ToString(CultureInfo.InvariantCulture)}-{sequence.ToString(CultureInfo.InvariantCulture)}");
        }
        else id = explicitId;
        if (id <= stream.Last) return FakeReply.Error("ERR The ID specified in XADD is equal or smaller than the target stream top item");
        stream.Entries.Add(id, args[(index + 1)..]);
        stream.EntriesAdded++;
        stream.Last = id;
        if (idempotency is { } insert)
        {
            if (identities is null) stream.Producers[insert.Producer] = identities = [];
            identities.Add(new(insert.Identity, id.ToString(), generatedMilliseconds));
            if (identities.Count > stream.MaxSize) identities.RemoveAt(0);
        }
        if (entry is null) SetEntry(args[1], new Entry(stream));
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
        // Without ENTRIESREAD, Redis keeps this counter unknown until a delivery.
        var group = new FakeStreamGroup(start);
        if (!stream.Groups.TryAdd(args[3], group)) return FakeReply.Error("BUSYGROUP Consumer Group name already exists");
        if (Find(args[2]) is null) SetEntry(args[2], new Entry(stream));
        // Redis does not invalidate WATCH for group metadata on an existing stream.
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
        if (_createConsumersOnEmptyReads && request.Group is not null)
            foreach (var stream in streams) stream!.Groups[request.Group].Consumers.Add(request.Consumer!);
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
                && (!history || group!.Pending.TryGetValue(entry.Key, out var pending)
                    && !pending.IsReleased && pending.Consumer.AsSpan().SequenceEqual(request.Consumer))).GetEnumerator();
            var hasEntry = candidates.MoveNext();
            if (group is not null && (history || hasEntry)) group.Consumers.Add(request.Consumer!);
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
                    if (group.EntriesRead.HasValue) group.EntriesRead++;
                    else group.EntriesRead = EstimateStreamEntriesRead(stream, entry.Key);
                    group.Pending[entry.Key] = new(request.Consumer!, Now);
                }
                else if (group is not null)
                {
                    group.Pending[entry.Key].DeliveredAt = Now;
                    group.Pending[entry.Key].DeliveryCount++;
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
        return FakeReply.Integer(removed);
    }

    private FakeReply StreamPending(byte[][] args)
    {
        var stream = Find(args[1])?.Stream;
        if (stream is null || !stream.Groups.TryGetValue(args[2], out var group))
            return FakeReply.Error("NOGROUP No such consumer group");
        var pending = group.Pending.OrderBy(entry => entry.Key).ToArray();
        if (args.Length == 3)
        {
            var consumers = group.Pending.Values.Where(entry => !entry.IsReleased)
                .GroupBy(entry => entry.Consumer, BinaryKeyComparer.Instance)
                .OrderBy(entries => entries.Key, StreamConsumerOrder)
                .Select(entries => FakeReply.Array([FakeReply.Bulk(entries.Key),
                    FakeReply.Text(entries.Count().ToString(CultureInfo.InvariantCulture))])).ToArray();
            return FakeReply.Array([FakeReply.Integer(group.Pending.Count),
                group.Pending.Count == 0 ? FakeReply.Null : FakeReply.Text(pending[0].Key.ToString()),
                group.Pending.Count == 0 ? FakeReply.Null : FakeReply.Text(pending[^1].Key.ToString()),
                group.Pending.Count == 0 ? FakeReply.NullArray : FakeReply.Array(consumers)]);
        }

        var index = 3;
        long minIdle = 0;
        if (Token(args[index]) == "IDLE")
        {
            if (args.Length < 8) return WrongArity("XPENDING");
            minIdle = Integer(args[++index]);
            if (minIdle < 0) return Syntax("XPENDING");
            index++;
        }
        if (args.Length - index is not (3 or 4)) return WrongArity("XPENDING");
        var start = PendingBound(args[index++]);
        var end = PendingBound(args[index++]);
        var count = Integer(args[index++]);
        if (count <= 0) return Syntax("XPENDING");
        var consumer = index == args.Length ? null : args[index];
        return FakeReply.Array(pending.Where(entry =>
            (start.Exclusive ? entry.Key > start.Id : entry.Key >= start.Id)
            && (end.Exclusive ? entry.Key < end.Id : entry.Key <= end.Id)
            && (entry.Value.IsReleased || Math.Max(0, Now - entry.Value.DeliveredAt) >= minIdle)
            && (consumer is null || !entry.Value.IsReleased && entry.Value.Consumer.AsSpan().SequenceEqual(consumer)))
            .Take((int)Math.Min(count, int.MaxValue))
            .Select(entry => FakeReply.Array([FakeReply.Text(entry.Key.ToString()), FakeReply.Bulk(entry.Value.Consumer),
                FakeReply.Integer(entry.Value.IsReleased ? -1 : Math.Max(0, Now - entry.Value.DeliveredAt)), FakeReply.Integer(entry.Value.DeliveryCount)]))
            .ToArray());
    }

    private static (RespireStreamId Id, bool Exclusive) PendingBound(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var exclusive = text.StartsWith('(');
        if (exclusive) text = text[1..];
        if (text is "-" or "+") return (new(text), exclusive);
        return (StreamId(Encoding.UTF8.GetBytes(text)), exclusive);
    }

    private static long? EstimateStreamEntriesRead(FakeStream stream, RespireStreamId id)
    {
        if (stream.EntriesAdded == 0) return 0;
        if (id == stream.Last) return stream.EntriesAdded;
        if (stream.Entries.Count == 0 || stream.EntriesAdded != stream.Entries.Count) return null;
        var first = stream.Entries.First().Key;
        if (id < first) return 0;
        return id == first ? 1 : null;
    }

    private FakeReply StreamGroupInfo(Connection connection, byte[][] args)
    {
        if (args.Length != 3 || Token(args[1]) != "GROUPS") return Syntax("XINFO");
        var stream = Find(args[2])?.Stream;
        if (stream is null) return FakeReply.Error("ERR no such key");
        return FakeReply.Array(stream.Groups.Select(pair =>
        {
            var group = pair.Value;
            // Lag may be independently knowable without assigning EntriesRead.
            var entriesRead = group.EntriesRead ?? EstimateStreamEntriesRead(stream, group.Last);
            FakeReply[] fields = [FakeReply.Text("name"), FakeReply.Bulk(pair.Key),
                FakeReply.Text("consumers"), FakeReply.Integer(group.Consumers.Count),
                FakeReply.Text("pending"), FakeReply.Integer(group.Pending.Count),
                FakeReply.Text("last-delivered-id"), FakeReply.Text(group.Last.ToString()),
                FakeReply.Text("entries-read"), group.EntriesRead is { } read ? FakeReply.Integer(read) : FakeReply.Null,
                FakeReply.Text("lag"), entriesRead is { } logicalCount
                    ? FakeReply.Integer(stream.EntriesAdded - logicalCount) : FakeReply.Null];
            return connection.Resp3 ? FakeReply.Map(fields) : FakeReply.Array(fields);
        }).ToArray());
    }

    private async Task<Outbound?> ExecuteStreamReadAsync(Connection connection, byte[][] args, RespireFakeFaultScope? scope)
    {
        StreamRequest request;
        lock (_gate)
        {
            if (connection.Transaction is not null) return ExecuteLocked(connection, args, scope, out _);
            try { request = ParseStreamRequest(args); }
            catch (Exception error) when (error is FormatException or OverflowException)
            {
                return ExecuteLocked(connection, args, scope, out _);
            }
            if (request.Block is null) return ExecuteLocked(connection, args, scope, out _);
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
                catch (WrongTypeException) { return ExecuteLocked(connection, args, scope, out _); }
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
