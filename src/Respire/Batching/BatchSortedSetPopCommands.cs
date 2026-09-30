using System.Diagnostics.CodeAnalysis;
using Respire.Commands;
using Respire.Serialization;

namespace Respire;

public partial interface IBatchSortedSetCommands
{
    /// <summary>Pops entries from the first nonempty sorted set, or null if all are empty. Redis: ZMPOP (7.0+).</summary>
    /// <remarks>Count must be positive; keys must share a Cluster slot. Blocking pops have no deferred form.</remarks>
    RespirePending<RespireSortedSetPopManyResult?> PopMany(
        ReadOnlySpan<RespireKey> keys, long count = 1, bool descending = false);

    /// <summary>Pops entries from the first nonempty sorted set, or null if all are empty. Redis: ZMPOP (7.0+).</summary>
    /// <remarks>Count must be positive; keys must share a Cluster slot. Blocking pops have no deferred form.</remarks>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    RespirePending<RespireSortedSetPopManyResult<T>?> PopMany<T>(
        ReadOnlySpan<RespireKey> keys, long count = 1, bool descending = false);

}

internal sealed partial class BatchSortedSetCommands
{
    public RespirePending<RespireSortedSetPopManyResult?> PopMany(
        ReadOnlySpan<RespireKey> keys, long count = 1, bool descending = false)
    {
        var (operation, command) = SortedSetCommands.PopManyCommand(sink.Client, keys, count, descending, waitFor: null);
        return sink.Add<CmdN, RespireSortedSetPopManyResult?>(operation, command, keys,
            static (c, reply) => SortedSetCommands.ParsePopMany(in reply, c.KeyPrefixBytes));
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public RespirePending<RespireSortedSetPopManyResult<T>?> PopMany<T>(
        ReadOnlySpan<RespireKey> keys, long count = 1, bool descending = false)
    {
        var (operation, command) = SortedSetCommands.PopManyCommand(sink.Client, keys, count, descending, waitFor: null);
        return sink.Add<CmdN, RespireSortedSetPopManyResult<T>?>(operation, command, keys,
            static (c, reply) => SortedSetCommands.ParsePopMany<T>(c, in reply));
    }

}
