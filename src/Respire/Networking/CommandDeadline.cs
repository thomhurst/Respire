namespace Respire.Networking;

/// <summary>
/// An absolute command deadline in <see cref="Environment.TickCount64"/> milliseconds, plus
/// whether a MOVING reroute has already added the maintenance relaxed-timeout allowance to it.
/// </summary>
/// <remarks>
/// Both parts share one <see cref="long"/>, so a pending response's deadline is still written
/// and read as a single value; the deadline sweep reads it while a producer may re-stamp it.
/// Code outside this type sees only <see cref="Ticks"/> and <see cref="IsRelaxed"/>, so the
/// encoding cannot leak into tick arithmetic.
/// </remarks>
internal readonly struct CommandDeadline
{
    // TickCount64 counts milliseconds since boot and cannot reach this bit.
    private const long RelaxedFlag = 1L << 62;
    private readonly long _value;

    private CommandDeadline(long value) => _value = value;

    internal long RawValue => _value;
    internal static CommandDeadline FromRawValue(long value) => new(value);

    /// <summary>No deadline.</summary>
    public static CommandDeadline None => default;

    /// <summary>
    /// A deadline <paramref name="timeoutMilliseconds"/> from now, or <see cref="None"/> for 0.
    /// TickCount64 starts at 0 and timeouts are positive, so a real deadline is never 0.
    /// </summary>
    public static CommandDeadline After(long timeoutMilliseconds)
        => timeoutMilliseconds == 0 ? default : new(Environment.TickCount64 + timeoutMilliseconds);

    /// <summary>A deadline at an absolute tick value. 0 means <see cref="None"/>.</summary>
    public static CommandDeadline At(long ticks) => new(ticks);

    public bool IsSet => _value != 0;

    /// <summary>The absolute deadline in ticks, or 0 when there is none.</summary>
    public long Ticks => _value & ~RelaxedFlag;

    /// <summary>Milliseconds left on the TickCount64 clock; no deadline has an unbounded budget.</summary>
    internal long RemainingMilliseconds => IsSet ? Math.Max(0L, Ticks - Environment.TickCount64) : long.MaxValue;

    /// <summary>True when a reroute has already added the relaxed-timeout allowance.</summary>
    public bool IsRelaxed => (_value & RelaxedFlag) != 0;

    /// <summary>Extends the deadline and records that the relaxed allowance has been used.</summary>
    public CommandDeadline Relax(long extensionMilliseconds) => new((Ticks + extensionMilliseconds) | RelaxedFlag);
}
