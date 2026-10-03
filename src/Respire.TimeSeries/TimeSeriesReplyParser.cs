using Respire.Protocol;

namespace Respire.TimeSeries;

/// <summary>Parses RedisTimeSeries replies, in both RESP2 and RESP3 shapes, into the public model types.</summary>
internal static class TimeSeriesReplyParser
{
    /// <summary>Parses a TS.RANGE or TS.REVRANGE reply.</summary>
    internal static RespireTimeSeriesRangeResult ParseRange(RespireResult result) => new(ParseSampleList(result));

    /// <summary>Parses a TS.GET reply, which is <c>[timestamp, value]</c> or an empty array (or null) for a series without samples.</summary>
    internal static RespireTimeSeriesSample? ParseLatest(RespireResult result)
        => result.IsNull || result.Count == 0 ? null : ParseSample(result);

    /// <param name="result">The TS.MGET, TS.MRANGE, or TS.MREVRANGE reply.</param>
    /// <param name="latestSample">True for TS.MGET, whose series hold one optional sample instead of a list.</param>
    internal static IReadOnlyList<RespireTimeSeriesSeries> ParseSeries(RespireResult result, bool latestSample)
    {
        if (result.Type == RespDataType.Map)
        {
            // RESP3: key => [labels, ...metadata maps..., samples]. MGET has no metadata and one sample.
            var mapped = new List<RespireTimeSeriesSeries>(result.Count / 2);
            for (var index = 0; index + 1 < result.Count; index += 2)
            {
                var data = result[index + 1];
                if (data.Count < 2) throw UnexpectedReply();
                mapped.Add(new(
                    new RespireKey(result[index].AsBytes()),
                    ParseLabels(data[0]),
                    ParseSeriesSamples(data[data.Count - 1], latestSample)));
            }
            return mapped;
        }

        // RESP2: [key, labels, samples] per series.
        var series = new List<RespireTimeSeriesSeries>(result.Count);
        foreach (var item in result)
        {
            if (item.Count != 3) throw UnexpectedReply();
            series.Add(new(new RespireKey(item[0].AsBytes()), ParseLabels(item[1]), ParseSeriesSamples(item[2], latestSample)));
        }
        return series;
    }

    /// <summary>Parses a TS.INFO reply. RESP2 replies are flat name/value arrays and RESP3 replies are maps.</summary>
    internal static RespireTimeSeriesInfo ParseInfo(RespireResult result)
    {
        // Both shapes index as name/value pairs.
        if (result.Type is not (RespDataType.Map or RespDataType.Array) || result.Count % 2 != 0)
            throw UnexpectedReply();

        long totalSamples = 0;
        long memoryUsageBytes = 0;
        long firstTimestamp = 0;
        long lastTimestamp = 0;
        long retentionMilliseconds = 0;
        long chunkCount = 0;
        long chunkSizeBytes = 0;
        string? chunkType = null;
        RespireTimeSeriesDuplicatePolicy? duplicatePolicy = null;
        long ignoreMaxTimeDifference = 0;
        double ignoreMaxValueDifference = 0;
        IReadOnlyDictionary<string, string?> labels = RespireTimeSeriesInfo.EmptyLabels;
        RespireKey? sourceKey = null;
        IReadOnlyList<RespireTimeSeriesRule> rules = [];
        for (var index = 0; index + 1 < result.Count; index += 2)
        {
            var value = result[index + 1];
            switch (result[index].AsString())
            {
                case "totalSamples": totalSamples = value.AsInteger(); break;
                case "memoryUsage": memoryUsageBytes = value.AsInteger(); break;
                case "firstTimestamp": firstTimestamp = value.AsInteger(); break;
                case "lastTimestamp": lastTimestamp = value.AsInteger(); break;
                case "retentionTime": retentionMilliseconds = value.AsInteger(); break;
                case "chunkCount": chunkCount = value.AsInteger(); break;
                case "chunkSize": chunkSizeBytes = value.AsInteger(); break;
                case "chunkType": chunkType = value.IsNull ? null : value.AsString(); break;
                case "duplicatePolicy": duplicatePolicy = RespireTimeSeriesOptions.FromPolicy(value.IsNull ? null : value.AsString()); break;
                case "ignoreMaxTimeDiff": ignoreMaxTimeDifference = value.AsInteger(); break;
                case "ignoreMaxValDiff": ignoreMaxValueDifference = value.AsDouble(); break;
                case "labels": labels = ParseLabels(value); break;
                case "sourceKey": sourceKey = value.IsNull ? (RespireKey?)null : new RespireKey(value.AsBytes()); break;
                case "rules": rules = ParseRules(value); break;
            }
        }
        return new RespireTimeSeriesInfo
        {
            TotalSamples = totalSamples,
            MemoryUsageBytes = memoryUsageBytes,
            FirstTimestamp = firstTimestamp,
            LastTimestamp = lastTimestamp,
            RetentionMilliseconds = retentionMilliseconds,
            ChunkCount = chunkCount,
            ChunkSizeBytes = chunkSizeBytes,
            ChunkType = chunkType,
            DuplicatePolicy = duplicatePolicy,
            IgnoreMaxTimeDifference = ignoreMaxTimeDifference,
            IgnoreMaxValueDifference = ignoreMaxValueDifference,
            Labels = labels,
            SourceKey = sourceKey,
            Rules = rules,
        };
    }

