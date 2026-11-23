using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Respire.Testing;

public sealed partial class RespireFakeServer
{
    private readonly LinkedList<StreamReadWaiter> _streamReadWaiters = new();
    private readonly Dictionary<byte[], bool> _readyStreamKeys = new(BinaryKeyComparer.Instance);
    private readonly Queue<byte[]> _readyStreams = new();

    private sealed class StreamReadWaiter(Connection connection, byte[][] arguments, StreamRequest request, long timeout)
    {
        private readonly long _started = Stopwatch.GetTimestamp();

        internal Connection Connection
        {
            get;
        } = connection;

        internal byte[][] Arguments
        {
            get;
        } = arguments;

        internal StreamRequest Request
        {
            get;
        } = request;

        internal double RemainingMilliseconds => timeout == 0
            ? double.PositiveInfinity : timeout - Stopwatch.GetElapsedTime(_started).TotalMilliseconds;

        internal TaskCompletionSource<Outbound?> Completion
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static readonly IComparer<byte[]> StreamConsumerOrder = Comparer<byte[]>.Create(
        static (left, right) => left.AsSpan().SequenceCompareTo(right));

    private sealed class FakeStream
    {
        internal SortedDictionary<RespireStreamId, byte[][]> Entries
        {
            get;
        } = [];

        internal Dictionary<byte[], FakeStreamGroup> Groups
        {
            get;
        } = new(BinaryKeyComparer.Instance);
        internal RespireStreamId Last = new("0-0");
        internal RespireStreamId MaxDeletedId = RespireStreamId.Beginning;
        internal long EntriesAdded;
        internal int Duration = 100;
        internal int MaxSize = 100;

        internal Dictionary<byte[], List<StreamIdentity>> Producers
        {
            get;
        } = new(BinaryKeyComparer.Instance);

        internal bool Delete(RespireStreamId id)
        {
            if (!Entries.Remove(id))
            {
                return false;
            }

            if (id > MaxDeletedId)
            {
                MaxDeletedId = id;
            }

            return true;
        }
    }

    private sealed record StreamIdentity(byte[] Identity, string Id, ulong Milliseconds);

    private void ExpireStreamIdentities(FakeStream stream)
    {
        if (stream.Producers.Count == 0)
        {
            return;
        }
        // Sweep all producers on access, including reads and ordinary appends.
        var threshold = Now - stream.Duration * 1000L;
        List<byte[]>? emptyProducers = null;

        foreach (var (producer, identities) in stream.Producers)
        {
            identities.RemoveAll(item => threshold >= 0 && item.Milliseconds <= (ulong)threshold);

            if (identities.Count == 0)
            {
                (emptyProducers ??= []).Add(producer);
            }
        }

        if (emptyProducers is not null)
        {
            foreach (var producer in emptyProducers)
            {
                stream.Producers.Remove(producer);
            }
        }
    }

    private FakeReply StreamConfigure(byte[][] args)
    {
        var stream = Find(args[1])?.Stream;

        if (stream is null)
        {
            return FakeReply.Error("ERR no such key");
        }

        int? duration = null, size = null;

        for (var index = 2; index < args.Length; index += 2)
        {
            var token = Token(args[index]);

            if (index + 1 == args.Length || token is not ("IDMP-DURATION" or "IDMP-MAXSIZE"))
            {
                return Syntax("XCFGSET");
            }

            if (token == "IDMP-DURATION" && duration.HasValue || token == "IDMP-MAXSIZE" && size.HasValue)
            {
                return FakeReply.Error($"ERR {token} specified multiple times");
            }

            var value = Integer(args[index + 1]);
            var maximum = token == "IDMP-DURATION"
                ? RespireStreamConfigurationOptions.MaximumIdempotencyDurationSeconds
                : RespireStreamConfigurationOptions.MaximumIdempotencyMaxSize;

            if (value < 1 || value > maximum)
            {
                return FakeReply.Error($"ERR {token} must be between 1 and {maximum}");
            }

            if (token == "IDMP-DURATION")
            {
                duration = (int)value;
            }
            else
            {
                size = (int)value;
            }
        }

        if (duration is null && size is null)
        {
            return FakeReply.Error("ERR At least one parameter must be specified");
        }

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

        internal HashSet<byte[]> Consumers
        {
            get;
        } = new(BinaryKeyComparer.Instance);

        internal Dictionary<RespireStreamId, FakeStreamPending> Pending
        {
            get;
        } = [];
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
        return StreamId(Encoding.UTF8.GetString(bytes), defaultSequence: 0);
    }

