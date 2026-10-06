using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;
using Respire.Serialization;

namespace Respire;

/// <summary>Redis 8.10 sparse arrays with unsigned indexes. Reads preserve holes; generic value types require nullable T or TryGet to distinguish absent slots.</summary>
public interface IArrayCommands
{
    /// <summary>Matching indexes with default OR options. Redis: ARGREP.</summary>
    ValueTask<ulong[]> GrepAsync(RespireKey key, RespireArrayBound start, RespireArrayBound end, params ReadOnlySpan<RespireArrayPredicate> predicates);

    /// <summary>Matching indexes with default OR options. Redis: ARGREP.</summary>
    ValueTask<ulong[]> GrepAsync(RespireKey key, RespireArrayBound start, RespireArrayBound end, ReadOnlySpan<RespireArrayPredicate> predicates, CancellationToken cancellationToken);

    /// <summary>Matching indexes and values with default OR options. Redis: ARGREP.</summary>
    ValueTask<RespireArrayEntry<string>[]> GrepEntriesAsync(RespireKey key, RespireArrayBound start, RespireArrayBound end, params ReadOnlySpan<RespireArrayPredicate> predicates);

    /// <summary>Matching indexes and values with default OR options. Redis: ARGREP.</summary>
    ValueTask<RespireArrayEntry<string>[]> GrepEntriesAsync(RespireKey key, RespireArrayBound start, RespireArrayBound end, ReadOnlySpan<RespireArrayPredicate> predicates, CancellationToken cancellationToken);

    /// <summary>Matching indexes and values with default OR options. Redis: ARGREP.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<RespireArrayEntry<T>[]> GrepEntriesAsync<T>(RespireKey key, RespireArrayBound start, RespireArrayBound end, params ReadOnlySpan<RespireArrayPredicate> predicates);

    /// <summary>Matching indexes and values with default OR options. Redis: ARGREP.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<RespireArrayEntry<T>[]> GrepEntriesAsync<T>(RespireKey key, RespireArrayBound start, RespireArrayBound end, ReadOnlySpan<RespireArrayPredicate> predicates, CancellationToken cancellationToken);

    /// <summary>Serializes a value into one slot and returns whether it was newly populated, as 0 or 1. Redis: ARSET.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<ulong> SetAsync<T>(RespireKey key, ulong index, T value, CancellationToken cancellationToken = default);

    /// <summary>Number of populated slots, zero when missing. Redis: ARCOUNT.</summary>
    ValueTask<ulong> CountAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Highest populated index plus one, zero when missing. Redis: ARLEN.</summary>
    ValueTask<ulong> LengthAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Value at an index, or null for a missing key or slot. Redis: ARGET.</summary>
    ValueTask<string?> GetStringAsync(RespireKey key, ulong index, CancellationToken cancellationToken = default);

    /// <summary>Deserialized value, or default when missing. Use nullable value types or TryGet to preserve presence. Redis: ARGET.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<T?> GetAsync<T>(RespireKey key, ulong index, CancellationToken cancellationToken = default);

    /// <summary>Deserialized value with explicit slot presence, including stored defaults. Redis: ARGET.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<RespireGet<T>> TryGetAsync<T>(RespireKey key, ulong index, CancellationToken cancellationToken = default);

    /// <summary>Values in requested index order, retaining null holes. Redis: ARMGET.</summary>
    ValueTask<string?[]> GetManyAsync(RespireKey key, params ReadOnlySpan<ulong> indexes);

    /// <summary>Values in requested index order, retaining null holes. Redis: ARMGET.</summary>
    ValueTask<string?[]> GetManyAsync(RespireKey key, ReadOnlySpan<ulong> indexes, CancellationToken cancellationToken);

    /// <summary>Values in requested index order. Use nullable value types to retain holes. Redis: ARMGET.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<T?[]> GetManyAsync<T>(RespireKey key, params ReadOnlySpan<ulong> indexes);

