using System.Globalization;

namespace Respire.Streaming;

public sealed partial class RespireJobQueue<T>
{
    /// <summary>Persistent queue-wide pause, rate policy and limiter-hit metadata.</summary>
    public string ControlKey => JobQueueControls.Key(StreamKey);

    /// <summary>Stops new claims across all workers. Already admitted deliveries finish. Returns true on a state change.</summary>
    public ValueTask<bool> PauseAsync(CancellationToken cancellationToken = default)
        => SetPausedAsync(true, cancellationToken);

    /// <summary>Resumes claims across all workers. Returns true on a state change.</summary>
    public ValueTask<bool> ResumeAsync(CancellationToken cancellationToken = default)
        => SetPausedAsync(false, cancellationToken);

    private async ValueTask<bool> SetPausedAsync(bool paused, CancellationToken cancellationToken)
    {
        JobQueueScheduling.ValidateKeys(_client, [StreamKey, ControlKey, EventsStreamKey]);
        return await _client.Scripts.ExecuteIntegerAsync(JobQueueControls.Pause, [ControlKey, EventsStreamKey],
            [paused ? 1 : 0], cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <summary>Changes the shared rate policy, or disables it with null. Preserves usage in the active window.</summary>
    /// <remarks>Window duration changes apply at the next window. All workers read the shared policy on each claim.</remarks>
    public async ValueTask SetRateLimitAsync(RespireJobRateLimit? rateLimit, CancellationToken cancellationToken = default)
    {
        rateLimit?.Validate();
        await _client.Scripts.ExecuteIntegerAsync(JobQueueControls.Configure, [ControlKey],
            [rateLimit?.PermitLimit ?? 0, (long)Math.Ceiling(rateLimit?.Window.TotalMilliseconds ?? 0), "update"],
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the shared paused state and cumulative denied admission count.</summary>
    public async ValueTask<RespireJobQueueState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        using var result = await _client.Scripts.ExecuteAsync(JobQueueControls.State, [ControlKey], [],
            cancellationToken).ConfigureAwait(false);
        return new(result[0].AsString() == "1", long.Parse(result[1].AsString(), CultureInfo.InvariantCulture));
    }
}

/// <summary>A queue-wide fixed-window admission limit, measured by the Redis server clock.</summary>
public sealed record RespireJobRateLimit
{
    /// <summary>Maximum admitted delivery attempts in one window. Must be positive.</summary>
    public int PermitLimit { get; init; } = 100;
    /// <summary>Window duration. Positive and at most Int32.MaxValue milliseconds; fractions round up.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(1);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(PermitLimit);
        JobQueueScheduling.ValidateDuration(Window, nameof(Window));
    }
}

/// <summary>A shared queue control snapshot. Limiter hits count denied reader admissions, not unique jobs.</summary>
public sealed record RespireJobQueueState(bool IsPaused, long RateLimitHits);
