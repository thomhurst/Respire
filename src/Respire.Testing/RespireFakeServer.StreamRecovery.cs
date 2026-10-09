using System.Globalization;
using System.Text;
using Respire.Internal;

namespace Respire.Testing;

public sealed partial class RespireFakeServer
{
    private static readonly RespireScript[] WorkerScripts =
        [StreamWorkerScripts.Replay, StreamWorkerScripts.Claim, StreamWorkerScripts.Ack, StreamWorkerScripts.DeadLetter];
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
            if (Math.Max(0, Now - delivery.DeliveredAt) < minIdle) continue;
            group.Consumers.Add(args[3]);
            delivery.Consumer = args[3];
            delivery.DeliveredAt = Now;
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
        var expected = script == StreamWorkerScripts.Claim ? 9 : 8;
        if (args.Length != expected) return WrongArity("EVAL");
        var stream = Find(args[3])?.Stream;
        if (stream is null || !stream.Groups.TryGetValue(args[4], out var group))
            return FakeReply.Error("NOGROUP No such consumer group");
        if (script == StreamWorkerScripts.Ack)
        {
            var id = StreamId(args[6]);
            if (!group.Pending.TryGetValue(id, out var pending)
                || !pending.Consumer.AsSpan().SequenceEqual(args[5]) || pending.DeliveryCount != Integer(args[7]))
                return FakeReply.Integer(0);
            group.Pending.Remove(id);
            return FakeReply.Integer(1);
        }

        FakeReply page;
        if (script == StreamWorkerScripts.Claim)
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
            var owned = group.Pending.Where(pair => pair.Key > cursor && pair.Value.Consumer.AsSpan().SequenceEqual(args[5]))
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
            return FakeReply.Integer(1);
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
