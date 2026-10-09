using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

public partial interface IStringCommands
{
    /// <summary>Returns inclusive byte ranges and total subsequence length. Redis: LCS IDX (7.0+).</summary>
    /// <remarks>Both keys must share a Cluster slot after prefixing. Filtering does not change the total length.</remarks>
    ValueTask<RespireLcsIndexResult> LcsIndexAsync(RespireKey firstKey, RespireKey secondKey,
        RespireLcsOptions? options = null, CancellationToken cancellationToken = default);
}

internal sealed partial class StringCommands
{
    public ValueTask<RespireLcsIndexResult> LcsIndexAsync(RespireKey firstKey, RespireKey secondKey,
        RespireLcsOptions? options = null, CancellationToken cancellationToken = default)
    {
        return client.ConvertResponseAsync("LCS", LcsIndexCommand(client, firstKey, secondKey, options, cancellationToken),
            cancellationToken, this,
            static (StringCommands _, in RespValue value) => ParseLcsIndex(in value));
    }

    internal static CmdN LcsIndexCommand(RespireClient client, RespireKey firstKey, RespireKey secondKey,
        RespireLcsOptions? options, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return CreateLcsIndexCommand(client, firstKey, secondKey, options);
        }
        catch (Exception error)
        {
            // Construction has no transport attempts. Dispatch and deferred execution
            // retain their existing owners after this boundary succeeds.
            RespireTelemetry.RecordError(error, internallyHandled: false);
            throw;
        }
    }

    private static CmdN CreateLcsIndexCommand(RespireClient client, RespireKey firstKey, RespireKey secondKey,
        RespireLcsOptions? options)
    {
        if (options?.MinimumMatchLength is { } minimum) ArgumentOutOfRangeException.ThrowIfNegative(minimum);
        var args = new RespireValue[3 + (options?.MinimumMatchLength is not null ? 2 : 0)
            + (options?.IncludeMatchLength == true ? 1 : 0)];
        args[0] = client.Key(in firstKey);
        args[1] = client.Key(in secondKey);
        if (client.Core.Cluster is not null) EnsureSameSlot(args.AsSpan(0, 2), stride: 1, operation: "LCS");
        args[2] = "IDX";
        var index = 3;
        if (options?.MinimumMatchLength is { } minimumMatchLength)
        {
            args[index++] = "MINMATCHLEN";
            args[index++] = minimumMatchLength;
        }
        if (options?.IncludeMatchLength == true) args[index] = "WITHMATCHLEN";
        return new(RespireCommands.String.LCS.Verb, args);
    }

    internal static RespireLcsIndexResult ParseLcsIndex(in RespValue value)
    {
        var fields = value.AsArray();
        if (value.Type is not (RespDataType.Array or RespDataType.Map) || fields.Length != 4)
            throw new RespireProtocolException("An LCS IDX reply must contain matches and len fields.");
        RespireLcsMatch[]? matches = null;
        long? length = null;
        for (var i = 0; i < fields.Length; i += 2)
        {
            if (fields[i].AsSpan().SequenceEqual("matches"u8) && matches is null)
                matches = ParseLcsMatches(in fields[i + 1]);
            else if (fields[i].AsSpan().SequenceEqual("len"u8) && length is null)
                length = LcsNonnegativeInteger(in fields[i + 1]);
            else
                throw new RespireProtocolException("An LCS IDX reply contains an unknown or duplicate field.");
        }
        if (matches is null || length is null)
            throw new RespireProtocolException("An LCS IDX reply must contain matches and len fields.");
        return new(matches, length.Value);
    }

    private static RespireLcsMatch[] ParseLcsMatches(in RespValue value)
    {
        if (value.Type != RespDataType.Array)
            throw new RespireProtocolException("LCS matches must be an array.");
        var entries = value.AsArray();
        var matches = new RespireLcsMatch[entries.Length];
        for (var i = 0; i < entries.Length; i++)
        {
            var match = entries[i].AsArray();
            if (entries[i].Type != RespDataType.Array || match.Length is not (2 or 3))
                throw new RespireProtocolException("An LCS match must contain two ranges and an optional length.");
            matches[i] = new(ParseLcsRange(in match[0]), ParseLcsRange(in match[1]),
                match.Length == 3 ? LcsNonnegativeInteger(in match[2]) : null);
        }
        return matches;
    }

    private static RespireLcsRange ParseLcsRange(in RespValue value)
    {
        var pair = value.AsArray();
        if (value.Type != RespDataType.Array || pair.Length != 2)
            throw new RespireProtocolException("An LCS range must contain exactly two offsets.");
        var start = LcsNonnegativeInteger(in pair[0]);
        var end = LcsNonnegativeInteger(in pair[1]);
        if (end < start) throw new RespireProtocolException("An LCS range end must not precede its start.");
        return new(start, end);
    }

    private static long LcsNonnegativeInteger(in RespValue value)
    {
        if (value.Type != RespDataType.Integer || value.AsInteger() < 0)
            throw new RespireProtocolException("LCS offsets and lengths must be nonnegative integers.");
        return value.AsInteger();
    }
}