    /// <summary>Values in requested index order. Use nullable value types to retain holes. Redis: ARMGET.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<T?[]> GetManyAsync<T>(RespireKey key, ReadOnlySpan<ulong> indexes, CancellationToken cancellationToken);

    /// <summary>Inclusive range retaining holes; descending endpoints return reverse order. Redis: ARGETRANGE.</summary>
    ValueTask<string?[]> RangeAsync(RespireKey key, ulong start, ulong end, CancellationToken cancellationToken = default);

    /// <summary>Deserialized inclusive range. Use nullable value types to retain holes. Redis: ARGETRANGE.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<T?[]> RangeAsync<T>(RespireKey key, ulong start, ulong end, CancellationToken cancellationToken = default);

    /// <summary>Writes consecutive slots; returns newly populated slot count. Does not move the insertion cursor. Redis: ARSET.</summary>
    ValueTask<ulong> SetAsync(RespireKey key, ulong index, params ReadOnlySpan<RespireValue> values);

    /// <summary>Writes consecutive slots; returns newly populated slot count. Does not move the insertion cursor. Redis: ARSET.</summary>
    ValueTask<ulong> SetAsync(RespireKey key, ulong index, ReadOnlySpan<RespireValue> values, CancellationToken cancellationToken);

    /// <summary>Writes an array of values to consecutive slots. Redis: ARSET.</summary>
    ValueTask<ulong> SetAsync(RespireKey key, ulong index, RespireValue[] values, CancellationToken cancellationToken = default);

    /// <summary>Writes scattered slots; returns newly populated slot count. Does not move the insertion cursor. Redis: ARMSET.</summary>
    ValueTask<ulong> SetManyAsync(RespireKey key, params ReadOnlySpan<RespireArrayItem> items);

    /// <summary>Writes scattered slots; returns newly populated slot count. Does not move the insertion cursor. Redis: ARMSET.</summary>
    ValueTask<ulong> SetManyAsync(RespireKey key, ReadOnlySpan<RespireArrayItem> items, CancellationToken cancellationToken);

    /// <summary>Deletes populated slots without shifting indexes; removes an empty key. Redis: ARDEL.</summary>
    ValueTask<ulong> DeleteAsync(RespireKey key, params ReadOnlySpan<ulong> indexes);

    /// <summary>Deletes populated slots without shifting indexes; removes an empty key. Redis: ARDEL.</summary>
    ValueTask<ulong> DeleteAsync(RespireKey key, ReadOnlySpan<ulong> indexes, CancellationToken cancellationToken);

    /// <summary>Deletes one or more inclusive ranges without shifting slots; returns removed slot count. Redis: ARDELRANGE.</summary>
    ValueTask<ulong> DeleteRangeAsync(RespireKey key, params ReadOnlySpan<RespireArrayRange> ranges);

    /// <summary>Deletes one or more inclusive ranges without shifting slots; returns removed slot count. Redis: ARDELRANGE.</summary>
    ValueTask<ulong> DeleteRangeAsync(RespireKey key, ReadOnlySpan<RespireArrayRange> ranges, CancellationToken cancellationToken);

    /// <summary>Writes at the insertion cursor; returns the last written index. Redis: ARINSERT.</summary>
    ValueTask<ulong> InsertAsync(RespireKey key, params ReadOnlySpan<RespireValue> values);

    /// <summary>Writes at the insertion cursor; returns the last written index. Redis: ARINSERT.</summary>
    ValueTask<ulong> InsertAsync(RespireKey key, ReadOnlySpan<RespireValue> values, CancellationToken cancellationToken);

    /// <summary>Writes with wrapping and preserves the recent contiguous tail on resize; returns the last written index. Redis: ARRING.</summary>
    ValueTask<ulong> RingAsync(RespireKey key, long size, params ReadOnlySpan<RespireValue> values);

    /// <summary>Writes with wrapping and preserves the recent contiguous tail on resize; returns the last written index. Redis: ARRING.</summary>
    ValueTask<ulong> RingAsync(RespireKey key, long size, ReadOnlySpan<RespireValue> values, CancellationToken cancellationToken);