    private static RespireStreamId StreamId(string text, ulong defaultSequence)
    {
        var id = new RespireStreamId(text);

        if (id == RespireStreamId.Min || id == RespireStreamId.Max || id == RespireStreamId.New)
        {
            throw new FormatException();
        }

        _ = id.CompareTo(RespireStreamId.Beginning);
        var parts = id.ToString().Split('-');
        var milliseconds = ulong.Parse(parts[0], CultureInfo.InvariantCulture);
        ulong sequence = parts.Length == 2 ? ulong.Parse(parts[1], CultureInfo.InvariantCulture) : defaultSequence;
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

            if (option == "NOMKSTREAM")
            {
                onlyExisting = true;
                index++;
            }
            else if (option == "IDMP" && idempotency is null && index + 2 < args.Length)
            {
                var producer = args[index + 1];
                var identity = args[index + 2];

                if (producer.Length == 0 || identity.Length == 0)
                {
                    return Syntax("XADD");
                }

                idempotency = (producer, identity);
                index += 3;
            }
            else
            {
                break;
            }
        }

        if (index >= args.Length || args.Length - index < 3 || (args.Length - index) % 2 == 0)
        {
            return WrongArity("XADD");
        }

        var automaticId = Token(args[index]) == "*";
        // Manual idempotency, like Redis, requires an automatically generated entry ID.
        if (idempotency is not null && !automaticId)
        {
            return Syntax("XADD");
        }

        var explicitId = automaticId ? default : StreamId(args[index]);
        var entry = Find(args[1]);

        if (entry is null && onlyExisting)
        {
            return FakeReply.Null;
        }

        var stream = entry?.Stream ?? new FakeStream();
        List<StreamIdentity>? identities = null;

        if (idempotency is { } lookup && stream.Producers.TryGetValue(lookup.Producer, out identities))
        {
            var existing = identities.Find(item => item.Identity.AsSpan().SequenceEqual(lookup.Identity));

            if (existing is not null)
            {
                return FakeReply.Text(existing.Id);
            }
        }

        RespireStreamId id;
        ulong generatedMilliseconds = 0;

        if (automaticId)
        {
            string[] parts = stream.Last.ToString().Split('-');
            ulong lastMilliseconds = ulong.Parse(parts[0], CultureInfo.InvariantCulture);
            ulong lastSequence = ulong.Parse(parts[1], CultureInfo.InvariantCulture);
            ulong milliseconds = Math.Max((ulong)Math.Max(0, Now), lastMilliseconds);

            if (milliseconds == lastMilliseconds && lastSequence == ulong.MaxValue)
            {
                if (milliseconds == ulong.MaxValue)
                {
                    return FakeReply.Error("ERR The stream has exhausted the last possible ID, unable to add more items");
                }

                milliseconds++;
            }

            ulong sequence = milliseconds == lastMilliseconds ? lastSequence + 1 : 0;
            generatedMilliseconds = milliseconds;
            id = new($"{milliseconds.ToString(CultureInfo.InvariantCulture)}-{sequence.ToString(CultureInfo.InvariantCulture)}");
        }
        else
        {
            id = explicitId;
        }

        if (id <= stream.Last)
        {
            return FakeReply.Error("ERR The ID specified in XADD is equal or smaller than the target stream top item");
        }

        stream.Entries.Add(id, args[(index + 1)..]);
        stream.EntriesAdded++;
        stream.Last = id;

        if (idempotency is { } insert)
        {
            if (identities is null)
            {
                stream.Producers[insert.Producer] = identities = [];
            }

            identities.Add(new(insert.Identity, id.ToString(), generatedMilliseconds));

            if (identities.Count > stream.MaxSize)
            {
                identities.RemoveAt(0);
            }
        }

        if (entry is null)
        {
            SetEntry(args[1], new Entry(stream));
        }
        else
        {
            TouchWatchedKey(args[1]);
        }

