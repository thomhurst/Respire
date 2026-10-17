using System.Diagnostics.CodeAnalysis;
using Respire.Commands;
using Respire.Internal;
using Respire.Serialization;

namespace Respire;

public partial interface IRespireBatchSortedSetCommands
{
    /// <summary>Queues ZRANGE XX; null means a missing key. Requires Valkey 9.2+.</summary>
    RespirePending<string[]?> RangeIfExists(RespireKey key, long start = 0, long stop = -1, bool descending = false);
    /// <summary>Queues ZRANGE XX and deserializes members; null means a missing key. Requires Valkey 9.2+.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    RespirePending<T[]?> RangeIfExists<T>(RespireKey key, long start = 0, long stop = -1, bool descending = false);
    /// <summary>Queues ZRANGE XX WITHSCORES; null means a missing key. Requires Valkey 9.2+.</summary>
    RespirePending<RespireSortedSetEntry[]?> RangeIfExistsWithScores(RespireKey key, long start = 0, long stop = -1, bool descending = false);
    /// <summary>Queues ZRANGE XX WITHSCORES and deserializes members; null means a missing key. Requires Valkey 9.2+.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    RespirePending<RespireSortedSetEntry<T>[]?> RangeIfExistsWithScores<T>(RespireKey key, long start = 0, long stop = -1, bool descending = false);
}

internal sealed partial class BatchSortedSetCommands
{
    public RespirePending<string[]?> RangeIfExists(RespireKey key, long start = 0, long stop = -1, bool descending = false)
        => sink.Add<Cmd1N, string[]?>("ZRANGE", SortedSetCommands.ExistenceRangeCommand(sink.Client, key, start, stop, descending, false),
            static (_, reply) => reply.IsNull ? null : ResponseReader.StringArray(in reply));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public RespirePending<T[]?> RangeIfExists<T>(RespireKey key, long start = 0, long stop = -1, bool descending = false)
        => sink.Add<Cmd1N, T[]?>("ZRANGE", SortedSetCommands.ExistenceRangeCommand(sink.Client, key, start, stop, descending, false),
            static (client, reply) => reply.IsNull ? null : client.DeserializeArray<T>(in reply));

    public RespirePending<RespireSortedSetEntry[]?> RangeIfExistsWithScores(RespireKey key, long start = 0, long stop = -1, bool descending = false)
        => sink.Add<Cmd1N, RespireSortedSetEntry[]?>("ZRANGE", SortedSetCommands.ExistenceRangeCommand(sink.Client, key, start, stop, descending, true),
            static (_, reply) => reply.IsNull ? null : SortedSetCommands.ParseEntries(in reply));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public RespirePending<RespireSortedSetEntry<T>[]?> RangeIfExistsWithScores<T>(RespireKey key, long start = 0, long stop = -1, bool descending = false)
        => sink.Add<Cmd1N, RespireSortedSetEntry<T>[]?>("ZRANGE", SortedSetCommands.ExistenceRangeCommand(sink.Client, key, start, stop, descending, true),
            static (client, reply) => reply.IsNull ? null : SortedSetCommands.ParseEntries<T>(client, in reply));
}