    /// <summary>Next insertion index; zero before insertion or when missing, null when exhausted. Redis: ARNEXT.</summary>
    ValueTask<ulong?> NextIndexAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Sets the next insertion position; false when missing. UInt64.MaxValue exhausts the cursor. Redis: ARSEEK.</summary>
    ValueTask<bool> SeekAsync(RespireKey key, ulong index, CancellationToken cancellationToken = default);

    /// <summary>Recent positions, retaining holes; oldest first unless reverse. Nonpositive count returns empty. Redis: ARLASTITEMS.</summary>
    ValueTask<string?[]> LastItemsAsync(RespireKey key, long count, bool reverse = false, CancellationToken cancellationToken = default);

    /// <summary>Deserialized recent positions. Use nullable value types to retain holes. Redis: ARLASTITEMS.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<T?[]> LastItemsAsync<T>(RespireKey key, long count, bool reverse = false, CancellationToken cancellationToken = default);

    /// <summary>Array metadata, with encoding statistics when full. Missing keys raise a server error. Redis: ARINFO.</summary>
    ValueTask<RespireArrayInfo> InfoAsync(RespireKey key, bool full = false, CancellationToken cancellationToken = default);

    /// <summary>Aggregates existing slots, preserving numeric text and nil. MATCH requires a match value. Redis: AROP.</summary>
    ValueTask<RespireArrayAggregate> AggregateAsync(RespireKey key, ulong start, ulong end, RespireArrayOperation operation, RespireValue? match = null, CancellationToken cancellationToken = default);

    /// <summary>Existing slots in inclusive index order; limit caps populated slots. Redis uses index ranges, not cursors. Redis: ARSCAN.</summary>
    ValueTask<RespireArrayEntry<string>[]> ScanPageAsync(RespireKey key, ulong start, ulong end, long? limit = null, CancellationToken cancellationToken = default);

    /// <summary>Deserialized existing slots in inclusive index order; limit caps populated slots. Redis: ARSCAN.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<RespireArrayEntry<T>[]> ScanPageAsync<T>(RespireKey key, ulong start, ulong end, long? limit = null, CancellationToken cancellationToken = default);

    /// <summary>Matching indexes in range order. Predicates use OR unless MatchAll is set. Redis: ARGREP.</summary>
    ValueTask<ulong[]> GrepAsync(RespireKey key, RespireArrayBound start, RespireArrayBound end, ReadOnlySpan<RespireArrayPredicate> predicates, RespireArrayGrepOptions options, CancellationToken cancellationToken = default);

    /// <summary>Matching indexes and values in range order. Redis: ARGREP.</summary>
    ValueTask<RespireArrayEntry<string>[]> GrepEntriesAsync(RespireKey key, RespireArrayBound start, RespireArrayBound end, ReadOnlySpan<RespireArrayPredicate> predicates, RespireArrayGrepOptions options, CancellationToken cancellationToken = default);

    /// <summary>Matching indexes and deserialized values in range order. Redis: ARGREP.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<RespireArrayEntry<T>[]> GrepEntriesAsync<T>(RespireKey key, RespireArrayBound start, RespireArrayBound end, ReadOnlySpan<RespireArrayPredicate> predicates, RespireArrayGrepOptions options, CancellationToken cancellationToken = default);

    /// <summary>Pages populated slots by index. Each page is a separate read, not an atomic snapshot. Cancelling enumeration cancels the pending page.</summary>
    IAsyncEnumerable<RespireArrayEntry<string>> ScanAsync(RespireKey key, ulong start, ulong end, int pageSize = 100, CancellationToken cancellationToken = default);

    /// <summary>Pages populated slots by index. Each page is a separate read, not an atomic snapshot. Cancelling enumeration cancels the pending page.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    IAsyncEnumerable<RespireArrayEntry<T>> ScanAsync<T>(RespireKey key, ulong start, ulong end, int pageSize = 100, CancellationToken cancellationToken = default);

}

