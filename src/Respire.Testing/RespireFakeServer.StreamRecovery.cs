using System.Globalization;
using System.Text;
using Respire.Internal;

namespace Respire.Testing;

public sealed partial class RespireFakeServer
{
    private static readonly RespireScript[] WorkerScripts =
        [StreamWorkerScripts.Replay, StreamWorkerScripts.Claim, StreamWorkerScripts.Ack, StreamWorkerScripts.DeadLetter,
            StreamWorkerScripts.CapabilityClaim, StreamWorkerScripts.AckAndDelete, StreamWorkerScripts.Nack];
    private readonly HashSet<string> _workerScriptCache = new(StringComparer.Ordinal);

    private FakeReply StreamDelete(byte[][] args)
    {
        var stream = Find(args[1])?.Stream;
        var ids = args[2..].Select(StreamId).ToArray();
        var removed = stream is null ? 0 : ids.Count(stream.Entries.Remove);
        if (removed > 0) TouchWatchedKey(args[1]);
        return FakeReply.Integer(removed);
    }

    private FakeReply StreamAutoClaim(byte[][] args)
    {
        var stream = Find(args[1])?.Stream;
        if (stream is null || !stream.Groups.TryGetValue(args[2], out var group))
            return FakeReply.Error("NOGROUP No such consumer group");
        var minIdle = Integer(args[4]);
        var start = StreamId(args[5]);
        long count = 100;
        var justIds = false;
        for (var i = 6; i < args.Length; i++)
        {
            if (Token(args[i]) == "COUNT" && i + 1 < args.Length) count = Integer(args[++i]);
            else if (Token(args[i]) == "JUSTID" && !justIds) justIds = true;
            else return Syntax("XAUTOCLAIM");
        }
        if (minIdle < 0 || count <= 0) return Syntax("XAUTOCLAIM");
        var pending = group.Pending.Where(pair => pair.Key >= start).OrderBy(pair => pair.Key).ToArray();
        var entries = new List<FakeReply>();
        var deleted = new List<FakeReply>();
        var scanned = 0;
        while (scanned < pending.Length && scanned < Math.Min(count, int.MaxValue) * 10L && entries.Count + deleted.Count < count)
        {
            var (id, delivery) = pending[scanned++];
            var hasBody = stream.Entries.TryGetValue(id, out var fields);
            if (!hasBody && _autoClaimDeletesPendingEntries)
            {
                group.Pending.Remove(id);
                deleted.Add(FakeReply.Text(id.Value));
                continue;
            }
            if (!delivery.IsReleased && Math.Max(0, Now - delivery.DeliveredAt) < minIdle) continue;
            group.Consumers.Add(args[3]);
            delivery.Consumer = args[3];
            delivery.DeliveredAt = Now;
            delivery.IsReleased = false;
            if (!justIds) delivery.DeliveryCount++;
            var entry = justIds ? FakeReply.Text(id.Value) : FakeReply.Null;
            if (!justIds && hasBody) entry = StreamFields(id, fields!);
            entries.Add(entry);
        }
        var cursor = FakeReply.Text(scanned < pending.Length ? pending[scanned].Key.Value : "0-0");
        return FakeReply.Array(_autoClaimDeletesPendingEntries
            ? [cursor, FakeReply.Array(entries.ToArray()), FakeReply.Array(deleted.ToArray())]
            : [cursor, FakeReply.Array(entries.ToArray())]);
    }

    private static FakeReply StreamFields(RespireStreamId id, byte[][] fields)
        => FakeReply.Array([FakeReply.Text(id.Value), FakeReply.Array(fields.Select(FakeReply.Bulk).ToArray())]);

