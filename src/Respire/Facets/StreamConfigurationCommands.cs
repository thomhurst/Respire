using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

/// <summary>Per-stream idempotent producer settings for Redis 8.6+ XCFGSET.</summary>
public readonly record struct StreamConfigurationOptions
{
    internal const int MaximumIdempotencyDurationSeconds = 86400;
    internal const int MaximumIdempotencyMaxSize = 10000;

    /// <summary>Retention in whole seconds, from 1 through 86400. Null preserves the current value.</summary>
    public int? IdempotencyDurationSeconds { get; init; }

    /// <summary>Maximum tracked identities per producer, from 1 through 10000. Null preserves the current value.</summary>
    public int? IdempotencyMaxSize { get; init; }
}

public partial interface IStreamCommands
{
    /// <summary>Changes idempotency settings on an existing stream. Returns true on OK. Redis 8.6+: XCFGSET.</summary>
    /// <remarks>At least one setting is required. Changing a value clears the stream's producer deduplication records.
    /// Cancellation cannot undo a dispatched configuration change.</remarks>
    ValueTask<bool> ConfigureAsync(RespireKey key, StreamConfigurationOptions options, CancellationToken cancellationToken = default);
}

internal sealed partial class StreamCommands
{
    private static readonly Verb XCfgSet = new("XCFGSET");

    public ValueTask<bool> ConfigureAsync(RespireKey key, StreamConfigurationOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return client.ConvertResponseAsync("XCFGSET", BuildConfigurationCommand(client, key, options),
            cancellationToken, this, static (StreamCommands _, in RespValue value) => ResponseReader.Ok(in value));
    }

    internal static Cmd1N BuildConfigurationCommand(RespireClient client, RespireKey key, StreamConfigurationOptions options)
    {
        if (options.IdempotencyDurationSeconds is null && options.IdempotencyMaxSize is null)
            throw new ArgumentException("At least one stream configuration setting is required.", nameof(options));
        if (options.IdempotencyDurationSeconds is < 1 or > StreamConfigurationOptions.MaximumIdempotencyDurationSeconds)
            throw new ArgumentOutOfRangeException(nameof(options), $"IdempotencyDurationSeconds must be between 1 and {StreamConfigurationOptions.MaximumIdempotencyDurationSeconds}.");
        if (options.IdempotencyMaxSize is < 1 or > StreamConfigurationOptions.MaximumIdempotencyMaxSize)
            throw new ArgumentOutOfRangeException(nameof(options), $"IdempotencyMaxSize must be between 1 and {StreamConfigurationOptions.MaximumIdempotencyMaxSize}.");
        var arguments = new RespireValue[(options.IdempotencyDurationSeconds.HasValue ? 2 : 0) + (options.IdempotencyMaxSize.HasValue ? 2 : 0)];
        var index = 0;
        if (options.IdempotencyDurationSeconds is { } duration)
        {
            arguments[index++] = "IDMP-DURATION";
            arguments[index++] = duration;
        }
        if (options.IdempotencyMaxSize is { } size)
        {
            arguments[index++] = "IDMP-MAXSIZE";
            arguments[index] = size;
        }
        return new(XCfgSet, client.Key(key), arguments);
    }
}