internal sealed class ArrayCommands(RespireClient client) : IArrayCommands
{
    public ValueTask<ulong[]> GrepAsync(RespireKey key, RespireArrayBound start, RespireArrayBound end, params ReadOnlySpan<RespireArrayPredicate> predicates)
        => GrepAsync(key, start, end, predicates, default, CancellationToken.None);

    public ValueTask<ulong[]> GrepAsync(RespireKey key, RespireArrayBound start, RespireArrayBound end, ReadOnlySpan<RespireArrayPredicate> predicates, CancellationToken cancellationToken)
        => GrepAsync(key, start, end, predicates, default, cancellationToken);

    public ValueTask<RespireArrayEntry<string>[]> GrepEntriesAsync(RespireKey key, RespireArrayBound start, RespireArrayBound end, params ReadOnlySpan<RespireArrayPredicate> predicates)
        => GrepEntriesAsync(key, start, end, predicates, default, CancellationToken.None);

    public ValueTask<RespireArrayEntry<string>[]> GrepEntriesAsync(RespireKey key, RespireArrayBound start, RespireArrayBound end, ReadOnlySpan<RespireArrayPredicate> predicates, CancellationToken cancellationToken)
        => GrepEntriesAsync(key, start, end, predicates, default, cancellationToken);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<RespireArrayEntry<T>[]> GrepEntriesAsync<T>(RespireKey key, RespireArrayBound start, RespireArrayBound end, params ReadOnlySpan<RespireArrayPredicate> predicates)
        => GrepEntriesAsync<T>(key, start, end, predicates, default, CancellationToken.None);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<RespireArrayEntry<T>[]> GrepEntriesAsync<T>(RespireKey key, RespireArrayBound start, RespireArrayBound end, ReadOnlySpan<RespireArrayPredicate> predicates, CancellationToken cancellationToken)
        => GrepEntriesAsync<T>(key, start, end, predicates, default, cancellationToken);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<ulong> SetAsync<T>(RespireKey key, ulong index, T value, CancellationToken cancellationToken = default)
    {
        ArrayCommandArguments.ValidateIndex(index);
        return client.ConvertResponseAsync("ARSET", new Cmd3(RespireCommands.Array.ARSET.Verb, client.Key(in key), index,
            client.SerializeRawCompatible(value)), cancellationToken, 0,
            static (int _, in RespValue reply) => ArrayResponseReader.Unsigned(in reply));
    }

    public ValueTask<ulong> CountAsync(RespireKey key, CancellationToken cancellationToken = default)
        => Read(RespireCommands.Array.ARCOUNT, key, [], cancellationToken, static (RespireClient c, in RespValue v) => ArrayResponseReader.Unsigned(in v));

    public ValueTask<ulong> LengthAsync(RespireKey key, CancellationToken cancellationToken = default)
        => Read(RespireCommands.Array.ARLEN, key, [], cancellationToken, static (RespireClient c, in RespValue v) => ArrayResponseReader.Unsigned(in v));

    public ValueTask<string?> GetStringAsync(RespireKey key, ulong index, CancellationToken cancellationToken = default)
        => ReadIndex(RespireCommands.Array.ARGET, key, index, cancellationToken, static (RespireClient c, in RespValue v) => ResponseReader.StringOrNull(in v));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T?> GetAsync<T>(RespireKey key, ulong index, CancellationToken cancellationToken = default)
        => ReadIndex(RespireCommands.Array.ARGET, key, index, cancellationToken, static (RespireClient c, in RespValue v) => c.DeserializeBorrowed<T>(in v));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<RespireGet<T>> TryGetAsync<T>(RespireKey key, ulong index, CancellationToken cancellationToken = default)
        => ReadIndex(RespireCommands.Array.ARGET, key, index, cancellationToken, static (RespireClient c, in RespValue v) => c.TryDeserializeBorrowed<T>(in v));

