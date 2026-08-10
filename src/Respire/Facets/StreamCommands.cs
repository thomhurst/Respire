using System.Buffers.Text;
using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Protocol;

namespace Respire;

/// <summary>A stream entry id ("1526919030474-55"). Comparable as Redis compares them.</summary>
public readonly struct RespireStreamId : IEquatable<RespireStreamId>
{
    private readonly string? _value;

    /// <summary>Creates an id from Redis stream-id syntax.</summary>
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

    /// <summary>Converts Redis stream-id syntax to a typed id.</summary>
    public static implicit operator RespireStreamId(string value) => new(value);

    /// <inheritdoc/>
    public override string ToString() => Value;

    /// <inheritdoc/>
    public bool Equals(RespireStreamId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is RespireStreamId other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
}

/// <summary>
/// One stream entry. Entries read through a consumer group can acknowledge themselves via
/// <see cref="AckAsync"/>.
/// </summary>
public readonly record struct RespireStreamEntry
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

    /// <summary>The entry id.</summary>
    public RespireStreamId Id { get; }

    /// <summary>The entry's field/value pairs.</summary>
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
        => this[field] is { } bytes ? Internal.Utf8String.GetString(bytes) : null;

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

/// <summary>A consumer and its pending-entry count from Redis XPENDING.</summary>
public readonly record struct RespireStreamConsumerPendingCount(string Consumer, long Pending);

/// <summary>Summary information returned by Redis XPENDING.</summary>
public readonly record struct RespireStreamPendingSummary(
    long Count,
    RespireStreamId? SmallestId,
    RespireStreamId? GreatestId,
    RespireStreamConsumerPendingCount[] Consumers);

/// <summary>One detailed pending entry returned by Redis XPENDING.</summary>
public readonly record struct RespireStreamPendingEntry(
    RespireStreamId Id,
    string Consumer,
    TimeSpan IdleTime,
    long DeliveryCount);

/// <summary>Stream metadata returned by Redis XINFO STREAM.</summary>
public readonly record struct RespireStreamInfo(
    long Length,
    long RadixTreeKeys,
    long RadixTreeNodes,
    long Groups,
    RespireStreamId LastGeneratedId,
    RespireStreamId? MaxDeletedEntryId,
    long? EntriesAdded,
    RespireStreamId? RecordedFirstEntryId,
    RespireStreamEntry? FirstEntry,
    RespireStreamEntry? LastEntry);

/// <summary>Consumer-group metadata returned by Redis XINFO GROUPS.</summary>
public readonly record struct RespireStreamGroupInfo(
    string Name,
    long Consumers,
    long Pending,
    RespireStreamId LastDeliveredId,
    long? EntriesRead,
    long? Lag);

/// <summary>Consumer metadata returned by Redis XINFO CONSUMERS.</summary>
public readonly record struct RespireStreamConsumerInfo(
    string Name,
    long Pending,
    TimeSpan IdleTime,
    TimeSpan? InactiveTime);

/// <summary>
/// Stream commands. Collection cardinality uses <see cref="CountAsync"/>. Group reading is
/// exposed as an endless async stream of entries.
/// </summary>
public interface IStreamCommands
{
    /// <summary>Appends an entry (id auto-generated) and returns its id. Redis: XADD.</summary>
    ValueTask<RespireStreamId> AddAsync(RespireKey key, params ReadOnlySpan<(string Field, RespireValue Value)> fields);

    /// <summary>Appends an entry (id auto-generated) and returns its id. Redis: XADD.</summary>
    ValueTask<RespireStreamId> AddAsync(
        RespireKey key,
        ReadOnlySpan<(string Field, RespireValue Value)> fields,
        CancellationToken cancellationToken);

    /// <summary>Number of entries. Redis: XLEN.</summary>
    ValueTask<long> CountAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Entries in an inclusive id range. Redis: XRANGE.</summary>
    ValueTask<RespireStreamEntry[]> RangeAsync(
        RespireKey key,
        RespireStreamId? start = null,
        RespireStreamId? end = null,
        int? count = null,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes stream entries; returns the number removed. Redis: XDEL.</summary>
    ValueTask<long> DeleteAsync(RespireKey key, params ReadOnlySpan<RespireStreamId> ids);

    /// <summary>Deletes stream entries; returns the number removed. Redis: XDEL.</summary>
    ValueTask<long> DeleteAsync(
        RespireKey key, ReadOnlySpan<RespireStreamId> ids, CancellationToken cancellationToken);

    /// <summary>Trims a stream by maximum length; returns the number removed. Redis: XTRIM MAXLEN.</summary>
    ValueTask<long> TrimByMaxLengthAsync(
        RespireKey key,
        long maxLength,
        bool approximate = false,
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

    /// <summary>Deletes a consumer group; returns false when it did not exist. Redis: XGROUP DESTROY.</summary>
    ValueTask<bool> DeleteGroupAsync(RespireKey key, string group, CancellationToken cancellationToken = default);

    /// <summary>Deletes a consumer from a group; returns its pending entry count. Redis: XGROUP DELCONSUMER.</summary>
    ValueTask<long> DeleteConsumerAsync(
        RespireKey key,
        string group,
        string consumer,
        CancellationToken cancellationToken = default);

    /// <summary>Sets a consumer group's last delivered id. Redis: XGROUP SETID.</summary>
    ValueTask SetGroupPositionAsync(
        RespireKey key,
        string group,
        RespireStreamId id,
        long? entriesRead = null,
        CancellationToken cancellationToken = default);

    /// <summary>Acknowledges entries; returns how many were newly acknowledged. Redis: XACK.</summary>
    ValueTask<long> AcknowledgeAsync(RespireKey key, string group, params ReadOnlySpan<RespireStreamId> ids);

    /// <summary>Acknowledges entries; returns how many were newly acknowledged. Redis: XACK.</summary>
    ValueTask<long> AcknowledgeAsync(
        RespireKey key, string group, ReadOnlySpan<RespireStreamId> ids, CancellationToken cancellationToken);

    /// <summary>Summarizes pending entries for a group. Redis: XPENDING.</summary>
    ValueTask<RespireStreamPendingSummary> GetPendingSummaryAsync(
        RespireKey key,
        string group,
        CancellationToken cancellationToken = default);

    /// <summary>Returns pending entries in an inclusive id range. Redis: XPENDING range form.</summary>
    ValueTask<RespireStreamPendingEntry[]> GetPendingAsync(
        RespireKey key,
        string group,
        RespireStreamId? start = null,
        RespireStreamId? end = null,
        int count = 10,
        string? consumer = null,
        CancellationToken cancellationToken = default);

    /// <summary>Returns stream metadata. Redis: XINFO STREAM.</summary>
    ValueTask<RespireStreamInfo> GetInfoAsync(
        RespireKey key,
        CancellationToken cancellationToken = default);

    /// <summary>Returns consumer group metadata for a stream. Redis: XINFO GROUPS.</summary>
    ValueTask<RespireStreamGroupInfo[]> GetGroupInfoAsync(
        RespireKey key,
        CancellationToken cancellationToken = default);

    /// <summary>Returns consumer metadata for a stream group. Redis: XINFO CONSUMERS.</summary>
    ValueTask<RespireStreamConsumerInfo[]> GetConsumerInfoAsync(
        RespireKey key,
        string group,
        CancellationToken cancellationToken = default);

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
    private static readonly Verb XDel = new("XDEL");
    private static readonly Verb XTrim = new("XTRIM");
    private static readonly Verb XGroupDestroy = new("XGROUP DESTROY");
    private static readonly Verb XGroupDelConsumer = new("XGROUP DELCONSUMER");
    private static readonly Verb XGroupSetId = new("XGROUP SETID");
    private static readonly Verb XPending = new("XPENDING");
    private static readonly Verb XInfoStream = new("XINFO STREAM");
    private static readonly Verb XInfoGroups = new("XINFO GROUPS");
    private static readonly Verb XInfoConsumers = new("XINFO CONSUMERS");

    public ValueTask<RespireStreamId> AddAsync(RespireKey key, params ReadOnlySpan<(string Field, RespireValue Value)> fields)
        => AddAsync(key, fields, CancellationToken.None);

    public ValueTask<RespireStreamId> AddAsync(
        RespireKey key,
        ReadOnlySpan<(string Field, RespireValue Value)> fields,
        CancellationToken cancellationToken)
    {
        var args = new RespireValue[1 + fields.Length * 2];
        args[0] = "*";
        for (var i = 0; i < fields.Length; i++)
        {
            args[1 + i * 2] = fields[i].Field;
            args[2 + i * 2] = fields[i].Value;
        }

        return AddCoreAsync(new Cmd1N(Verbs.XAdd, client.Key(in key), args), cancellationToken);
    }

    private async ValueTask<RespireStreamId> AddCoreAsync(Cmd1N command, CancellationToken cancellationToken)
        => new(await client.StringAsync("XADD", command, cancellationToken).ConfigureAwait(false));

    public ValueTask<long> CountAsync(RespireKey key, CancellationToken cancellationToken = default)
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

    public ValueTask<long> DeleteAsync(RespireKey key, params ReadOnlySpan<RespireStreamId> ids)
        => DeleteAsync(key, ids, CancellationToken.None);

    public ValueTask<long> DeleteAsync(
        RespireKey key, ReadOnlySpan<RespireStreamId> ids, CancellationToken cancellationToken)
    {
        RequireIds(ids);
        var args = new RespireValue[ids.Length];
        for (var i = 0; i < ids.Length; i++)
        {
            args[i] = ids[i].Value;
        }

        return client.IntegerAsync("XDEL", new Cmd1N(XDel, client.Key(in key), args), cancellationToken);
    }

    public ValueTask<long> TrimByMaxLengthAsync(
        RespireKey key,
        long maxLength,
        bool approximate = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxLength);
        return approximate
            ? client.IntegerAsync(
                "XTRIM", new Cmd4(XTrim, client.Key(in key), "MAXLEN", "~", maxLength), cancellationToken)
            : client.IntegerAsync(
                "XTRIM", new Cmd3(XTrim, client.Key(in key), "MAXLEN", maxLength), cancellationToken);
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
        catch (RespireServerException ex) when (ex.Code == RespireErrorCodes.BusyGroup)
        {
            return false;
        }
    }

    public ValueTask<bool> DeleteGroupAsync(
        RespireKey key,
        string group,
        CancellationToken cancellationToken = default)
        => client.FlagAsync(
            "XGROUP DESTROY", new Cmd2(XGroupDestroy, client.Key(in key), group), cancellationToken);

    public ValueTask<long> DeleteConsumerAsync(
        RespireKey key,
        string group,
        string consumer,
        CancellationToken cancellationToken = default)
        => client.IntegerAsync(
            "XGROUP DELCONSUMER", new Cmd3(XGroupDelConsumer, client.Key(in key), group, consumer),
            cancellationToken);

    public async ValueTask SetGroupPositionAsync(
        RespireKey key,
        string group,
        RespireStreamId id,
        long? entriesRead = null,
        CancellationToken cancellationToken = default)
    {
        if (entriesRead is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entriesRead), "Entries read must be non-negative.");
        }

        if (entriesRead is { } read)
        {
            await client.OkAsync(
                "XGROUP SETID",
                new Cmd5(XGroupSetId, client.Key(in key), group, id.Value, "ENTRIESREAD", read),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await client.OkAsync(
            "XGROUP SETID",
            new Cmd3(XGroupSetId, client.Key(in key), group, id.Value),
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<long> AcknowledgeAsync(RespireKey key, string group, params ReadOnlySpan<RespireStreamId> ids)
        => AcknowledgeAsync(key, group, ids, CancellationToken.None);

    public ValueTask<long> AcknowledgeAsync(
        RespireKey key, string group, ReadOnlySpan<RespireStreamId> ids, CancellationToken cancellationToken)
    {
        var args = new RespireValue[ids.Length];
        for (var i = 0; i < ids.Length; i++)
        {
            args[i] = ids[i].Value;
        }

        return client.IntegerAsync(
            "XACK", new Cmd2N(Verbs.XAck, client.Key(in key), group, args), cancellationToken);
    }

    public async ValueTask<RespireStreamPendingSummary> GetPendingSummaryAsync(
        RespireKey key,
        string group,
        CancellationToken cancellationToken = default)
    {
        var reply = await client.SendAsync(
            "XPENDING", new Cmd2(XPending, client.Key(in key), group), cancellationToken).ConfigureAwait(false);
        try
        {
            return ParsePendingSummary(in reply);
        }
        finally
        {
            reply.Dispose();
        }
    }

    public async ValueTask<RespireStreamPendingEntry[]> GetPendingAsync(
        RespireKey key,
        string group,
        RespireStreamId? start = null,
        RespireStreamId? end = null,
        int count = 10,
        string? consumer = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        var from = (start ?? RespireStreamId.Min).Value;
        var to = (end ?? RespireStreamId.Max).Value;
        RespireValue[] args = consumer is null
            ? [client.Key(in key), group, from, to, count]
            : [client.Key(in key), group, from, to, count, consumer];
        var reply = await client.SendAsync("XPENDING", new CmdN(XPending, args), cancellationToken).ConfigureAwait(false);
        try
        {
            return ParsePendingEntries(in reply);
        }
        finally
        {
            reply.Dispose();
        }
    }

    public async ValueTask<RespireStreamInfo> GetInfoAsync(
        RespireKey key,
        CancellationToken cancellationToken = default)
    {
        var reply = await client.SendAsync(
            "XINFO STREAM", new Cmd1(XInfoStream, client.Key(in key)), cancellationToken).ConfigureAwait(false);
        try
        {
            return ParseStreamInfo(in reply);
        }
        finally
        {
            reply.Dispose();
        }
    }

    public async ValueTask<RespireStreamGroupInfo[]> GetGroupInfoAsync(
        RespireKey key,
        CancellationToken cancellationToken = default)
    {
        var reply = await client.SendAsync(
            "XINFO GROUPS", new Cmd1(XInfoGroups, client.Key(in key)), cancellationToken).ConfigureAwait(false);
        try
        {
            return ParseGroupInfo(in reply);
        }
        finally
        {
            reply.Dispose();
        }
    }

    public async ValueTask<RespireStreamConsumerInfo[]> GetConsumerInfoAsync(
        RespireKey key,
        string group,
        CancellationToken cancellationToken = default)
    {
        var reply = await client.SendAsync(
            "XINFO CONSUMERS", new Cmd2(XInfoConsumers, client.Key(in key), group), cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return ParseConsumerInfo(in reply);
        }
        finally
        {
            reply.Dispose();
        }
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
            entries[i] = ParseEntry(in elements[i], client, resolvedKey, group);
        }

        return entries;
    }

    private static RespireStreamEntry ParseEntry(
        in RespValue entryValue, RespireClient? client, RespireValue resolvedKey, string? group)
    {
        var entry = entryValue.AsArray();
        var id = new RespireStreamId(entry.Length > 0 ? entry[0].AsString() : string.Empty);
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

        return new RespireStreamEntry(id, fields, client, resolvedKey, group);
    }

    private static RespireStreamEntry? ParseNullableEntry(in RespValue entryValue)
    {
        if (entryValue.IsNull || entryValue.AsArray().Length == 0)
        {
            return null;
        }

        return ParseEntry(in entryValue, client: null, resolvedKey: default, group: null);
    }

    private static RespireStreamPendingSummary ParsePendingSummary(in RespValue reply)
    {
        var values = reply.AsArray();
        if (values.Length == 0)
        {
            return new RespireStreamPendingSummary(0, null, null, []);
        }

        var consumers = values.Length > 3 && !values[3].IsNull
            ? ParsePendingConsumers(in values[3])
            : [];
        return new RespireStreamPendingSummary(
            ReadInteger(in values[0]),
            values.Length > 1 ? ReadNullableStreamId(in values[1]) : null,
            values.Length > 2 ? ReadNullableStreamId(in values[2]) : null,
            consumers);
    }

    private static RespireStreamConsumerPendingCount[] ParsePendingConsumers(in RespValue value)
    {
        var values = value.AsArray();
        var consumers = new RespireStreamConsumerPendingCount[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var consumer = values[i].AsArray();
            consumers[i] = new RespireStreamConsumerPendingCount(
                consumer.Length > 0 ? consumer[0].AsString() : string.Empty,
                consumer.Length > 1 ? ReadInteger(in consumer[1]) : 0);
        }

        return consumers;
    }

    private static RespireStreamPendingEntry[] ParsePendingEntries(in RespValue reply)
    {
        var values = reply.AsArray();
        var pending = new RespireStreamPendingEntry[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var item = values[i].AsArray();
            pending[i] = new RespireStreamPendingEntry(
                item.Length > 0 ? new RespireStreamId(item[0].AsString()) : default,
                item.Length > 1 ? item[1].AsString() : string.Empty,
                item.Length > 2 ? TimeSpan.FromMilliseconds(ReadInteger(in item[2])) : TimeSpan.Zero,
                item.Length > 3 ? ReadInteger(in item[3]) : 0);
        }

        return pending;
    }

    private static RespireStreamInfo ParseStreamInfo(in RespValue reply)
    {
        var values = reply.AsArray();
        var length = 0L;
        var radixTreeKeys = 0L;
        var radixTreeNodes = 0L;
        var groups = 0L;
        var lastGeneratedId = default(RespireStreamId);
        RespireStreamId? maxDeletedEntryId = null;
        long? entriesAdded = null;
        RespireStreamId? recordedFirstEntryId = null;
        RespireStreamEntry? firstEntry = null;
        RespireStreamEntry? lastEntry = null;

        for (var i = 0; i + 1 < values.Length; i += 2)
        {
            switch (values[i].AsString())
            {
                case "length":
                    length = ReadInteger(in values[i + 1]);
                    break;
                case "radix-tree-keys":
                    radixTreeKeys = ReadInteger(in values[i + 1]);
                    break;
                case "radix-tree-nodes":
                    radixTreeNodes = ReadInteger(in values[i + 1]);
                    break;
                case "groups":
                    groups = ReadInteger(in values[i + 1]);
                    break;
                case "last-generated-id":
                    lastGeneratedId = ReadStreamId(in values[i + 1]);
                    break;
                case "max-deleted-entry-id":
                    maxDeletedEntryId = ReadNullableStreamId(in values[i + 1]);
                    break;
                case "entries-added":
                    entriesAdded = ReadNullableInteger(in values[i + 1]);
                    break;
                case "recorded-first-entry-id":
                    recordedFirstEntryId = ReadNullableStreamId(in values[i + 1]);
                    break;
                case "first-entry":
                    firstEntry = ParseNullableEntry(in values[i + 1]);
                    break;
                case "last-entry":
                    lastEntry = ParseNullableEntry(in values[i + 1]);
                    break;
            }
        }

        return new RespireStreamInfo(
            length,
            radixTreeKeys,
            radixTreeNodes,
            groups,
            lastGeneratedId,
            maxDeletedEntryId,
            entriesAdded,
            recordedFirstEntryId,
            firstEntry,
            lastEntry);
    }

    private static RespireStreamGroupInfo[] ParseGroupInfo(in RespValue reply)
    {
        var values = reply.AsArray();
        var groups = new RespireStreamGroupInfo[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var fields = values[i].AsArray();
            var name = string.Empty;
            var consumers = 0L;
            var pending = 0L;
            var lastDeliveredId = default(RespireStreamId);
            long? entriesRead = null;
            long? lag = null;

            for (var field = 0; field + 1 < fields.Length; field += 2)
            {
                switch (fields[field].AsString())
                {
                    case "name":
                        name = fields[field + 1].AsString();
                        break;
                    case "consumers":
                        consumers = ReadInteger(in fields[field + 1]);
                        break;
                    case "pending":
                        pending = ReadInteger(in fields[field + 1]);
                        break;
                    case "last-delivered-id":
                        lastDeliveredId = ReadStreamId(in fields[field + 1]);
                        break;
                    case "entries-read":
                        entriesRead = ReadNullableInteger(in fields[field + 1]);
                        break;
                    case "lag":
                        lag = ReadNullableInteger(in fields[field + 1]);
                        break;
                }
            }

            groups[i] = new RespireStreamGroupInfo(name, consumers, pending, lastDeliveredId, entriesRead, lag);
        }

        return groups;
    }

    private static RespireStreamConsumerInfo[] ParseConsumerInfo(in RespValue reply)
    {
        var values = reply.AsArray();
        var consumers = new RespireStreamConsumerInfo[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var fields = values[i].AsArray();
            var name = string.Empty;
            var pending = 0L;
            var idle = TimeSpan.Zero;
            TimeSpan? inactive = null;

            for (var field = 0; field + 1 < fields.Length; field += 2)
            {
                switch (fields[field].AsString())
                {
                    case "name":
                        name = fields[field + 1].AsString();
                        break;
                    case "pending":
                        pending = ReadInteger(in fields[field + 1]);
                        break;
                    case "idle":
                        idle = TimeSpan.FromMilliseconds(ReadInteger(in fields[field + 1]));
                        break;
                    case "inactive":
                        inactive = fields[field + 1].IsNull
                            ? null
                            : TimeSpan.FromMilliseconds(ReadInteger(in fields[field + 1]));
                        break;
                }
            }

            consumers[i] = new RespireStreamConsumerInfo(name, pending, idle, inactive);
        }

        return consumers;
    }

    private static long ReadInteger(in RespValue value)
    {
        if (value.Type == RespDataType.Integer)
        {
            return value.AsInteger();
        }

        var span = value.AsSpan();
        return Utf8Parser.TryParse(span, out long parsed, out var consumed) && consumed == span.Length
            ? parsed
            : 0;
    }

    private static long? ReadNullableInteger(in RespValue value) => value.IsNull ? null : ReadInteger(in value);

    private static RespireStreamId ReadStreamId(in RespValue value)
        => ReadNullableStreamId(in value) ?? default;

    private static RespireStreamId? ReadNullableStreamId(in RespValue value)
    {
        if (value.IsNull)
        {
            return null;
        }

        var id = value.AsString();
        return id.Length == 0 ? (RespireStreamId?)null : new RespireStreamId(id);
    }

    private static void RequireIds(ReadOnlySpan<RespireStreamId> ids)
    {
        if (ids.IsEmpty)
        {
            throw new ArgumentException("At least one stream id is required.", nameof(ids));
        }
    }
}
