using System.Diagnostics.CodeAnalysis;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;
using Respire.Serialization;

namespace Respire;

public partial interface IRespireSortedSetCommands
{
    /// <summary>Returns a rank range, or null for a missing key. Valkey 9.2+: ZRANGE XX.</summary>
    ValueTask<string[]?> RangeIfExistsAsync(RespireKey key, long start = 0, long stop = -1, bool descending = false, CancellationToken cancellationToken = default);
    /// <summary>Returns owned deserialized members, or null for a missing key. Valkey 9.2+: ZRANGE XX.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<T[]?> RangeIfExistsAsync<T>(RespireKey key, long start = 0, long stop = -1, bool descending = false, CancellationToken cancellationToken = default);
    /// <summary>Returns members and scores, or null for a missing key. Valkey 9.2+: ZRANGE XX WITHSCORES.</summary>
    ValueTask<RespireSortedSetEntry[]?> RangeIfExistsWithScoresAsync(RespireKey key, long start = 0, long stop = -1, bool descending = false, CancellationToken cancellationToken = default);
    /// <summary>Returns owned deserialized members and scores, or null for a missing key. Valkey 9.2+.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<RespireSortedSetEntry<T>[]?> RangeIfExistsWithScoresAsync<T>(RespireKey key, long start = 0, long stop = -1, bool descending = false, CancellationToken cancellationToken = default);
}

internal sealed partial class SortedSetCommands
{
    public ValueTask<string[]?> RangeIfExistsAsync(RespireKey key, long start = 0, long stop = -1, bool descending = false, CancellationToken cancellationToken = default)
        => client.ConvertResponseAsync("ZRANGE", ExistenceRangeCommand(client, key, start, stop, descending, false),
            cancellationToken, this, static (SortedSetCommands _, in RespValue reply) => reply.IsNull ? null : ResponseReader.StringArray(in reply));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T[]?> RangeIfExistsAsync<T>(RespireKey key, long start = 0, long stop = -1, bool descending = false, CancellationToken cancellationToken = default)
        => client.ConvertResponseAsync("ZRANGE", ExistenceRangeCommand(client, key, start, stop, descending, false),
            cancellationToken, client, static (RespireClient state, in RespValue reply) => reply.IsNull ? null : state.DeserializeArray<T>(in reply));

    public ValueTask<RespireSortedSetEntry[]?> RangeIfExistsWithScoresAsync(RespireKey key, long start = 0, long stop = -1, bool descending = false, CancellationToken cancellationToken = default)
        => client.ConvertResponseAsync("ZRANGE", ExistenceRangeCommand(client, key, start, stop, descending, true),
            cancellationToken, this, static (SortedSetCommands _, in RespValue reply) => reply.IsNull ? null : ParseEntries(in reply));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<RespireSortedSetEntry<T>[]?> RangeIfExistsWithScoresAsync<T>(RespireKey key, long start = 0, long stop = -1, bool descending = false, CancellationToken cancellationToken = default)
        => client.ConvertResponseAsync("ZRANGE", ExistenceRangeCommand(client, key, start, stop, descending, true),
            cancellationToken, client, static (RespireClient state, in RespValue reply) => reply.IsNull ? null : ParseEntries<T>(state, in reply));

    internal static Cmd1N ExistenceRangeCommand(RespireClient client, RespireKey key, long start, long stop, bool descending, bool withScores)
    {
        var arguments = new RespireValue[3 + (descending ? 1 : 0) + (withScores ? 1 : 0)];
        arguments[0] = start;
        arguments[1] = stop;
        var index = 2;
        if (descending) arguments[index++] = "REV";
        if (withScores) arguments[index++] = "WITHSCORES";
        arguments[index] = "XX";
        return new(Verbs.ZRange, client.Key(in key), arguments);
    }
}