        MarkStreamReadyLocked(args[1], groupOnly: false);
        return FakeReply.Text(id.ToString());
    }

    private FakeReply StreamCreateGroup(byte[][] args)
    {
        if (Token(args[1]) != "CREATE" || (args.Length == 6 && Token(args[5]) != "MKSTREAM"))
        {
            return Syntax("XGROUP");
        }

        var stream = Find(args[2])?.Stream;

        if (stream is null && args.Length != 6)
        {
            return FakeReply.Error("ERR The XGROUP subcommand requires the key to exist");
        }

        stream ??= new FakeStream();
        var start = Token(args[4]) == "$" ? stream.Last : StreamId(args[4]);
        // Without ENTRIESREAD, Redis keeps this counter unknown until a delivery.
        var group = new FakeStreamGroup(start);

        if (!stream.Groups.TryAdd(args[3], group))
        {
            return FakeReply.Error("BUSYGROUP Consumer Group name already exists");
        }

        if (Find(args[2]) is null)
        {
            SetEntry(args[2], new Entry(stream));
        }
        // Redis does not invalidate WATCH for group metadata on an existing stream.
        return FakeReply.Ok;
    }

    private static StreamRequest ParseStreamRequest(byte[][] args)
    {
        var index = 1;
        byte[]? group = null, consumer = null;

        if (Token(args[0]) == "XREADGROUP")
        {
            if (args.Length < 7 || Token(args[index++]) != "GROUP")
            {
                throw new FormatException();
            }

            group = args[index++];
            consumer = args[index++];
        }

        long? count = null, maxCount = null, maxSize = null;
        long? block = null;

        while (index < args.Length && Token(args[index]) != "STREAMS")
        {
            var option = Token(args[index++]);

            if (index == args.Length)
            {
                throw new FormatException();
            }

            var value = Integer(args[index++]);

            if (value < 0 || (value == 0 && option != "BLOCK"))
            {
                throw new FormatException();
            }

            switch (option)
            {
                case "COUNT":
                    count = value;
                    break;
                case "MAXCOUNT":
                    maxCount = value;
                    break;
                case "MAXSIZE":
                    maxSize = value;
                    break;
                case "BLOCK":
                    block = value;
                    break;
                default:
                    throw new FormatException();
            }
        }

        if (maxCount is { } total && count is { } perStream && total < perStream)
        {
            throw new FormatException();
        }

        if (index >= args.Length || (args.Length - ++index) % 2 != 0 || index == args.Length)
        {
            throw new FormatException();
        }

        var length = (args.Length - index) / 2;
        var keys = args[index..(index + length)];
        var ids = new RespireStreamId[length];

        for (var i = 0; i < length; i++)
        {
            var token = Token(args[index + length + i]);
            ids[i] = token switch
            {
                "$" when group is null => RespireStreamId.New,
                "+" when group is null => RespireStreamId.Max,
                ">" when group is not null => new RespireStreamId(token),
                _ => StreamId(args[index + length + i])
            };
        }

        return new(group, consumer, count ?? long.MaxValue, maxCount ?? long.MaxValue, maxSize ?? long.MaxValue, block, keys, ids);
    }

