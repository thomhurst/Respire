using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

public partial interface IStringCommands
{
    /// <summary>Atomically increments with optional bounds and expiry, returning the value and applied delta. Redis 8.8+: INCREX BYINT.</summary>
    /// <remarks>Rejection without Saturate preserves both value and TTL. Older servers return an unsupported-command error; this method does not emulate INCREX.</remarks>
    ValueTask<RespireIncrementResult<long>> IncrementExtendedAsync(RespireKey key, long by = 1,
        IntegerIncrementOptions options = default, CancellationToken cancellationToken = default);

    /// <summary>Atomically increments a floating-point value with optional bounds and expiry. Redis 8.8+: INCREX BYFLOAT.</summary>
    /// <remarks>Redis uses long double arithmetic; returned doubles can lose precision or become infinite beyond the .NET double range. Rejected bounds preserve value and TTL.</remarks>
    ValueTask<RespireIncrementResult<double>> IncrementExtendedAsync(RespireKey key, double by,
        FloatIncrementOptions options = default, CancellationToken cancellationToken = default);
}

internal sealed partial class StringCommands
{
    public ValueTask<RespireIncrementResult<long>> IncrementExtendedAsync(RespireKey key, long by = 1,
        IntegerIncrementOptions options = default, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return client.ConvertResponseAsync("INCREX", BuildIncrementExtended(client, key, by, options),
            cancellationToken, this, static (StringCommands _, in RespValue value) => ReadIntegerIncrement(in value));
    }

    public ValueTask<RespireIncrementResult<double>> IncrementExtendedAsync(RespireKey key, double by,
        FloatIncrementOptions options = default, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return client.ConvertResponseAsync("INCREX", BuildIncrementExtended(client, key, by, options),
            cancellationToken, this, static (StringCommands _, in RespValue value) => ReadFloatIncrement(in value));
    }

    internal static Cmd1N BuildIncrementExtended(RespireClient client, RespireKey key, long by, IntegerIncrementOptions options)
    {
        // Lifted comparisons are false when either bound is absent, allowing one-sided bounds.
        if (options.LowerBound > options.UpperBound)
            throw new ArgumentException("LowerBound cannot exceed UpperBound.", nameof(options));
        return BuildIncrementExtended(client, key, "BYINT", by,
            new(options.LowerBound, options.UpperBound), options.Saturate, options.Expiry, options.ExpireOnlyWhenPersistent, nameof(options));
    }

    internal static Cmd1N BuildIncrementExtended(RespireClient client, RespireKey key, double by, FloatIncrementOptions options)
    {
        if (!double.IsFinite(by)) throw new ArgumentOutOfRangeException(nameof(by), "The increment must be finite.");
        // The lifted ordering comparison only rejects bounds when both are present.
        if (options.LowerBound is { } lower && double.IsNaN(lower) ||
            options.UpperBound is { } upper && double.IsNaN(upper) || options.LowerBound > options.UpperBound)
            throw new ArgumentException("Bounds cannot be NaN and LowerBound cannot exceed UpperBound.", nameof(options));
        return BuildIncrementExtended(client, key, "BYFLOAT", by,
            new(options.LowerBound, options.UpperBound), options.Saturate, options.Expiry, options.ExpireOnlyWhenPersistent, nameof(options));
    }

    private readonly record struct IncrementBounds(RespireValue? Lower, RespireValue? Upper)
    {
        internal int TokenCount => (Lower.HasValue ? 2 : 0) + (Upper.HasValue ? 2 : 0);
    }

    private static Cmd1N BuildIncrementExtended(RespireClient client, RespireKey key, string mode, RespireValue by,
        IncrementBounds bounds, bool saturate, RespireExpiry expiry, bool enx, string optionsParameterName)
    {
        var relative = expiry.TryGetRelativeMilliseconds(out var milliseconds);
        var absolute = expiry.TryGetAbsoluteUnixMilliseconds(out var timestamp);
        if (relative && milliseconds <= 0 || absolute && timestamp <= 0)
            throw new ArgumentOutOfRangeException(optionsParameterName, "INCREX expiry must be positive.");
        if (enx && !relative && !absolute)
            throw new ArgumentException("ENX requires a relative or absolute expiry.", optionsParameterName);
        var expiryCount = relative || absolute ? 2 : expiry.IsPersist ? 1 : 0;
        var arguments = new RespireValue[2 + bounds.TokenCount +
            (saturate ? 1 : 0) + expiryCount + (enx ? 1 : 0)];
        var index = 0;
        arguments[index++] = mode;
        arguments[index++] = by;
        if (bounds.Lower is { } lower) { arguments[index++] = "LBOUND"; arguments[index++] = lower; }
        if (bounds.Upper is { } upper) { arguments[index++] = "UBOUND"; arguments[index++] = upper; }
        if (saturate) arguments[index++] = "SATURATE";
        if (relative || absolute)
        {
            arguments[index++] = relative ? CommandOptionFrames.PXValue : CommandOptionFrames.PXATValue;
            arguments[index++] = relative ? milliseconds : timestamp;
        }
        else if (expiry.IsPersist) arguments[index++] = "PERSIST";
        if (enx) arguments[index] = "ENX";
        return new Cmd1N(RespireCommands.String.INCREX.Verb, client.Key(in key), arguments);
    }

    internal static RespireIncrementResult<long> ReadIntegerIncrement(in RespValue value)
    {
        var pair = IncrementPair(in value);
        if (pair[0].Type != RespDataType.Integer || pair[1].Type != RespDataType.Integer)
            throw new RespireProtocolException("INCREX BYINT must return two integers.");
        return new(pair[0].AsInteger(), pair[1].AsInteger());
    }

    internal static RespireIncrementResult<double> ReadFloatIncrement(in RespValue value)
    {
        var pair = IncrementPair(in value);
        return new(ReadFloat(in pair[0]), ReadFloat(in pair[1]));

        static double ReadFloat(in RespValue element)
        {
            if (element.Type is not (RespDataType.Double or RespDataType.BulkString))
                throw new RespireProtocolException("INCREX BYFLOAT must return two floating-point numbers.");
            var number = ResponseReader.Double(in element);
            if (double.IsNaN(number)) throw new RespireProtocolException("INCREX BYFLOAT cannot return NaN.");
            return number;
        }
    }

    private static ReadOnlySpan<RespValue> IncrementPair(in RespValue value)
    {
        if (value.Type != RespDataType.Array || value.AsArray().Length != 2)
            throw new RespireProtocolException("INCREX must return an array of exactly two numbers.");
        return value.AsArray();
    }
}
