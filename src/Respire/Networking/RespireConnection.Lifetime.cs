using Respire.Internal;

namespace Respire.Networking;

internal sealed partial class RespireConnection
{
    private TimeProvider? _connectionLifetimeClock;
    internal long? LifetimeExpiresAt { get; private set; }

    private void InitializeConnectionLifetime(RespireConnectionOptions options)
    {
        if (options.ConnectionMaxLifetime is not { } lifetime) return;
        _connectionLifetimeClock = options.ConnectionLifetimeTimeProvider;
        var duration = lifetime.TotalSeconds + Random.Shared.NextDouble() * options.ConnectionMaxLifetimeJitter.TotalSeconds;
        var now = _connectionLifetimeClock.GetTimestamp();
        // Saturate extreme configurations rather than overflowing a monotonic deadline.
        var deadline = now + Math.Ceiling(duration * _connectionLifetimeClock.TimestampFrequency);
        LifetimeExpiresAt = deadline >= long.MaxValue ? long.MaxValue : (long)deadline;
    }

    internal TimeSpan? RemainingConnectionLifetime => LifetimeExpiresAt is { } expires
        ? _connectionLifetimeClock!.GetElapsedTime(_connectionLifetimeClock.GetTimestamp(), expires) : null;

    internal bool IsConnectionLifetimeExpired => LifetimeExpiresAt is { } expires
        && _connectionLifetimeClock!.GetTimestamp() >= expires;

    internal void RecordConnectionRecycle()
    {
        RequestMetricCloseReason(ConnectionTelemetry.CloseReason.MaxLifetime);
        ConnectionTelemetry.State.Recycled(_connectionMetrics, _logger, Host, Port);
    }
}
