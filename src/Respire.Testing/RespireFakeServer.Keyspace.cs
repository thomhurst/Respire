using System.Buffers.Text;
using System.Globalization;

namespace Respire.Testing;

public sealed partial class RespireFakeServer
{
    private const int ScanBucketCount = 256;

    private FakeReply ServerTime()
    {
        var seconds = _commandTimestamp.ToUnixTimeSeconds();
        var microseconds = _commandTimestamp.UtcTicks % TimeSpan.TicksPerSecond / 10;
        return FakeReply.Array([FakeReply.Text(seconds.ToString(CultureInfo.InvariantCulture)),
            FakeReply.Text(microseconds.ToString(CultureInfo.InvariantCulture))]);
    }

    private FakeReply Keys(byte[] pattern)
    {
        var replies = new List<FakeReply>();
        var remainingWork = ArrayGlobWorkLimit;
        // Find can remove expired keys, so never enumerate the live dictionary here.
        try
        {
            foreach (var key in _entries.Keys.ToArray())
            {
                if (Find(key) is not null && ArrayGlob(key, pattern, ignoreCase: false, ref remainingWork))
                    replies.Add(FakeReply.Bulk(key));
            }
        }
        catch (ArrayGlobLimitException) { return KeyspaceGlobLimit("KEYS"); }
        return FakeReply.Array(replies.ToArray());
    }

    private FakeReply Scan(byte[][] args)
    {
        if (!Utf8Parser.TryParse(args[1], out ulong cursor, out var consumed) || consumed != args[1].Length)
            return FakeReply.Error("ERR invalid cursor");
        long count = 10;
        byte[]? pattern = null;
        for (var index = 2; index < args.Length; index += 2)
        {
            if (index + 1 == args.Length) return Syntax("SCAN");
            switch (Token(args[index]))
            {
                case "MATCH": pattern = args[index + 1]; break;
                case "COUNT":
                    count = Integer(args[index + 1]);
                    if (count <= 0) return Syntax("SCAN");
                    break;
                default: return Syntax("SCAN");
            }
        }

        var replies = new List<FakeReply>();
        // A cursor names the next fixed bucket, not an offset in the current key list.
        // Deletion/expiry between calls must not shift surviving keys behind the cursor.
        // Process a whole bucket before yielding: COUNT is a hint, including before MATCH.
        if (cursor < ScanBucketCount)
        {
            var buckets = _entries.Keys.ToLookup(key => BinaryKeyComparer.Instance.GetHashCode(key) & (ScanBucketCount - 1));
            var remainingWork = ArrayGlobWorkLimit;
            long visited = 0;
            try
            {
                do
                {
                    foreach (var key in buckets[(int)cursor])
                    {
                        visited++;
                        if (Find(key) is not null && (pattern is null || ArrayGlob(key, pattern, ignoreCase: false, ref remainingWork)))
                            replies.Add(FakeReply.Bulk(key));
                    }
                    cursor++;
                } while (cursor < ScanBucketCount && visited < count);
            }
            catch (ArrayGlobLimitException) { return KeyspaceGlobLimit("SCAN"); }
        }
        if (cursor >= ScanBucketCount) cursor = 0;
        return FakeReply.Array([FakeReply.Text(cursor.ToString(CultureInfo.InvariantCulture)), FakeReply.Array(replies.ToArray())]);
    }

    private static FakeReply KeyspaceGlobLimit(string command)
        => FakeReply.Error($"ERR Respire.Testing {command} glob exceeded its work limit");
}
