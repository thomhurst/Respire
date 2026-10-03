namespace Respire.Internal;

internal enum ReadLatencyKind : byte
{
    Unknown,
    Pending,
    Measured,
}

/// <summary>Separates FIFO occupancy from missing evidence and a measured latency.</summary>
internal readonly record struct ReadLatencyResult
{
    private readonly long _ticks;

    private ReadLatencyResult(ReadLatencyKind kind, long ticks) => (Kind, _ticks) = (kind, ticks);

    internal ReadLatencyKind Kind { get; }
    internal long Ticks => Kind == ReadLatencyKind.Measured ? _ticks
        : throw new InvalidOperationException("This sample has no measured latency.");
    internal static ReadLatencyResult Unknown => default;
    internal static ReadLatencyResult Pending => new(ReadLatencyKind.Pending, 0);

    internal static ReadLatencyResult Measured(long ticks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ticks);
        return new(ReadLatencyKind.Measured, ticks);
    }
}
