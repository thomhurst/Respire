using Respire.Commands;

namespace Respire;

/// <summary>Optional replacement and eviction metadata for RESTORE.</summary>
public readonly record struct RespireRestoreOptions
{
    /// <summary>Overwrite an existing key. Redis: REPLACE (3.0+).</summary>
    public bool Replace { get; init; }
    /// <summary>Nonnegative idle seconds for LRU eviction. Mutually exclusive with Frequency. Redis: IDLETIME (5.0+).</summary>
    public long? IdleTimeSeconds { get; init; }
    /// <summary>LFU frequency, 0 through 255. Mutually exclusive with IdleTimeSeconds. Redis: FREQ (5.0+).</summary>
    public byte? Frequency { get; init; }
}

public partial interface IKeyCommands
{
    /// <summary>Returns an owned Redis-serialized payload, or null for a missing key. Does not include expiry. Redis: DUMP.</summary>
    ValueTask<byte[]?> DumpAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Restores a Redis-serialized payload; true on OK. Redis: RESTORE.</summary>
    /// <remarks>None/Persist creates a persistent key. Relative expiry must be positive after millisecond truncation;
    /// absolute expiry must be after the Unix epoch and uses ABSTTL (Redis 5.0+). Keep is unsupported.
    /// Do not modify the payload until completion. Redis validates payload format, version, and checksum.</remarks>
    ValueTask<bool> RestoreAsync(RespireKey key, ReadOnlyMemory<byte> payload,
        RespireExpiry expiry = default, RespireRestoreOptions options = default,
        CancellationToken cancellationToken = default);
}

internal sealed partial class KeyCommands
{
    public ValueTask<byte[]?> DumpAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.BytesOrNullAsync("DUMP", new Cmd1(RespireCommands.Key.DUMP.Verb, client.Key(in key)), cancellationToken);

    public ValueTask<bool> RestoreAsync(RespireKey key, ReadOnlyMemory<byte> payload,
        RespireExpiry expiry = default, RespireRestoreOptions options = default,
        CancellationToken cancellationToken = default)
        => client.OkResultAsync("RESTORE", RestoreCommand(client, key, payload, expiry, options), cancellationToken);

    internal static CmdN RestoreCommand(RespireClient client, RespireKey key, ReadOnlyMemory<byte> payload,
        RespireExpiry expiry, RespireRestoreOptions options)
    {
        if (options.IdleTimeSeconds is < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "IdleTimeSeconds must be nonnegative.");
        if (options.IdleTimeSeconds is not null && options.Frequency is not null)
            throw new ArgumentException("IDLETIME and FREQ are mutually exclusive.", nameof(options));
        if (expiry.IsKeep)
            throw new ArgumentException("RESTORE does not support keeping the existing expiry.", nameof(expiry));

        long ttl = 0;
        var absolute = expiry.TryGetAbsoluteUnixMilliseconds(out var instant);
        if (absolute || expiry.TryGetRelativeMilliseconds(out ttl))
        {
            if (absolute) ttl = instant;
            // Redis interprets zero as persistence even with ABSTTL; do not silently turn
            // a zero/sub-millisecond duration or the Unix epoch into a persistent key.
            if (ttl <= 0)
                throw new ArgumentOutOfRangeException(nameof(expiry), "RESTORE expiry must be positive in milliseconds; use None or Persist for no expiry.");
        }

        var arguments = new RespireValue[3 + (options.Replace ? 1 : 0) + (absolute ? 1 : 0)
            + (options.IdleTimeSeconds is not null || options.Frequency is not null ? 2 : 0)];
        arguments[0] = client.Key(in key);
        arguments[1] = ttl;
        arguments[2] = payload;
        var index = 3;
        if (options.Replace) arguments[index++] = "REPLACE";
        if (absolute) arguments[index++] = "ABSTTL";
        if (options.IdleTimeSeconds is { } idle)
        {
            arguments[index++] = "IDLETIME";
            arguments[index] = idle;
        }
        else if (options.Frequency is { } frequency)
        {
            arguments[index++] = "FREQ";
            arguments[index] = frequency;
        }
        return new CmdN(RespireCommands.Key.RESTORE.Verb, arguments);
    }
}
