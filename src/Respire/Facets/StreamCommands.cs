using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Protocol;

namespace Respire;

/// <summary>A stream entry id ("1526919030474-55"). Comparable as Redis compares them.</summary>
public readonly struct RespireStreamId : IEquatable<RespireStreamId>
{
    private readonly string? _value;

    public RespireStreamId(string value) => _value = value ?? throw new ArgumentNullException(nameof(value));

    /// <summary>The smallest id ("-"), for range starts.</summary>
    public static readonly RespireStreamId Min = new("-");

    /// <summary>The largest id ("+"), for range ends.</summary>
    public static readonly RespireStreamId Max = new("+");

    /// <summary>"$" — only entries added after now (XGROUP CREATE default).</summary>
    public static readonly RespireStreamId New = new("$");

    /// <summary>"0" — the beginning of the stream.</summary>
    public static readonly RespireStreamId Beginning = new("0");

    internal string Value => _value ?? "0";

    public static implicit operator RespireStreamId(string value) => new(value);

    public override string ToString() => Value;

    public bool Equals(RespireStreamId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is RespireStreamId other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
}

/// <summary>
/// One stream entry. Entries read through a consumer group can acknowledge themselves via
/// <see cref="AckAsync"/>.
/// </summary>
public sealed class RespireStreamEntry
{
    private readonly RespireClient? _client;
    private readonly RespireValue _resolvedKey;
    private readonly string? _group;

    internal RespireStreamEntry(
        RespireStreamId id,
        KeyValuePair<string, byte[]>[] fields,
        RespireClient? client = null,
        RespireValue resolvedKey = default,
        string? group = null)
    {
        Id = id;
        Fields = fields;
        _client = client;
        _resolvedKey = resolvedKey;
        _group = group;
    }

    public RespireStreamId Id { get; }

    public IReadOnlyList<KeyValuePair<string, byte[]>> Fields { get; }

    /// <summary>The field's raw bytes, or null when absent.</summary>
    public byte[]? this[string field]
    {
        get
        {
            foreach (var pair in Fields)
            {
                if (string.Equals(pair.Key, field, StringComparison.Ordinal))
                {
                    return pair.Value;
                }
            }

            return null;
        }
    }

    /// <summary>The field decoded as a UTF-8 string, or null when absent.</summary>
    public string? GetString(string field)
        => this[field] is { } bytes ? System.Text.Encoding.UTF8.GetString(bytes) : null;

    /// <summary>
    /// Acknowledges this entry to its consumer group; returns false when it was already
    /// acknowledged. Only available on entries from a group read. Redis: XACK.
    /// </summary>
    public ValueTask<bool> AckAsync(CancellationToken cancellationToken = default)
    {
        if (_client is null || _group is null)
        {
            throw new InvalidOperationException("Only entries read through a consumer group can be acknowledged.");
        }

        return _client.FlagAsync(
            "XACK", new Cmd3(Verbs.XAck, _resolvedKey, _group, Id.Value), cancellationToken);
    }
}

/// <summary>Stream commands. Group reading is exposed as an endless async stream of entries.</summary>
public interface IStreamCommands
{
    /// <summary>Appends an entry (id auto-generated) and returns its id. Redis: XADD.</summary>
    ValueTask<RespireStreamId> AddAsync(RespireKey key, params ReadOnlySpan<(string Field, RespireValue Value)> fields);

    /// <summary>Number of entries. Redis: XLEN.</summary>
    ValueTask<long> LengthAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Entries in an inclusive id range. Redis: XRANGE.</summary>
    ValueTask<RespireStreamEntry[]> RangeAsync(
        RespireKey key,
        RespireStreamId? start = null,
        RespireStreamId? end = null,
        int? count = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a consumer group (and, by default, the stream itself if missing). Returns false
    /// when the group already exists. Redis: XGROUP CREATE.
    /// </summary>
    ValueTask<bool> CreateGroupAsync(
        RespireKey key,
        string group,
        RespireStreamId? startAt = null,
        bool createStream = true,
        CancellationToken cancellationToken = default);

    /// <summary>Acknowledges entries; returns how many were newly acknowledged. Redis: XACK.</summary>
    ValueTask<long> AcknowledgeAsync(RespireKey key, string group, params ReadOnlySpan<RespireStreamId> ids);

    /// <summary>
    /// Continuously reads new entries for a consumer group:
    /// <c>await foreach (var entry in client.Streams.ReadGroupAsync(...))</c>. Blocks server-side
    /// between deliveries on a dedicated pooled connection (never stalling other traffic) and
    /// ends only via the cancellation token. Call <see cref="RespireStreamEntry.AckAsync"/> after
    /// processing each entry. Redis: XREADGROUP.
    /// </summary>
    IAsyncEnumerable<RespireStreamEntry> ReadGroupAsync(
        RespireKey key, string group, string consumer, int batchSize = 64, CancellationToken cancellationToken = default);
}

internal sealed class StreamCommands(RespireClient client) : IStreamCommands
{
    private static readonly TimeSpan BlockInterval = TimeSpan.FromSeconds(5);

    public ValueTask<RespireStreamId> AddAsync(RespireKey key, params ReadOnlySpan<(string Field, RespireValue Value)> fields)
    {
        var args = new RespireValue[1 + fields.Length * 2];
        args[0] = "*";
        for (var i = 0; i < fields.Length; i++)
        {
            args[1 + i * 2] = fields[i].Field;
            args[2 + i * 2] = fields[i].Value;
        }

        return AddCoreAsync(new Cmd1N(Verbs.XAdd, client.Key(in key), args));
    }

    private async ValueTask<RespireStreamId> AddCoreAsync(Cmd1N command)
        => new(await client.StringAsync("XADD", command, CancellationToken.None).ConfigureAwait(false));

    public ValueTask<long> LengthAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.IntegerAsync("XLEN", new Cmd1(Verbs.XLen, client.Key(in key)), cancellationToken);

    public async ValueTask<RespireStreamEntry[]> RangeAsync(
        RespireKey key, RespireStreamId? start = null, RespireStreamId? end = null, int? count = null,
        CancellationToken cancellationToken = default)
    {
        var from = (start ?? RespireStreamId.Min).Value;
        var to = (end ?? RespireStreamId.Max).Value;
        var reply = count is { } take
            ? await client.SendAsync(
                "XRANGE", new Cmd5(Verbs.XRange, client.Key(in key), from, to, "COUNT", take), cancellationToken)
                .ConfigureAwait(false)
            : await client.SendAsync(
                "XRANGE", new Cmd3(Verbs.XRange, client.Key(in key), from, to), cancellationToken).ConfigureAwait(false);

        var entries = ParseEntries(in reply, client: null, resolvedKey: default, group: null);
        reply.Dispose();
        return entries;
    }

    public async ValueTask<bool> CreateGroupAsync(
        RespireKey key, string group, RespireStreamId? startAt = null, bool createStream = true,
        CancellationToken cancellationToken = default)
    {
        var from = (startAt ?? RespireStreamId.New).Value;
        try
        {
            if (createStream)
            {
                await client.OkAsync(
                    "XGROUP CREATE", new Cmd4(Verbs.XGroupCreate, client.Key(in key), group, from, "MKSTREAM"),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await client.OkAsync(
                    "XGROUP CREATE", new Cmd3(Verbs.XGroupCreate, client.Key(in key), group, from), cancellationToken)
                    .ConfigureAwait(false);
            }

            return true;
        }
        catch (RespireServerException ex) when (ex.Code == "BUSYGROUP")
        {
            return false;
        }
    }

    public ValueTask<long> AcknowledgeAsync(RespireKey key, string group, params ReadOnlySpan<RespireStreamId> ids)
    {
        var args = new RespireValue[ids.Length];
        for (var i = 0; i < ids.Length; i++)
        {
            args[i] = ids[i].Value;
        }

        return client.IntegerAsync(
            "XACK", new Cmd2N(Verbs.XAck, client.Key(in key), group, args), CancellationToken.None);
    }

    public async IAsyncEnumerable<RespireStreamEntry> ReadGroupAsync(
        RespireKey key, string group, string consumer, int batchSize = 64,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var resolvedKey = client.Key(in key);
        while (!cancellationToken.IsCancellationRequested)
        {
            var reply = await client.SendBlockingAsync(
                "XREADGROUP",
                new CmdN(Verbs.XReadGroup,
                [
                    "GROUP", group, consumer,
                    "COUNT", batchSize,
                    "BLOCK", (long)BlockInterval.TotalMilliseconds,
                    "STREAMS", resolvedKey, ">",
                ]),
                cancellationToken).ConfigureAwait(false);

            if (reply.IsNull)
            {
                reply.Dispose();
                continue;
            }

            var entries = ParseReadReply(in reply, resolvedKey, group);
            reply.Dispose();
            foreach (var entry in entries)
            {
                yield return entry;
            }
        }
    }

    /// <summary>XREADGROUP replies [[key, entries]] (RESP2 array) or {key: entries} (RESP3 map, pairs flattened).</summary>
    private RespireStreamEntry[] ParseReadReply(in RespValue reply, RespireValue resolvedKey, string group)
    {
        var streams = reply.AsArray();
        if (streams.Length == 0)
        {
            return [];
        }

        if (reply.Type == RespDataType.Map)
        {
            // Flattened pairs: [key, entries, key, entries, …]; single stream requested.
            return ParseEntries(in streams[1], client, resolvedKey, group);
        }

        var streamPair = streams[0].AsArray();
        return ParseEntries(in streamPair[1], client, resolvedKey, group);
    }

    /// <summary>Entries are [id, [field, value, …]]; trimmed entries can carry a null field list.</summary>
    private static RespireStreamEntry[] ParseEntries(
        in RespValue entriesValue, RespireClient? client, RespireValue resolvedKey, string? group)
    {
        var elements = entriesValue.AsArray();
        var entries = new RespireStreamEntry[elements.Length];
        for (var i = 0; i < elements.Length; i++)
        {
            var entry = elements[i].AsArray();
            var id = new RespireStreamId(entry[0].AsString());
            KeyValuePair<string, byte[]>[] fields = [];
            if (entry.Length > 1 && !entry[1].IsNull)
            {
                var flat = entry[1].AsArray();
                fields = new KeyValuePair<string, byte[]>[flat.Length / 2];
                for (var f = 0; f < fields.Length; f++)
                {
                    fields[f] = new KeyValuePair<string, byte[]>(
                        flat[f * 2].AsString(), flat[f * 2 + 1].AsSpan().ToArray());
                }
            }

            entries[i] = new RespireStreamEntry(id, fields, client, resolvedKey, group);
        }

        return entries;
    }
}
