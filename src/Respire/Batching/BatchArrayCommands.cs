using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;
using Respire.Serialization;

namespace Respire;

/// <summary>Array commands queued on batches and transactions. Argument spans are copied; referenced byte buffers must remain unchanged until execution.</summary>
public interface IBatchArrayCommands
{
    /// <summary>Matching indexes with default OR options. Redis: ARGREP.</summary>
    RespirePending<ulong[]> Grep(RespireKey key, RespireArrayBound start, RespireArrayBound end, params ReadOnlySpan<RespireArrayPredicate> predicates);

    /// <summary>Matching indexes and values with default OR options. Redis: ARGREP.</summary>
    RespirePending<RespireArrayEntry<string>[]> GrepEntries(RespireKey key, RespireArrayBound start, RespireArrayBound end, params ReadOnlySpan<RespireArrayPredicate> predicates);

    /// <summary>Matching indexes and values with default OR options. Redis: ARGREP.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    RespirePending<RespireArrayEntry<T>[]> GrepEntries<T>(RespireKey key, RespireArrayBound start, RespireArrayBound end, params ReadOnlySpan<RespireArrayPredicate> predicates);

    /// <summary>Serializes a value into one slot; returns 0 or 1 newly populated slots. Redis: ARSET.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    RespirePending<ulong> Set<T>(RespireKey key, ulong index, T value);

    /// <summary>Number of populated slots, zero when missing. Redis: ARCOUNT.</summary>
    RespirePending<ulong> Count(RespireKey key);

    /// <summary>Highest populated index plus one, zero when missing. Redis: ARLEN.</summary>
    RespirePending<ulong> Length(RespireKey key);

    /// <summary>Value at an index, or null for a missing key or slot. Redis: ARGET.</summary>
    RespirePending<string?> GetString(RespireKey key, ulong index);

    /// <summary>Deserialized value, or default when missing. Use nullable value types or TryGet to preserve presence. Redis: ARGET.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    RespirePending<T?> Get<T>(RespireKey key, ulong index);

    /// <summary>Deserialized value with explicit slot presence, including stored defaults. Redis: ARGET.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    RespirePending<RespireGet<T>> TryGet<T>(RespireKey key, ulong index);

    /// <summary>Values in requested index order, retaining null holes. Redis: ARMGET.</summary>
    RespirePending<string?[]> GetMany(RespireKey key, params ReadOnlySpan<ulong> indexes);

    /// <summary>Values in requested index order. Use nullable value types to retain holes. Redis: ARMGET.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    RespirePending<T?[]> GetMany<T>(RespireKey key, params ReadOnlySpan<ulong> indexes);

    /// <summary>Inclusive range retaining holes; descending endpoints return reverse order. Redis: ARGETRANGE.</summary>
    RespirePending<string?[]> Range(RespireKey key, ulong start, ulong end);

    /// <summary>Deserialized inclusive range. Use nullable value types to retain holes. Redis: ARGETRANGE.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    RespirePending<T?[]> Range<T>(RespireKey key, ulong start, ulong end);

    /// <summary>Writes consecutive slots; returns newly populated slot count. Does not move the insertion cursor. Redis: ARSET.</summary>
    RespirePending<ulong> Set(RespireKey key, ulong index, params ReadOnlySpan<RespireValue> values);

    /// <summary>Writes an array of values to consecutive slots. Redis: ARSET.</summary>
    RespirePending<ulong> Set(RespireKey key, ulong index, RespireValue[] values);

    /// <summary>Writes scattered slots; returns newly populated slot count. Does not move the insertion cursor. Redis: ARMSET.</summary>
    RespirePending<ulong> SetMany(RespireKey key, params ReadOnlySpan<RespireArrayItem> items);

    /// <summary>Deletes populated slots without shifting indexes; removes an empty key. Redis: ARDEL.</summary>
    RespirePending<ulong> Delete(RespireKey key, params ReadOnlySpan<ulong> indexes);

    /// <summary>Deletes one or more inclusive ranges without shifting slots; returns removed slot count. Redis: ARDELRANGE.</summary>
    RespirePending<ulong> DeleteRange(RespireKey key, params ReadOnlySpan<RespireArrayRange> ranges);