    private FakeReply StreamRead(Connection connection, byte[][] args)
    {
        var request = ParseStreamRequest(args);
        // Validate every key and group before changing any PEL.
        var streams = request.Keys.Select(key => Find(key)?.Stream).ToArray();

        if (request.Group is not null && streams.Any(stream => stream is null || !stream.Groups.ContainsKey(request.Group)))
        {
            return FakeReply.Error("NOGROUP No such consumer group");
        }

        var pairs = new List<FakeReply>();
        long total = 0, bytes = 0;

        for (var i = 0; i < streams.Length; i++)
        {
            if (total >= request.MaxCount || (total > 0 && bytes >= request.MaxSize))
            {
                break;
            }

            if (streams[i] is not { } stream)
            {
                continue;
            }

            var group = request.Group is null ? null : stream.Groups[request.Group];
            var history = group is not null && request.Ids[i].ToString() != ">";
            var cursor = group is not null && !history ? group.Last
                : request.Ids[i] == RespireStreamId.New ? stream.Last : request.Ids[i];
            using IEnumerator<(RespireStreamId Id, byte[][]? Fields)> candidates = (history
                ? group!.Pending.Where(entry => entry.Key > cursor && !entry.Value.IsReleased
                        && entry.Value.Consumer.AsSpan().SequenceEqual(request.Consumer))
                    .OrderBy(entry => entry.Key)
                    .Select(entry => (Id: entry.Key, Fields: stream.Entries.GetValueOrDefault(entry.Key)))
                : ReadLiveStreamEntries(stream, cursor)
                    .Select(entry => (Id: entry.Key, Fields: (byte[][]?)entry.Value))).GetEnumerator();
            var hasEntry = candidates.MoveNext();

            if (group is not null && (_createConsumersOnEmptyReads || history || hasEntry))
            {
                group.Consumers.Add(request.Consumer!);
            }

            if (!hasEntry && !history)
            {
                continue;
            }

            var keyReply = FakeReply.Bulk(request.Keys[i]);
            // Redis defers outer/entry-list headers; account the stream pair and bulk key now.
            // FakeMatchesRedisAcrossReplyBudgets verifies this framing arithmetic against Redis in RESP2/RESP3.
            bytes += keyReply.Encode(connection.Resp3).Length + (connection.Resp3 ? 0 : 4);
            var entries = new List<FakeReply>();

            while (hasEntry)
            {
                if (entries.Count >= request.Count || total >= request.MaxCount || (total > 0 && bytes >= request.MaxSize))
                {
                    break;
                }

                (RespireStreamId id, byte[][]? fields) = candidates.Current;
                FakeReply reply = StreamFields(id, fields);
                entries.Add(reply);
                bytes += reply.Encode(connection.Resp3).Length;
                total++;

                if (group is not null && !history)
                {
                    AdvanceStreamGroup(stream, group, id);
                    group.Pending[id] = new(request.Consumer!, Now);
                }
                else if (group is not null && fields is not null)
                {
                    FakeStreamPending pending = group.Pending[id];
                    pending.DeliveredAt = Now;
                    pending.DeliveryCount++;
                }

                // Do not advance through any remaining history once the page's budget is exhausted.
                hasEntry = entries.Count < request.Count && total < request.MaxCount && bytes < request.MaxSize
                    && candidates.MoveNext();
            }

            var entryReply = FakeReply.Array(entries.ToArray());
            bytes += 3 + entries.Count.ToString(CultureInfo.InvariantCulture).Length;

            if (connection.Resp3)
            {
                pairs.Add(keyReply);
                pairs.Add(entryReply);
            }
            else
            {
                pairs.Add(FakeReply.Array([keyReply, entryReply]));
            }
        }

        return pairs.Count == 0 ? FakeReply.NullArray
            : connection.Resp3 ? FakeReply.Map(pairs.ToArray()) : FakeReply.Array(pairs.ToArray());
    }

    private static IEnumerable<KeyValuePair<RespireStreamId, byte[][]>> ReadLiveStreamEntries(FakeStream stream, RespireStreamId cursor)
    {
        return cursor == RespireStreamId.Max
            ? stream.Entries.TakeLast(1)
            : stream.Entries.Where(entry => entry.Key > cursor);
    }

    private FakeReply StreamAcknowledge(byte[][] args)
    {
        var stream = Find(args[1])?.Stream;
        var ids = args[3..].Select(StreamId).ToArray();

        if (stream is null || !stream.Groups.TryGetValue(args[2], out var group))
        {
            return FakeReply.Integer(0);
        }

        var removed = ids.Count(group.Pending.Remove);
        return FakeReply.Integer(removed);
    }

    private FakeReply StreamPending(byte[][] args)
    {
        var stream = Find(args[1])?.Stream;

        if (stream is null || !stream.Groups.TryGetValue(args[2], out var group))
        {
            return FakeReply.Error("NOGROUP No such consumer group");
        }

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
            if (args.Length < 8)
            {
                return WrongArity("XPENDING");
            }

            minIdle = Integer(args[++index]);

            if (minIdle < 0)
            {
                return Syntax("XPENDING");
            }

            index++;
        }

        if (args.Length - index is not (3 or 4))
        {
            return WrongArity("XPENDING");
        }

