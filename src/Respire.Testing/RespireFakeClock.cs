namespace Respire.Testing;

/// <summary>A manually advanced UTC clock for fake-server key expiry. It does not control client I/O deadlines.</summary>
/// <remarks>Only GetUtcNow is overridden. CreateTimer and monotonic timestamps retain system-clock behavior.</remarks>
public sealed class RespireFakeClock : TimeProvider
{
    private long _ticks;

    /// <summary>Creates a clock starting at the supplied instant, or the Unix epoch.</summary>
    public RespireFakeClock(DateTimeOffset? start = null) => _ticks = (start ?? DateTimeOffset.UnixEpoch).UtcTicks;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    /// <summary>Moves expiry time forward. Negative intervals and dates outside DateTimeOffset's range are rejected.</summary>
    public void Advance(TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
        while (true)
        {
            var old = Interlocked.Read(ref _ticks);
            var next = checked(old + elapsed.Ticks);
            if (next > DateTimeOffset.MaxValue.Ticks) throw new ArgumentOutOfRangeException(nameof(elapsed));
            if (Interlocked.CompareExchange(ref _ticks, next, old) == old) return;
        }
    }
}