    /// <summary>Writes at the insertion cursor; returns the last written index. Redis: ARINSERT.</summary>
    RespirePending<ulong> Insert(RespireKey key, params ReadOnlySpan<RespireValue> values);

    /// <summary>Writes with wrapping and preserves the recent contiguous tail on resize; returns the last written index. Redis: ARRING.</summary>
    RespirePending<ulong> Ring(RespireKey key, long size, params ReadOnlySpan<RespireValue> values);

    /// <summary>Next insertion index; zero before insertion or when missing, null when exhausted. Redis: ARNEXT.</summary>
    RespirePending<ulong?> NextIndex(RespireKey key);

    /// <summary>Sets the next insertion position; false when missing. UInt64.MaxValue exhausts the cursor. Redis: ARSEEK.</summary>
    RespirePending<bool> Seek(RespireKey key, ulong index);

    /// <summary>Recent positions, retaining holes; oldest first unless reverse. Nonpositive count returns empty. Redis: ARLASTITEMS.</summary>
    RespirePending<string?[]> LastItems(RespireKey key, long count, bool reverse = false);

    /// <summary>Deserialized recent positions. Use nullable value types to retain holes. Redis: ARLASTITEMS.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    RespirePending<T?[]> LastItems<T>(RespireKey key, long count, bool reverse = false);

    /// <summary>Array metadata, with encoding statistics when full. Missing keys raise a server error. Redis: ARINFO.</summary>
    RespirePending<RespireArrayInfo> Info(RespireKey key, bool full = false);

    /// <summary>Aggregates existing slots, preserving numeric text and nil. MATCH requires a match value. Redis: AROP.</summary>
    RespirePending<RespireArrayAggregate> Aggregate(RespireKey key, ulong start, ulong end, RespireArrayOperation operation, RespireValue? match = null);

    /// <summary>Existing slots in inclusive index order; limit caps populated slots. Redis uses index ranges, not cursors. Redis: ARSCAN.</summary>
    RespirePending<RespireArrayEntry<string>[]> ScanPage(RespireKey key, ulong start, ulong end, long? limit = null);

    /// <summary>Deserialized existing slots in inclusive index order; limit caps populated slots. Redis: ARSCAN.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    RespirePending<RespireArrayEntry<T>[]> ScanPage<T>(RespireKey key, ulong start, ulong end, long? limit = null);

    /// <summary>Matching indexes in range order. Predicates use OR unless MatchAll is set. Redis: ARGREP.</summary>
    RespirePending<ulong[]> Grep(RespireKey key, RespireArrayBound start, RespireArrayBound end, ReadOnlySpan<RespireArrayPredicate> predicates, RespireArrayGrepOptions options);

    /// <summary>Matching indexes and values in range order. Redis: ARGREP.</summary>
    RespirePending<RespireArrayEntry<string>[]> GrepEntries(RespireKey key, RespireArrayBound start, RespireArrayBound end, ReadOnlySpan<RespireArrayPredicate> predicates, RespireArrayGrepOptions options);

    /// <summary>Matching indexes and deserialized values in range order. Redis: ARGREP.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    RespirePending<RespireArrayEntry<T>[]> GrepEntries<T>(RespireKey key, RespireArrayBound start, RespireArrayBound end, ReadOnlySpan<RespireArrayPredicate> predicates, RespireArrayGrepOptions options);

}

internal sealed class BatchArrayCommands(IPendingSink sink) : IBatchArrayCommands
{
    public RespirePending<ulong[]> Grep(RespireKey key, RespireArrayBound start, RespireArrayBound end, params ReadOnlySpan<RespireArrayPredicate> predicates)
        => Grep(key, start, end, predicates, default);

    public RespirePending<RespireArrayEntry<string>[]> GrepEntries(RespireKey key, RespireArrayBound start, RespireArrayBound end, params ReadOnlySpan<RespireArrayPredicate> predicates)
        => GrepEntries(key, start, end, predicates, default);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public RespirePending<RespireArrayEntry<T>[]> GrepEntries<T>(RespireKey key, RespireArrayBound start, RespireArrayBound end, params ReadOnlySpan<RespireArrayPredicate> predicates)
        => GrepEntries<T>(key, start, end, predicates, default);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public RespirePending<ulong> Set<T>(RespireKey key, ulong index, T value)
    {
        ArrayCommandArguments.ValidateIndex(index);
        return sink.Add<Cmd3, ulong>("ARSET", new Cmd3(RespireCommands.Array.ARSET.Verb, sink.Client.Key(in key), index,
            sink.Client.SerializeRawCompatible(value)), static (c, v) => ArrayResponseReader.Unsigned(in v));
    }

