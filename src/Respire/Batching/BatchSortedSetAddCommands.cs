using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Internal;
using Respire.Serialization;

namespace Respire;

public partial interface IBatchSortedSetCommands
{
    /// <summary>Queues conditional ZADD. True when new, or changed with CH.</summary>
    RespirePending<bool> Add(RespireKey key, RespireSortedSetAddOptions options, RespireValue member, double score);

    /// <summary>Queues conditional ZADD for a serialized member; booleans retain Redis 1/0 encoding. True when new, or changed with CH.</summary>
    /// <remarks>Specify the type argument explicitly to select serialization for types with an implicit RespireValue conversion.</remarks>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    [OverloadResolutionPriority(-1)]
    RespirePending<bool> Add<T>(RespireKey key, RespireSortedSetAddOptions options, T member, double score);

    /// <summary>Queues conditional ZADD for one or more members. Counts new members, or new/changed members with CH.</summary>
    /// <remarks>Entry tuples are snapshotted. Byte-backed members borrow storage until execution or commit completes.</remarks>
    RespirePending<long> Add(RespireKey key, RespireSortedSetAddOptions options,
        params ReadOnlySpan<(RespireValue Member, double Score)> entries);

    /// <summary>Queues single-member ZADD INCR. Returns the resulting score, or null when rejected by NX/XX/GT/LT.</summary>
    RespirePending<double?> Increment(RespireKey key, RespireSortedSetAddOptions options, RespireValue member, double by);
}

internal sealed partial class BatchSortedSetCommands
{
    public RespirePending<bool> Add(RespireKey key, RespireSortedSetAddOptions options, RespireValue member, double score)
        => sink.Add<SortedSetAddCommand, bool>("ZADD", SortedSetCommands.AddCommand(sink.Client, key, options, member, score),
            static (c, v) => ResponseReader.Flag(in v));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    [OverloadResolutionPriority(-1)]
    public RespirePending<bool> Add<T>(RespireKey key, RespireSortedSetAddOptions options, T member, double score)
    {
        SortedSetAddCommand.Validate(options);
        return Add(key, options, sink.Client.SerializeCollectionMember(member), score);
    }

    public RespirePending<long> Add(RespireKey key, RespireSortedSetAddOptions options,
        params ReadOnlySpan<(RespireValue Member, double Score)> entries)
        => sink.Add<SortedSetAddCommand, long>("ZADD", SortedSetCommands.AddCommand(sink.Client, key, options, entries),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<double?> Increment(RespireKey key, RespireSortedSetAddOptions options, RespireValue member, double by)
        => sink.Add<SortedSetAddCommand, double?>("ZADD", SortedSetCommands.AddCommand(sink.Client, key, options, member, by, increment: true),
            static (c, v) => ResponseReader.DoubleOrNull(in v));
}