    public ValueTask<string?[]> GetManyAsync(RespireKey key, params ReadOnlySpan<ulong> indexes)
        => GetManyAsync(key, indexes, CancellationToken.None);

    public ValueTask<string?[]> GetManyAsync(RespireKey key, ReadOnlySpan<ulong> indexes, CancellationToken cancellationToken)
        => Read(RespireCommands.Array.ARMGET, key, ArrayCommandArguments.Indexes(indexes), cancellationToken, static (RespireClient c, in RespValue v) => ResponseReader.NullableStringArray(in v));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T?[]> GetManyAsync<T>(RespireKey key, params ReadOnlySpan<ulong> indexes)
        => GetManyAsync<T>(key, indexes, CancellationToken.None);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T?[]> GetManyAsync<T>(RespireKey key, ReadOnlySpan<ulong> indexes, CancellationToken cancellationToken)
        => Read(RespireCommands.Array.ARMGET, key, ArrayCommandArguments.Indexes(indexes), cancellationToken, static (RespireClient c, in RespValue v) => c.DeserializeNullableArray<T>(in v));

    public ValueTask<string?[]> RangeAsync(RespireKey key, ulong start, ulong end, CancellationToken cancellationToken = default)
        => Read(RespireCommands.Array.ARGETRANGE, key, ArrayCommandArguments.Range(start, end), cancellationToken, static (RespireClient c, in RespValue v) => ResponseReader.NullableStringArray(in v));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T?[]> RangeAsync<T>(RespireKey key, ulong start, ulong end, CancellationToken cancellationToken = default)
        => Read(RespireCommands.Array.ARGETRANGE, key, ArrayCommandArguments.Range(start, end), cancellationToken, static (RespireClient c, in RespValue v) => c.DeserializeNullableArray<T>(in v));

    public ValueTask<ulong> SetAsync(RespireKey key, ulong index, params ReadOnlySpan<RespireValue> values)
        => SetAsync(key, index, values, CancellationToken.None);

    public ValueTask<ulong> SetAsync(RespireKey key, ulong index, ReadOnlySpan<RespireValue> values, CancellationToken cancellationToken)
        => Read(RespireCommands.Array.ARSET, key, ArrayCommandArguments.Set(index, values), cancellationToken, static (RespireClient c, in RespValue v) => ArrayResponseReader.Unsigned(in v));

    public ValueTask<ulong> SetAsync(RespireKey key, ulong index, RespireValue[] values, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        return SetAsync(key, index, values.AsSpan(), cancellationToken);
    }

    public ValueTask<ulong> SetManyAsync(RespireKey key, params ReadOnlySpan<RespireArrayItem> items)
        => SetManyAsync(key, items, CancellationToken.None);

    public ValueTask<ulong> SetManyAsync(RespireKey key, ReadOnlySpan<RespireArrayItem> items, CancellationToken cancellationToken)
        => Read(RespireCommands.Array.ARMSET, key, ArrayCommandArguments.SetMany(items), cancellationToken, static (RespireClient c, in RespValue v) => ArrayResponseReader.Unsigned(in v));

    public ValueTask<ulong> DeleteAsync(RespireKey key, params ReadOnlySpan<ulong> indexes)
        => DeleteAsync(key, indexes, CancellationToken.None);

    public ValueTask<ulong> DeleteAsync(RespireKey key, ReadOnlySpan<ulong> indexes, CancellationToken cancellationToken)
        => Read(RespireCommands.Array.ARDEL, key, ArrayCommandArguments.Indexes(indexes), cancellationToken, static (RespireClient c, in RespValue v) => ArrayResponseReader.Unsigned(in v));

    public ValueTask<ulong> DeleteRangeAsync(RespireKey key, params ReadOnlySpan<RespireArrayRange> ranges)
        => DeleteRangeAsync(key, ranges, CancellationToken.None);