    public RespirePending<ulong> Count(RespireKey key)
        => Read(RespireCommands.Array.ARCOUNT, key, [], static (c, v) => ArrayResponseReader.Unsigned(in v));

    public RespirePending<ulong> Length(RespireKey key)
        => Read(RespireCommands.Array.ARLEN, key, [], static (c, v) => ArrayResponseReader.Unsigned(in v));

    public RespirePending<string?> GetString(RespireKey key, ulong index)
        => ReadIndex(RespireCommands.Array.ARGET, key, index, static (c, v) => ResponseReader.StringOrNull(in v));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public RespirePending<T?> Get<T>(RespireKey key, ulong index)
        => ReadIndex(RespireCommands.Array.ARGET, key, index, static (c, v) => c.DeserializeBorrowed<T>(in v));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public RespirePending<RespireGet<T>> TryGet<T>(RespireKey key, ulong index)
        => ReadIndex(RespireCommands.Array.ARGET, key, index, static (c, v) => c.TryDeserializeBorrowed<T>(in v));

    public RespirePending<string?[]> GetMany(RespireKey key, params ReadOnlySpan<ulong> indexes)
        => Read(RespireCommands.Array.ARMGET, key, ArrayCommandArguments.Indexes(indexes), static (c, v) => ResponseReader.NullableStringArray(in v));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public RespirePending<T?[]> GetMany<T>(RespireKey key, params ReadOnlySpan<ulong> indexes)
        => Read(RespireCommands.Array.ARMGET, key, ArrayCommandArguments.Indexes(indexes), static (c, v) => c.DeserializeNullableArray<T>(in v));

    public RespirePending<string?[]> Range(RespireKey key, ulong start, ulong end)
        => Read(RespireCommands.Array.ARGETRANGE, key, ArrayCommandArguments.Range(start, end), static (c, v) => ResponseReader.NullableStringArray(in v));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public RespirePending<T?[]> Range<T>(RespireKey key, ulong start, ulong end)
        => Read(RespireCommands.Array.ARGETRANGE, key, ArrayCommandArguments.Range(start, end), static (c, v) => c.DeserializeNullableArray<T>(in v));

    public RespirePending<ulong> Set(RespireKey key, ulong index, params ReadOnlySpan<RespireValue> values)
        => Read(RespireCommands.Array.ARSET, key, ArrayCommandArguments.Set(index, values), static (c, v) => ArrayResponseReader.Unsigned(in v));