    private FakeReply StreamWorkerScript(byte[][] args, bool sha)
    {
        var source = Encoding.UTF8.GetString(args[1]);
        var script = WorkerScripts.SingleOrDefault(candidate => (sha ? candidate.Sha1 : candidate.Source) == source);
        if (sha && !_workerScriptCache.Contains(source)) return FakeReply.Error("NOSCRIPT No matching script. Please use EVAL.");
        if (script is null) return FakeReply.Error("ERR fake server supports only the built-in stream worker scripts");
        if (script == StreamWorkerScripts.DeadLetter)
        {
            if (Integer(args[2]) != 2 || args.Length != 11) return Syntax("EVAL");
            if (!sha) _workerScriptCache.Add(script.Sha1);
            return StreamDeadLetter(args);
        }
        if (Integer(args[2]) != 1) return Syntax("EVAL");
        if (!sha) _workerScriptCache.Add(script.Sha1);
        var claim = script == StreamWorkerScripts.Claim || script == StreamWorkerScripts.CapabilityClaim;
        var expected = claim || script == StreamWorkerScripts.Nack ? 9 : 8;
        if (args.Length != expected) return WrongArity("EVAL");
        var stream = Find(args[3])?.Stream;
        if (stream is null || !stream.Groups.TryGetValue(args[4], out var group))
            return FakeReply.Error("NOGROUP No such consumer group");
        if (script == StreamWorkerScripts.Ack || script == StreamWorkerScripts.AckAndDelete || script == StreamWorkerScripts.Nack)
        {
            var id = StreamId(args[6]);
            if (!group.Pending.TryGetValue(id, out var pending)
                || !pending.Consumer.AsSpan().SequenceEqual(args[5]) || pending.DeliveryCount != Integer(args[7]))
                return FakeReply.Integer(0);
            if (script == StreamWorkerScripts.Nack)
            {
                if (_streamWorkerVersion < new Version(8, 8)) return FakeReply.Integer(0);
                if (pending.IsReleased || Math.Max(0, Now - pending.DeliveredAt) < Integer(args[8])) return FakeReply.Integer(0);
                pending.Consumer = [];
                pending.DeliveredAt = 0;
                pending.IsReleased = true;
                return FakeReply.Integer(0);
            }
            group.Pending.Remove(id);
            if (script == StreamWorkerScripts.AckAndDelete && _streamWorkerVersion >= new Version(8, 2)
                && stream.Groups.Values.All(other => other.Last >= id && !other.Pending.ContainsKey(id))
                && stream.Entries.Remove(id))
                TouchWatchedKey(args[3]);
            return FakeReply.Integer(1);
        }

        FakeReply page;
        if (script == StreamWorkerScripts.CapabilityClaim && _streamWorkerVersion >= new Version(8, 4))
            page = StreamWorkerNativeClaim(stream, group, args);
        else if (claim)
        {
            page = StreamAutoClaim(["XAUTOCLAIM"u8.ToArray(), args[3], args[4], args[5], args[6], args[7], "COUNT"u8.ToArray(), args[8]]);
            if (page.Prefix == '-') return page;
        }
        else
        {
            var cursor = StreamId(args[7]);
            var count = Integer(args[6]);
            if (count <= 0) return Syntax("EVAL");
            group.Consumers.Add(args[5]);
            var owned = group.Pending.Where(pair => pair.Key > cursor && !pair.Value.IsReleased
                && pair.Value.Consumer.AsSpan().SequenceEqual(args[5]))
                .OrderBy(pair => pair.Key).Take((int)Math.Min(count, int.MaxValue)).ToArray();
            var entries = new List<FakeReply>();
            foreach (var (id, pending) in owned)
            {
                if (!stream.Entries.TryGetValue(id, out var fields)) continue;
                pending.DeliveredAt = Now;
                pending.DeliveryCount++;
                entries.Add(StreamFields(id, fields));
            }
            page = FakeReply.Array([FakeReply.Text(owned.Length == 0 ? "0-0" : owned[^1].Key.Value), FakeReply.Array(entries.ToArray())]);
        }
        var parts = (FakeReply[])page.Value!;
        var rows = ((FakeReply[])parts[1].Value!).Where(entry => entry.Value is not null).Select(entry =>
        {
            var fields = (FakeReply[])entry.Value!;
            var id = StreamId((byte[])fields[0].Value!);
            return FakeReply.Array([fields[0], fields[1],
                FakeReply.Text(group.Pending[id].DeliveryCount.ToString(CultureInfo.InvariantCulture))]);
        }).ToArray();
        return FakeReply.Array([parts[0], FakeReply.Array(rows)]);
    }