    internal static InvalidOperationException UnexpectedReply()
        => new("Unexpected RedisTimeSeries reply shape.");

    private static Dictionary<string, string?> ParseLabels(RespireResult labels)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (labels.Type == RespDataType.Map)
        {
            for (var index = 0; index + 1 < labels.Count; index += 2)
                result[labels[index].AsString()] = ReadLabelValue(labels[index + 1]);
            return result;
        }
        foreach (var pair in labels)
        {
            if (pair.Count != 2) throw UnexpectedReply();
            result[pair[0].AsString()] = ReadLabelValue(pair[1]);
        }
        return result;
    }

    private static string? ReadLabelValue(RespireResult value) => value.IsNull ? null : value.AsString();

    private static IReadOnlyList<RespireTimeSeriesSample> ParseSeriesSamples(RespireResult samples, bool latestSample)
        => latestSample ? ParseLatestSample(samples) : ParseSampleList(samples);

    // TS.MGET returns [timestamp, value], or an empty array for a series without samples.
    private static IReadOnlyList<RespireTimeSeriesSample> ParseLatestSample(RespireResult sample)
        => sample.Count == 0 ? [] : [ParseSample(sample)];

    // Ranges return [[timestamp, value], ...].
    private static IReadOnlyList<RespireTimeSeriesSample> ParseSampleList(RespireResult samples)
    {
        var result = new List<RespireTimeSeriesSample>(samples.Count);
        foreach (var sample in samples) result.Add(ParseSample(sample));
        return result;
    }

    private static RespireTimeSeriesSample ParseSample(RespireResult sample)
    {
        if (sample.Count != 2) throw UnexpectedReply();
        return new(sample[0].AsInteger(), sample[1].AsDouble());
    }

    private static RespireTimeSeriesRule[] ParseRules(RespireResult rules)
    {
        if (rules.Type == RespDataType.Map)
        {
            // RESP3: destination => [bucketDuration, aggregator, alignment].
            var mapped = new RespireTimeSeriesRule[rules.Count / 2];
            for (var index = 0; index + 1 < rules.Count; index += 2)
                mapped[index / 2] = ParseRule(rules[index], rules[index + 1], offset: 0);
            return mapped;
        }

        // RESP2: [destination, bucketDuration, aggregator, alignment] per rule.
        var parsed = new RespireTimeSeriesRule[rules.Count];
        for (var index = 0; index < parsed.Length; index++)
        {
            var rule = rules[index];
            if (rule.Count < 3) throw UnexpectedReply();
            parsed[index] = ParseRule(rule[0], rule, offset: 1);
        }
        return parsed;
    }

    private static RespireTimeSeriesRule ParseRule(RespireResult destination, RespireResult fields, int offset)
    {
        if (fields.Count < offset + 2) throw UnexpectedReply();
        var align = fields.Count > offset + 2 ? fields[offset + 2].AsInteger() : 0;
        return new(new RespireKey(destination.AsBytes()), fields[offset].AsInteger(), fields[offset + 1].AsString(), align);
    }
}