    public ValueTask<ulong> DeleteRangeAsync(RespireKey key, ReadOnlySpan<RespireArrayRange> ranges, CancellationToken cancellationToken)
        => Read(RespireCommands.Array.ARDELRANGE, key, ArrayCommandArguments.Ranges(ranges), cancellationToken, static (RespireClient c, in RespValue v) => ArrayResponseReader.Unsigned(in v));

    public ValueTask<ulong> InsertAsync(RespireKey key, params ReadOnlySpan<RespireValue> values)
        => InsertAsync(key, values, CancellationToken.None);

    public ValueTask<ulong> InsertAsync(RespireKey key, ReadOnlySpan<RespireValue> values, CancellationToken cancellationToken)
        => Read(RespireCommands.Array.ARINSERT, key, ArrayCommandArguments.Values(values), cancellationToken, static (RespireClient c, in RespValue v) => ArrayResponseReader.Unsigned(in v));

    public ValueTask<ulong> RingAsync(RespireKey key, long size, params ReadOnlySpan<RespireValue> values)
        => RingAsync(key, size, values, CancellationToken.None);

    public ValueTask<ulong> RingAsync(RespireKey key, long size, ReadOnlySpan<RespireValue> values, CancellationToken cancellationToken)
        => Read(RespireCommands.Array.ARRING, key, ArrayCommandArguments.Ring(size, values), cancellationToken, static (RespireClient c, in RespValue v) => ArrayResponseReader.Unsigned(in v));

    public ValueTask<ulong?> NextIndexAsync(RespireKey key, CancellationToken cancellationToken = default)
        => Read(RespireCommands.Array.ARNEXT, key, [], cancellationToken, static (RespireClient c, in RespValue v) => ArrayResponseReader.UnsignedOrNull(in v));

    public ValueTask<bool> SeekAsync(RespireKey key, ulong index, CancellationToken cancellationToken = default)
        => Read(RespireCommands.Array.ARSEEK, key, [index], cancellationToken, static (RespireClient c, in RespValue v) => ResponseReader.Flag(in v));

    public ValueTask<string?[]> LastItemsAsync(RespireKey key, long count, bool reverse = false, CancellationToken cancellationToken = default)
        => Read(RespireCommands.Array.ARLASTITEMS, key, ArrayCommandArguments.LastItems(count, reverse), cancellationToken, static (RespireClient c, in RespValue v) => ResponseReader.NullableStringArray(in v));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T?[]> LastItemsAsync<T>(RespireKey key, long count, bool reverse = false, CancellationToken cancellationToken = default)
        => Read(RespireCommands.Array.ARLASTITEMS, key, ArrayCommandArguments.LastItems(count, reverse), cancellationToken, static (RespireClient c, in RespValue v) => c.DeserializeNullableArray<T>(in v));

    public ValueTask<RespireArrayInfo> InfoAsync(RespireKey key, bool full = false, CancellationToken cancellationToken = default)
        => Read(RespireCommands.Array.ARINFO, key, ArrayCommandArguments.Info(full), cancellationToken, static (RespireClient c, in RespValue v) => ArrayResponseReader.Info(in v));

    public ValueTask<RespireArrayAggregate> AggregateAsync(RespireKey key, ulong start, ulong end, RespireArrayOperation operation, RespireValue? match = null, CancellationToken cancellationToken = default)
        => Read(RespireCommands.Array.AROP, key, ArrayCommandArguments.Aggregate(start, end, operation, match), cancellationToken, static (RespireClient c, in RespValue v) => ArrayResponseReader.Aggregate(in v));

    public ValueTask<RespireArrayEntry<string>[]> ScanPageAsync(RespireKey key, ulong start, ulong end, long? limit = null, CancellationToken cancellationToken = default)
        => Read(RespireCommands.Array.ARSCAN, key, ArrayCommandArguments.Range(start, end, limit), cancellationToken, static (RespireClient c, in RespValue v) => ArrayResponseReader.Entries(in v));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<RespireArrayEntry<T>[]> ScanPageAsync<T>(RespireKey key, ulong start, ulong end, long? limit = null, CancellationToken cancellationToken = default)
        => Read(RespireCommands.Array.ARSCAN, key, ArrayCommandArguments.Range(start, end, limit), cancellationToken, static (RespireClient c, in RespValue v) => ArrayResponseReader.Entries<T>(c, in v));

