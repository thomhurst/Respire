using Respire.Protocol;

namespace Respire;

internal static class ServerDiagnosticsParser
{
    internal static string Text(in RespValue value)
    {
        if (value.Type is not (RespDataType.SimpleString or RespDataType.BulkString or RespDataType.VerbatimString))
            throw new RespireProtocolException("Server diagnostic text must be a string.");
        return value.AsString();
    }

    internal static long NonnegativeInteger(in RespValue value)
    {
        if (value.Type != RespDataType.Integer || value.AsInteger() < 0)
            throw new RespireProtocolException("Server diagnostic count must be a nonnegative integer.");
        return value.AsInteger();
    }

    private static ReadOnlySpan<RespValue> Array(in RespValue value)
    {
        if (value.Type != RespDataType.Array) throw new RespireProtocolException("Server diagnostic rows must be an array.");
        return value.AsArray();
    }

    private static ReadOnlySpan<RespValue> Pairs(in RespValue value)
    {
        if (value.Type is not (RespDataType.Array or RespDataType.Map))
            throw new RespireProtocolException("Server diagnostic fields must be a map or an array of pairs.");
        var items = value.AsArray();
        if (items.Length % 2 != 0) throw new RespireProtocolException("Server diagnostic field/value pair is incomplete.");
        return items;
    }

    internal static RespireLatencyHistorySample[] History(in RespValue value)
    {
        var rows = Array(in value);
        var result = new RespireLatencyHistorySample[rows.Length];
        for (var index = 0; index < rows.Length; index++)
        {
            var row = Array(in rows[index]);
            if (row.Length != 2) throw new RespireProtocolException("A latency history sample requires a timestamp and latency.");
            var seconds = NonnegativeInteger(in row[0]);
            var milliseconds = NonnegativeInteger(in row[1]);
            try
            {
                result[index] = new(DateTimeOffset.FromUnixTimeSeconds(seconds),
                    TimeSpan.FromTicks(checked(milliseconds * TimeSpan.TicksPerMillisecond)));
            }
            catch (Exception error) when (error is ArgumentOutOfRangeException or OverflowException)
            {
                throw new RespireProtocolException("Latency history time is outside the supported range.", error);
            }
        }
        return result;
    }

    internal static RespireLatencyHistogram[] Histograms(in RespValue value)
    {
        var commands = Pairs(in value);
        var result = new RespireLatencyHistogram[commands.Length / 2];
        for (var index = 0; index < result.Length; index++)
        {
            var command = Text(in commands[index * 2]);
            var fields = Pairs(in commands[index * 2 + 1]);
            long? calls = null;
            RespireLatencyHistogramBucket[]? buckets = null;
            var additional = new Dictionary<string, RespireResult>(StringComparer.Ordinal);
            for (var field = 0; field < fields.Length; field += 2)
            {
                var name = Text(in fields[field]);
                var item = fields[field + 1];
                switch (name)
                {
                    case "calls" when calls is null:
                        calls = NonnegativeInteger(in item);
                        break;
                    case "histogram_usec" when buckets is null:
                        buckets = Buckets(in item);
                        break;
                    case "calls" or "histogram_usec":
                        throw new RespireProtocolException("Latency histogram contains a duplicate required field.");
                    default:
                        if (!additional.TryAdd(name, RespireResult.CreateOwned(in item)))
                            throw new RespireProtocolException("Latency histogram contains a duplicate additional field.");
                        break;
                }
            }
            if (calls is null || buckets is null)
                throw new RespireProtocolException("Latency histogram requires calls and histogram_usec.");
            if (buckets.Length != 0 && buckets[^1].CumulativeCount > calls.Value)
                throw new RespireProtocolException("Latency histogram counts exceed the total calls.");
            // Redis can emit the same command repeatedly when the request names overlap.
            result[index] = new(command, calls.Value, buckets, additional);
        }
        return result;
    }

    private static RespireLatencyHistogramBucket[] Buckets(in RespValue value)
    {
        var pairs = Pairs(in value);
        var result = new RespireLatencyHistogramBucket[pairs.Length / 2];
        long previousBound = -1, previousCount = 0;
        for (var index = 0; index < result.Length; index++)
        {
            var bound = NonnegativeInteger(in pairs[index * 2]);
            var count = NonnegativeInteger(in pairs[index * 2 + 1]);
            if (bound <= previousBound || count < previousCount)
                throw new RespireProtocolException("Latency histogram bounds and cumulative counts must increase monotonically.");
            result[index] = new(bound, count);
            previousBound = bound;
            previousCount = count;
        }
        return result;
    }
}
