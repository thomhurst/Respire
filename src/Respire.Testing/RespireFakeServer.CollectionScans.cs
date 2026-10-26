using System.Buffers.Text;
using System.Globalization;

namespace Respire.Testing;

public sealed partial class RespireFakeServer
{
    private FakeReply CollectionScan(byte[][] args, string command)
    {
        if (!Utf8Parser.TryParse(args[2], out ulong cursor, out var consumed) || consumed != args[2].Length)
            return FakeReply.Error("ERR invalid cursor");
        long count = 10;
        byte[]? pattern = null;
        var noValues = false;
        for (var index = 3; index < args.Length; index++)
        {
            var option = Token(args[index]);
            if (option == "NOVALUES" && command == "HSCAN")
            {
                noValues = true;
                continue;
            }
            if (index + 1 == args.Length) return Syntax(command);
            switch (option)
            {
                case "MATCH": pattern = args[++index]; break;
                case "COUNT":
                    count = Integer(args[++index]);
                    if (count <= 0) return Syntax(command);
                    break;
                default: return Syntax(command);
            }
        }

        var entry = Find(args[1]);
        var set = command == "SSCAN" ? entry?.Set : null;
        var hash = command == "HSCAN" ? entry?.Hash : null;
        var sortedSet = command == "ZSCAN" ? entry?.SortedSet : null;
        IEnumerable<byte[]> members = command switch
        {
            "SSCAN" => set ?? [],
            "HSCAN" => hash?.Keys.AsEnumerable() ?? [],
            _ => sortedSet?.Keys.AsEnumerable() ?? [],
        };
        var replies = new List<FakeReply>();
        if (entry is not null && cursor < ScanBucketCount)
        {
            // Fixed buckets keep deletion between pages from shifting unvisited members behind the cursor.
            var buckets = members.ToLookup(member => BinaryKeyComparer.Instance.GetHashCode(member) & (ScanBucketCount - 1));
            var remainingWork = ArrayGlobWorkLimit;
            long visited = 0;
            try
            {
                do
                {
                    foreach (var member in buckets[(int)cursor])
                    {
                        visited++;
                        if (pattern is not null && !KeyspaceGlob(member, pattern, ref remainingWork)) continue;
                        replies.Add(FakeReply.Bulk(member));
                        if (hash is not null && !noValues) replies.Add(FakeReply.Bulk(hash[member]));
                        if (sortedSet is not null)
                        {
                            var score = sortedSet[member];
                            var text = score switch
                            {
                                double.PositiveInfinity => "inf",
                                double.NegativeInfinity => "-inf",
                                _ => score.ToString("R", CultureInfo.InvariantCulture),
                            };
                            replies.Add(FakeReply.Text(text));
                        }
                    }
                    cursor++;
                } while (cursor < ScanBucketCount && visited < count);
            }
            catch (ArrayGlobLimitException) { return KeyspaceGlobLimit(command); }
        }
        else cursor = 0;
        if (cursor >= ScanBucketCount) cursor = 0;
        return FakeReply.Array([FakeReply.Text(cursor.ToString(CultureInfo.InvariantCulture)), FakeReply.Array(replies.ToArray())]);
    }
}