    public RespirePending<ulong> Set(RespireKey key, ulong index, RespireValue[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Set(key, index, values.AsSpan());
    }

    public RespirePending<ulong> SetMany(RespireKey key, params ReadOnlySpan<RespireArrayItem> items)
        => Read(RespireCommands.Array.ARMSET, key, ArrayCommandArguments.SetMany(items), static (c, v) => ArrayResponseReader.Unsigned(in v));

    public RespirePending<ulong> Delete(RespireKey key, params ReadOnlySpan<ulong> indexes)
        => Read(RespireCommands.Array.ARDEL, key, ArrayCommandArguments.Indexes(indexes), static (c, v) => ArrayResponseReader.Unsigned(in v));

    public RespirePending<ulong> DeleteRange(RespireKey key, params ReadOnlySpan<RespireArrayRange> ranges)
        => Read(RespireCommands.Array.ARDELRANGE, key, ArrayCommandArguments.Ranges(ranges), static (c, v) => ArrayResponseReader.Unsigned(in v));

    public RespirePending<ulong> Insert(RespireKey key, params ReadOnlySpan<RespireValue> values)
        => Read(RespireCommands.Array.ARINSERT, key, ArrayCommandArguments.Values(values), static (c, v) => ArrayResponseReader.Unsigned(in v));

    public RespirePending<ulong> Ring(RespireKey key, long size, params ReadOnlySpan<RespireValue> values)
        => Read(RespireCommands.Array.ARRING, key, ArrayCommandArguments.Ring(size, values), static (c, v) => ArrayResponseReader.Unsigned(in v));

    public RespirePending<ulong?> NextIndex(RespireKey key)
        => Read(RespireCommands.Array.ARNEXT, key, [], static (c, v) => ArrayResponseReader.UnsignedOrNull(in v));

    public RespirePending<bool> Seek(RespireKey key, ulong index)
        => Read(RespireCommands.Array.ARSEEK, key, [index], static (c, v) => ResponseReader.Flag(in v));

    public RespirePending<string?[]> LastItems(RespireKey key, long count, bool reverse = false)
        => Read(RespireCommands.Array.ARLASTITEMS, key, ArrayCommandArguments.LastItems(count, reverse), static (c, v) => ResponseReader.NullableStringArray(in v));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public RespirePending<T?[]> LastItems<T>(RespireKey key, long count, bool reverse = false)
        => Read(RespireCommands.Array.ARLASTITEMS, key, ArrayCommandArguments.LastItems(count, reverse), static (c, v) => c.DeserializeNullableArray<T>(in v));

    public RespirePending<RespireArrayInfo> Info(RespireKey key, bool full = false)
        => Read(RespireCommands.Array.ARINFO, key, ArrayCommandArguments.Info(full), static (c, v) => ArrayResponseReader.Info(in v));

    public RespirePending<RespireArrayAggregate> Aggregate(RespireKey key, ulong start, ulong end, RespireArrayOperation operation, RespireValue? match = null)
        => Read(RespireCommands.Array.AROP, key, ArrayCommandArguments.Aggregate(start, end, operation, match), static (c, v) => ArrayResponseReader.Aggregate(in v));

    public RespirePending<RespireArrayEntry<string>[]> ScanPage(RespireKey key, ulong start, ulong end, long? limit = null)
        => Read(RespireCommands.Array.ARSCAN, key, ArrayCommandArguments.Range(start, end, limit), static (c, v) => ArrayResponseReader.Entries(in v));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public RespirePending<RespireArrayEntry<T>[]> ScanPage<T>(RespireKey key, ulong start, ulong end, long? limit = null)
        => Read(RespireCommands.Array.ARSCAN, key, ArrayCommandArguments.Range(start, end, limit), static (c, v) => ArrayResponseReader.Entries<T>(c, in v));

    public RespirePending<ulong[]> Grep(RespireKey key, RespireArrayBound start, RespireArrayBound end, ReadOnlySpan<RespireArrayPredicate> predicates, RespireArrayGrepOptions options)
        => Read(RespireCommands.Array.ARGREP, key, ArrayCommandArguments.Grep(start, end, predicates, options, withValues: false), static (c, v) => ArrayResponseReader.Indexes(in v));

    public RespirePending<RespireArrayEntry<string>[]> GrepEntries(RespireKey key, RespireArrayBound start, RespireArrayBound end, ReadOnlySpan<RespireArrayPredicate> predicates, RespireArrayGrepOptions options)
        => Read(RespireCommands.Array.ARGREP, key, ArrayCommandArguments.Grep(start, end, predicates, options, withValues: true), static (c, v) => ArrayResponseReader.Entries(in v));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public RespirePending<RespireArrayEntry<T>[]> GrepEntries<T>(RespireKey key, RespireArrayBound start, RespireArrayBound end, ReadOnlySpan<RespireArrayPredicate> predicates, RespireArrayGrepOptions options)
        => Read(RespireCommands.Array.ARGREP, key, ArrayCommandArguments.Grep(start, end, predicates, options, withValues: true), static (c, v) => ArrayResponseReader.Entries<T>(c, in v));

    private RespirePending<T> ReadIndex<T>(RespireCommand command, RespireKey key, ulong index, Func<RespireClient, RespValue, T> reader)
    {
        ArrayCommandArguments.ValidateIndex(index);
        return sink.Add<Cmd2, T>(command.Name, new Cmd2(command.Verb, sink.Client.Key(in key), index), reader);
    }

    private RespirePending<T> Read<T>(RespireCommand command, RespireKey key, RespireValue[] args, Func<RespireClient, RespValue, T> reader)
        => sink.Add<Cmd1N, T>(command.Name, new Cmd1N(command.Verb, sink.Client.Key(in key), args), reader);
}
