using System.Text;

namespace Respire.Streaming;

public sealed partial class RespireJobQueue<T>
{
    /// <summary>Replaces a group's shared admission policy. Null restores unlimited admission; pause state is preserved.</summary>
    public async ValueTask SetGroupOptionsAsync(string group, RespireJobGroupOptions? options, CancellationToken cancellationToken = default)
    {
        var prefix = GroupPrefix(group);
        options?.Validate();
        var keys = JobGroupScheduling.Keys(StreamKey);
        JobQueueScheduling.ValidateKeys(_client, keys);
        await _client.Scripts.ExecuteIntegerAsync(JobGroupControls.Configure, [.. keys],
            [prefix, options?.MaxConcurrency ?? 0, options?.RateLimit?.PermitLimit ?? 0,
                (long)Math.Ceiling(options?.RateLimit?.Window.TotalMilliseconds ?? 0),
                (long)Math.Ceiling((options?.LeaseDuration ?? TimeSpan.FromMinutes(1)).TotalMilliseconds)],
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops new delivery attempts for one application job group. In-flight leases continue.</summary>
    public ValueTask<bool> PauseGroupAsync(string group, CancellationToken cancellationToken = default)
        => SetGroupPausedAsync(group, true, cancellationToken);

    /// <summary>Makes one application job group eligible again, subject to its admission policy.</summary>
    public ValueTask<bool> ResumeGroupAsync(string group, CancellationToken cancellationToken = default)
        => SetGroupPausedAsync(group, false, cancellationToken);

    private async ValueTask<bool> SetGroupPausedAsync(string group, bool paused, CancellationToken cancellationToken)
    {
        var prefix = GroupPrefix(group);
        var keys = JobGroupScheduling.Keys(StreamKey);
        JobQueueScheduling.ValidateKeys(_client, keys);
        return await _client.Scripts.ExecuteIntegerAsync(JobGroupControls.Pause, [.. keys],
            [prefix, paused ? 1 : 0], cancellationToken).ConfigureAwait(false) == 1;
    }

    private static string GroupPrefix(string group)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        return Convert.ToHexString(new UTF8Encoding(false, true).GetBytes(group)).ToLowerInvariant() + "|";
    }
}

/// <summary>Distributed admission policy for one application job group, using the Redis server clock.</summary>
public sealed class RespireJobGroupOptions
{
    /// <summary>Maximum live delivery leases across workers. Null means unlimited.</summary>
    public int? MaxConcurrency { get; init; }
    /// <summary>GCRA rate policy: PermitLimit is the burst capacity, replenished over Window. Null means unlimited.</summary>
    public RespireJobRateLimit? RateLimit { get; init; }
    /// <summary>Renewable delivery lease lifetime. Defaults to one minute; must be at least 100 milliseconds.</summary>
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(1);

    internal void Validate()
    {
        if (MaxConcurrency is { } maximum) ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
        RateLimit?.Validate();
        JobQueueScheduling.ValidateDuration(LeaseDuration, nameof(LeaseDuration));
        if (LeaseDuration < TimeSpan.FromMilliseconds(100)) throw new ArgumentOutOfRangeException(nameof(LeaseDuration));
    }
}