        (RespireStreamId Id, bool Exclusive) start = PendingBound(args[index++], defaultSequence: 0);
        (RespireStreamId Id, bool Exclusive) end = PendingBound(args[index++], defaultSequence: ulong.MaxValue);
        var count = Integer(args[index++]);

        if (count <= 0)
        {
            return Syntax("XPENDING");
        }

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

    private static (RespireStreamId Id, bool Exclusive) PendingBound(byte[] bytes, ulong defaultSequence)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var exclusive = text.StartsWith('(');

        if (exclusive)
        {
            text = text[1..];
        }

        if (text is "-" or "+")
        {
            return (new(text), exclusive);
        }

        return (StreamId(text, defaultSequence), exclusive);
    }

    private static void AdvanceStreamGroup(FakeStream stream, FakeStreamGroup group, RespireStreamId id)
    {
        // A deletion at or beyond the previous cursor can leave a gap in the logical read count.
        group.EntriesRead = group.Last > stream.MaxDeletedId && group.EntriesRead is { } entriesRead
            ? entriesRead + 1
            : EstimateStreamEntriesRead(stream, id);
        group.Last = id;
    }

    private static long? EstimateStreamEntriesRead(FakeStream stream, RespireStreamId id)
    {
        if (id == stream.Last)
        {
            return stream.EntriesAdded;
        }

        RespireStreamId first = stream.Entries.First().Key;
        return id == first && stream.MaxDeletedId < first
            ? stream.EntriesAdded - stream.Entries.Count + 1
            : null;
    }

    private static long? GetStreamGroupLag(FakeStream stream, FakeStreamGroup group)
    {
        if (stream.Entries.Count == 0)
        {
            return 0;
        }

        RespireStreamId first = stream.Entries.First().Key;

        if (group.Last < first && stream.MaxDeletedId < first)
        {
            return stream.Entries.Count;
        }

        long? entriesRead = group.Last > stream.MaxDeletedId ? group.EntriesRead : null;
        entriesRead ??= EstimateStreamEntriesRead(stream, group.Last);
        return stream.EntriesAdded - entriesRead;
    }

    private FakeReply StreamGroupInfo(Connection connection, byte[][] args)
    {
        if (args.Length != 3 || Token(args[1]) != "GROUPS")
        {
            return Syntax("XINFO");
        }

        var stream = Find(args[2])?.Stream;

        if (stream is null)
        {
            return FakeReply.Error("ERR no such key");
        }

        return FakeReply.Array(stream.Groups.Select(pair =>
        {
            var group = pair.Value;
            long? lag = GetStreamGroupLag(stream, group);
            FakeReply[] fields = [FakeReply.Text("name"), FakeReply.Bulk(pair.Key),
                FakeReply.Text("consumers"), FakeReply.Integer(group.Consumers.Count),
                FakeReply.Text("pending"), FakeReply.Integer(group.Pending.Count),
                FakeReply.Text("last-delivered-id"), FakeReply.Text(group.Last.ToString()),
                FakeReply.Text("entries-read"), group.EntriesRead is { } read ? FakeReply.Integer(read) : FakeReply.Null,
                FakeReply.Text("lag"), lag is { } remaining ? FakeReply.Integer(remaining) : FakeReply.Null];
            return connection.Resp3 ? FakeReply.Map(fields) : FakeReply.Array(fields);
        }).ToArray());
    }

    private async Task<Outbound?> ExecuteStreamReadAsync(Connection connection, byte[][] args, RespireFakeFaultScope? scope)
    {
        LinkedListNode<StreamReadWaiter> node;
        long timeout;

        lock (_gate)
        {
            if (_disposed || connection.Closed)
            {
                return null;
            }

            if (connection.Transaction is not null)
            {
                return ExecuteLocked(connection, args, scope, out _);
            }

            StreamRequest request;

            try
            {
                request = ParseStreamRequest(args);
            }
            catch (Exception error) when (error is FormatException or OverflowException)
            {
                return ExecuteLocked(connection, args, scope, out _);
            }

            if (request.Block is not { } block)
            {
                return ExecuteLocked(connection, args, scope, out _);
            }

            timeout = block;

            try
            {
                scope?.ObserveExecution();
                _commandTime = _clock.GetUtcNow().ToUnixTimeMilliseconds();
                // Capture $ at registration so re-execution can see newly appended entries.
                for (int i = 0; i < request.Ids.Length; i++)
                {
                    if (request.Ids[i] == RespireStreamId.New)
                    {
                        var stream = Find(request.Keys[i])?.Data as FakeStream;
                        var last = stream?.Last ?? RespireStreamId.Beginning;
                        args[args.Length - request.Ids.Length + i] = Encoding.UTF8.GetBytes(last.ToString());
                    }
                }

                var reply = Execute(connection, args);

                if (!ReferenceEquals(reply, FakeReply.NullArray))
                {
                    return QueueOutputLocked(connection, reply.Encode(connection.Resp3), push: false);
                }

                node = _streamReadWaiters.AddLast(new StreamReadWaiter(connection, args, request, timeout));
            }
            catch
            {
                connection.Failed = true;
                throw;
            }
            finally
            {
                ServeReadyStreamReadsLocked();
            }
        }

        StreamReadWaiter waiter = node.Value;
        Task<Outbound?> completion = waiter.Completion.Task;

        try
        {
            if (timeout == 0)
            {
                return await completion.WaitAsync(connection.Lifetime.Token).ConfigureAwait(false);
            }

            double remaining = waiter.RemainingMilliseconds;

            while (remaining > 0 && !completion.IsCompleted)
            {
                try
                {
                    var delay = TimeSpan.FromMilliseconds(Math.Clamp(remaining, 1, int.MaxValue));
                    return await completion.WaitAsync(delay, connection.Lifetime.Token).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // A bounded timer may end before the requested deadline.
                    remaining = waiter.RemainingMilliseconds;
                }
            }

            lock (_gate)
            {
                if (node.List is not null)
                {
                    CompleteStreamReadLocked(node, FakeReply.NullArray);
                }
            }

            return await completion.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                node.List?.Remove(node);
            }
        }
    }

    private void MarkStreamReadyLocked(byte[] key, bool groupOnly)
    {
        if (_streamReadWaiters.Count == 0)
        {
            return;
        }

        if (_readyStreamKeys.TryAdd(key, groupOnly))
        {
            _readyStreams.Enqueue(key);
        }
        else if (!groupOnly)
        {
            _readyStreamKeys[key] = false;
        }
    }

    private void ServeReadyStreamReadsLocked()
    {
        // Run after the entire command, including EXEC, while the writer still owns _gate.
        // Consumers claim group entries in registration order before another command can take them.
        while (_readyStreams.TryDequeue(out byte[]? key))
        {
            _readyStreamKeys.Remove(key, out bool groupOnly);

            for (var node = _streamReadWaiters.First; node is not null;)
            {
                var next = node.Next;
                StreamRequest request = node.Value.Request;

                if ((!groupOnly || request.Group is not null) && request.Keys.Contains(key, BinaryKeyComparer.Instance))
                {
                    ServeStreamReadLocked(node);
                }

                node = next;
            }
        }
    }

    private void ServeStreamReadLocked(LinkedListNode<StreamReadWaiter> node)
    {
        StreamReadWaiter waiter = node.Value;
        Connection connection = waiter.Connection;

        if (connection.Closed || connection.Lifetime.IsCancellationRequested)
        {
            CompleteStreamReadLocked(node, null);
            return;
        }

        try
        {
            bool expired = waiter.RemainingMilliseconds <= 0;
            FakeReply reply = expired ? FakeReply.NullArray : Execute(connection, waiter.Arguments);

            if (expired || !ReferenceEquals(reply, FakeReply.NullArray))
            {
                CompleteStreamReadLocked(node, reply);
            }
        }
        catch (Exception error)
        {
            connection.Failed = true;
            node.List?.Remove(node);
            waiter.Completion.TrySetException(error);
        }
    }

    private void CompleteStreamReadLocked(LinkedListNode<StreamReadWaiter> node, FakeReply? reply)
    {
        StreamReadWaiter waiter = node.Value;
        node.List!.Remove(node);
        Connection connection = waiter.Connection;
        var output = reply is null ? null : QueueOutputLocked(connection, reply.Encode(connection.Resp3), push: false);
        waiter.Completion.TrySetResult(output);
    }
}
