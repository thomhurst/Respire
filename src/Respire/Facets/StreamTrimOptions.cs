using System.Globalization;

namespace Respire;

/// <summary>Options for XTRIM. Exactly one of MaxLength and MinId must be set.</summary>
public readonly record struct StreamTrimOptions
{
    /// <summary>Maximum number of entries to retain. Must be non-negative.</summary>
    public long? MaxLength { get; init; }

    /// <summary>Evicts entries older than this numeric stream id. Requires Redis 6.2+.</summary>
    public RespireStreamId? MinId { get; init; }

    /// <summary>Allows whole-node trimming that can retain extra entries. Defaults to false.</summary>
    /// <remarks>Unlike XTRIM, <see cref="StreamAddOptions.ApproximateTrim"/> defaults to true for XADD.</remarks>
    public bool Approximate { get; init; }

    /// <summary>Limits trimming work; requires approximate trimming and Redis 6.2+. Zero disables the limit.</summary>
    public long? Limit { get; init; }

    internal int ValidateAndCountArguments(bool requireThreshold)
    {
        if (MaxLength.HasValue && MinId.HasValue)
            throw new ArgumentException("MAXLEN and MINID are mutually exclusive trimming thresholds.");
        var hasThreshold = MaxLength.HasValue || MinId.HasValue;
        if (requireThreshold && !hasThreshold)
            throw new ArgumentException("A MAXLEN or MINID trimming threshold is required.");
        if (MaxLength is { } length) ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (MinId is { } id)
        {
            var text = id.Value.AsSpan();
            var separator = text.IndexOf('-');
            var milliseconds = separator < 0 ? text : text[..separator];
            if (!ulong.TryParse(milliseconds, NumberStyles.None, CultureInfo.InvariantCulture, out _)
                || (separator >= 0 && !ulong.TryParse(text[(separator + 1)..], NumberStyles.None,
                    CultureInfo.InvariantCulture, out _)))
                throw new ArgumentException("MINID requires a numeric stream id.", nameof(MinId));
        }
        if (Limit is { } limit)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(limit);
            if (!hasThreshold || !Approximate)
                throw new ArgumentException("LIMIT requires a trimming threshold and approximate trimming.", nameof(Limit));
        }
        return hasThreshold ? 2 + (Approximate ? 1 : 0) + (Limit.HasValue ? 2 : 0) : 0;
    }

    // Called only after validation; shared by immediate and deferred XADD/XTRIM encoders.
    internal int CopyArgumentsTo(Span<RespireValue> arguments)
    {
        if (!MaxLength.HasValue && !MinId.HasValue) return 0;
        var index = 0;
        arguments[index++] = MaxLength.HasValue ? "MAXLEN" : "MINID";
        if (Approximate) arguments[index++] = "~";
        arguments[index++] = MaxLength is { } length ? (RespireValue)length : MinId!.Value.Value;
        if (Limit is { } limit)
        {
            arguments[index++] = "LIMIT";
            arguments[index++] = limit;
        }
        return index;
    }
}