    private FakeReply StreamWorkerNativeClaim(FakeStream stream, FakeStreamGroup group, byte[][] args)
    {
        var idle = Integer(args[6]);
        var count = (int)Math.Min(Integer(args[8]), int.MaxValue);
        if (idle < 0 || count <= 0) return Syntax("XREADGROUP");
        var entries = new List<FakeReply>();
        var selected = 0;
        foreach (var (id, pending) in group.Pending.OrderBy(pair => !pair.Value.IsReleased)
            .ThenBy(pair => pair.Value.DeliveredAt).ThenBy(pair => pair.Key).ToArray())
        {
            if (selected == count) break;
            if (!pending.IsReleased && Math.Max(0, Now - pending.DeliveredAt) < idle) continue;
            selected++;
            if (!stream.Entries.TryGetValue(id, out var fields)) continue;
            pending.Consumer = args[5];
            pending.DeliveredAt = Now;
            pending.IsReleased = false;
            pending.DeliveryCount++;
            entries.Add(StreamFields(id, fields));
        }
        foreach (var (id, fields) in stream.Entries.Where(pair => pair.Key > group.Last).Take(count - selected))
        {
            group.Last = id;
            group.Pending[id] = new(args[5], Now);
            entries.Add(StreamFields(id, fields));
        }
        group.Consumers.Add(args[5]);
        var start = StreamId(args[7]);
        var cleanup = group.Pending.Where(pair => pair.Key >= start).OrderBy(pair => pair.Key)
            .Take((int)Math.Min((long)count + 1, int.MaxValue)).Select(pair => pair.Key).ToArray();
        foreach (var id in cleanup.Take(count))
            if (!stream.Entries.ContainsKey(id)) group.Pending.Remove(id);
        var cursor = cleanup.Length > count ? cleanup[count].Value : "0-0";
        return FakeReply.Array([FakeReply.Text(cursor), FakeReply.Array(entries.ToArray())]);
    }

    private FakeReply StreamDeadLetter(byte[][] args)
    {
        if (args[3].AsSpan().SequenceEqual(args[4]))
            return FakeReply.Error("ERR source and dead-letter keys must differ");
        var source = Find(args[3])?.Stream;
        if (source is null || !source.Groups.TryGetValue(args[5], out var group))
            return FakeReply.Error("NOGROUP No such consumer group");
        var id = StreamId(args[7]);
        if (!group.Pending.TryGetValue(id, out var pending)
            || !pending.Consumer.AsSpan().SequenceEqual(args[6]) || pending.DeliveryCount != Integer(args[8]))
            return FakeReply.Integer(0);
        if (!source.Entries.TryGetValue(id, out var originalFields))
        {
            group.Pending.Remove(id);
            TouchWatchedKey(args[3]);
            return FakeReply.Integer(-1);
        }
        if (originalFields.Length / 2 > StreamWorkerScripts.MaximumDeadLetterFields)
            return FakeReply.Error("ERR dead-letter entries support at most 1024 field/value pairs");
        var target = Find(args[4]);
        if (target is not null && target.Stream is null)
            return FakeReply.Error("WRONGTYPE dead-letter key is not a stream");
        byte[][] fields =
        [
            "_respire.source_id"u8.ToArray(), args[7], "_respire.group"u8.ToArray(), args[5][..Math.Min(256, args[5].Length)],
            "_respire.attempt"u8.ToArray(), args[8], "_respire.reason"u8.ToArray(), args[9][..Math.Min(64, args[9].Length)],
            "_respire.exception_type"u8.ToArray(), args[10][..Math.Min(256, args[10].Length)], .. originalFields,
        ];
        var added = StreamAdd(["XADD"u8.ToArray(), args[4], "*"u8.ToArray(), .. fields]);
        if (added.Prefix == '-') return added;
        group.Pending.Remove(id);
        TouchWatchedKey(args[3]);
        return FakeReply.Integer(1);
    }
}