    public ValueTask<ulong[]> GrepAsync(RespireKey key, RespireArrayBound start, RespireArrayBound end, ReadOnlySpan<RespireArrayPredicate> predicates, RespireArrayGrepOptions options, CancellationToken cancellationToken = default)
        => Read(RespireCommands.Array.ARGREP, key, ArrayCommandArguments.Grep(start, end, predicates, options, withValues: false), cancellationToken, static (RespireClient c, in RespValue v) => ArrayResponseReader.Indexes(in v));

    public ValueTask<RespireArrayEntry<string>[]> GrepEntriesAsync(RespireKey key, RespireArrayBound start, RespireArrayBound end, ReadOnlySpan<RespireArrayPredicate> predicates, RespireArrayGrepOptions options, CancellationToken cancellationToken = default)
        => Read(RespireCommands.Array.ARGREP, key, ArrayCommandArguments.Grep(start, end, predicates, options, withValues: true), cancellationToken, static (RespireClient c, in RespValue v) => ArrayResponseReader.Entries(in v));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<RespireArrayEntry<T>[]> GrepEntriesAsync<T>(RespireKey key, RespireArrayBound start, RespireArrayBound end, ReadOnlySpan<RespireArrayPredicate> predicates, RespireArrayGrepOptions options, CancellationToken cancellationToken = default)
        => Read(RespireCommands.Array.ARGREP, key, ArrayCommandArguments.Grep(start, end, predicates, options, withValues: true), cancellationToken, static (RespireClient c, in RespValue v) => ArrayResponseReader.Entries<T>(c, in v));

    public async IAsyncEnumerable<RespireArrayEntry<string>> ScanAsync(RespireKey key, ulong start, ulong end, int pageSize = 100,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArrayCommandArguments.ValidateIndex(start);
        ArrayCommandArguments.ValidateIndex(end);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        var reverse = start > end;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await ScanPageAsync(key, start, end, pageSize, cancellationToken).ConfigureAwait(false);
            if (page.Length == 0) yield break;
            foreach (var item in page) { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
            var last = page[^1].Index;
            if (reverse ? last <= end : last >= end) yield break;
            start = reverse ? last - 1 : last + 1;
        }
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public async IAsyncEnumerable<RespireArrayEntry<T>> ScanAsync<T>(RespireKey key, ulong start, ulong end, int pageSize = 100,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArrayCommandArguments.ValidateIndex(start);
        ArrayCommandArguments.ValidateIndex(end);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        var reverse = start > end;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await ScanPageAsync<T>(key, start, end, pageSize, cancellationToken).ConfigureAwait(false);
            if (page.Length == 0) yield break;
            foreach (var item in page) { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
            var last = page[^1].Index;
            if (reverse ? last <= end : last >= end) yield break;
            start = reverse ? last - 1 : last + 1;
        }
    }

    private ValueTask<T> ReadIndex<T>(RespireCommand command, RespireKey key, ulong index, CancellationToken cancellationToken,
        ResponseConverter<RespireClient, T> reader)
    {
        ArrayCommandArguments.ValidateIndex(index);
        return client.ConvertResponseAsync(command.Name, new Cmd2(command.Verb, client.Key(in key), index), cancellationToken, client, reader);
    }

    private ValueTask<T> Read<T>(RespireCommand command, RespireKey key, RespireValue[] args, CancellationToken cancellationToken,
        ResponseConverter<RespireClient, T> reader)
        => client.ConvertResponseAsync(command.Name, new Cmd1N(command.Verb, client.Key(in key), args), cancellationToken, client, reader);
}
