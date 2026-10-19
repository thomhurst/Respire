using System.Collections.Concurrent;

namespace Respire.Streaming;

/// <summary>A durable cursor and application state. State is an owned copy; percent is optional dashboard progress.</summary>
public sealed class RespireJobCheckpoint
{
    internal RespireJobCheckpoint(string cursor, byte[] state, int? percent)
        => (Cursor, State, Percent) = (cursor, state, percent);

    /// <summary>The application cursor saved by the handler.</summary>
    public string Cursor { get; }
    /// <summary>Owned application state bytes.</summary>
    public ReadOnlyMemory<byte> State { get; }
    /// <summary>Optional progress from zero through 100.</summary>
    public int? Percent { get; }
}

/// <summary>Reads checkpoint progress for retained job entries, without deserializing their payloads.</summary>
public static class RespireJobCheckpoints
{
    private static readonly RespireScript Locate = RespireScript.Create("""
        local rows = redis.call('XRANGE', KEYS[1], ARGV[1], ARGV[1], 'COUNT', 1)
        if #rows == 0 then return '' end
        for i = 1, #rows[1][2], 2 do
            if rows[1][2][i] == '_respire.job.checkpoint' then return rows[1][2][i+1] end
        end
        return ''
        """);

    /// <summary>Reads the last checkpoint for a ready-stream entry, or null if the entry or checkpoint is absent.</summary>
    public static async ValueTask<RespireJobCheckpoint?> ReadAsync(IRespireClient client, string stream,
        RespireStreamId entryId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(stream);
        // Return only the identity: dashboard inspection must not transfer job payloads.
        using var identity = await client.Scripts.ExecuteAsync(Locate, [stream], [entryId.Value], cancellationToken).ConfigureAwait(false);
        if (JobCheckpoints.Key(stream, identity.AsString()) is not { } key) return null;
        JobQueueScheduling.ValidateKeys(client, [stream, key]);
        return await JobCheckpoints.ReadAsync(client, key, cancellationToken).ConfigureAwait(false);
    }
}

internal static class JobCheckpoints
{
    internal static string? Key(string stream, RespireStreamEntry entry)
        => Key(stream, entry.GetString("_respire.job.checkpoint"));

    internal static string? Key(string stream, string? identity)
        => Guid.TryParseExact(identity, "N", out var id)
            ? stream + ":checkpoint:" + id.ToString("N") : null;

    internal static readonly RespireScript Save = RespireScript.Create("""
        local pending = redis.call('XPENDING', KEYS[1], ARGV[1], ARGV[3], ARGV[3], 1)
        if #pending ~= 1 or pending[1][2] ~= ARGV[2] or tostring(pending[1][4]) ~= ARGV[4] then return 0 end
        local rows = redis.call('XRANGE', KEYS[1], ARGV[3], ARGV[3])
        if #rows ~= 1 then return 0 end
        local expires = 0
        for i = 1, #rows[1][2], 2 do
            if rows[1][2][i] == '_respire.job.expires' then expires = tonumber(rows[1][2][i+1]) end
        end
        local time = redis.call('TIME')
        if expires > 0 and expires <= tonumber(time[1])*1000 + math.floor(tonumber(time[2])/1000) then return 0 end
        local value = cmsgpack.pack({ARGV[5], ARGV[6], tonumber(ARGV[7])})
        -- A single SET keeps data and expiry atomic even when permissions are denied.
        if expires > 0 then redis.call('SET', KEYS[2], value, 'PXAT', expires)
        else redis.call('SET', KEYS[2], value) end
        return 1
        """);
    internal static readonly RespireScript LeasedSave = JobGroupControls.Fence(Save);

    private static readonly RespireScript Read = RespireScript.Create("""
        local value = redis.call('GET', KEYS[1])
        if not value then return {} end
        return cmsgpack.unpack(value)
        """);

    internal static async ValueTask<RespireJobCheckpoint?> ReadAsync(IRespireClient client, string key, CancellationToken token)
    {
        using var result = await client.Scripts.ExecuteAsync(Read, [key], [], token).ConfigureAwait(false);
        if (result.Count == 0) return null;
        var percent = (int)result[2].AsInteger();
        return new(result[0].AsString(), result[1].AsBytes(), percent < 0 ? null : percent);
    }

    private static readonly ConcurrentDictionary<RespireScript, RespireScript> Completions = new();

    // Remove the extra key before existing completion/lease scripts inspect positional keys.
    internal static RespireScript Complete(RespireScript script) => Completions.GetOrAdd(script, static inner => RespireScript.Create("""
        local checkpoint = KEYS[#KEYS]
        KEYS[#KEYS] = nil
        if ARGV[5] ~= 'dead-letter' and not redis.acl_check_cmd('DEL', checkpoint) then
            return redis.error_reply('NOPERM checkpoint cleanup is not permitted')
        end
        local function complete()

        """ + inner.Source + """

        end
        local result = complete()
        if (result == 1 or result == 2) and ARGV[5] ~= 'dead-letter' then redis.call('DEL', checkpoint) end
        return result
        """));
}
