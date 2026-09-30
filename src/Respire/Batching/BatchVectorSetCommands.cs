using System.Diagnostics.CodeAnalysis;
using Respire.Commands;
using Respire.Protocol;
using Respire.Serialization;

namespace Respire;

/// <summary>Vector-set commands queued in a batch or transaction. Inputs are snapshotted at enqueue time.</summary>
public interface IBatchVectorSetCommands
{
    /// <summary>VADD: adds or replaces a vector; true means a new member was added.</summary>
    RespirePending<bool> Add(RespireKey key, ReadOnlyMemory<float> vector, RespireValue member, RespireVectorAddOptions options = default, RespireVectorEncoding encoding = default);
    /// <summary>VSIM: searches by vector, preserving server result order.</summary>
    RespirePending<RespireVectorMatch[]> Search(RespireKey key, ReadOnlyMemory<float> vector, RespireVectorSearchOptions options = default, RespireVectorEncoding encoding = default);
    /// <summary>VSIM ELE: searches using an existing member's vector.</summary>
    RespirePending<RespireVectorMatch[]> SearchByMember(RespireKey key, RespireValue member, RespireVectorSearchOptions options = default);
    /// <summary>VREM: removes one member, returning whether it existed.</summary>
    RespirePending<bool> Remove(RespireKey key, RespireValue member);
    /// <summary>VCARD: returns the number of members, or zero for a missing key.</summary>
    RespirePending<long> Count(RespireKey key);
    /// <summary>VDIM: returns stored dimensions; a missing key is a server error.</summary>
    RespirePending<long> Dimensions(RespireKey key);
    /// <summary>VEMB: returns the normalized, reconstructed vector, or null for a missing member.</summary>
    RespirePending<float[]?> Embedding(RespireKey key, RespireValue member);
    /// <summary>VISMEMBER: returns whether the member exists.</summary>
    RespirePending<bool> Contains(RespireKey key, RespireValue member);
    /// <summary>VGETATTR: returns owned JSON bytes, or null for absent attributes/member.</summary>
    RespirePending<byte[]?> GetAttributesJson(RespireKey key, RespireValue member);
    /// <summary>VSETATTR: sets raw JSON, or removes attributes with an empty string; false means missing member.</summary>
    RespirePending<bool> SetAttributesJson(RespireKey key, RespireValue member, RespireValue json);
    /// <summary>VGETATTR: deserializes JSON through the configured serializer, or returns default when absent.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    RespirePending<T?> GetAttributes<T>(RespireKey key, RespireValue member);
    /// <summary>VSETATTR: serializes attributes through the configured serializer, which must produce server-valid JSON.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    RespirePending<bool> SetAttributes<T>(RespireKey key, RespireValue member, T attributes);
    /// <summary>VINFO: returns owned metadata, or null for a missing key.</summary>
    RespirePending<RespireVectorSetInfo?> Info(RespireKey key);
    /// <summary>VLINKS: returns neighbors per graph level, highest level first; null means missing member.</summary>
    RespirePending<RespireVectorMatch[][]?> Links(RespireKey key, RespireValue member, bool includeScores = false);
    /// <summary>VRANDMEMBER: returns one owned binary member, or null for a missing key.</summary>
    RespirePending<byte[]?> RandomMember(RespireKey key);
    /// <summary>VRANDMEMBER count: positive counts request distinct members; negative counts permit duplicates.</summary>
    RespirePending<byte[][]> RandomMembers(RespireKey key, long count);
    /// <summary>VRANGE: returns owned members within binary-safe wire bounds: -, +, [inclusive, or (exclusive.</summary>
    /// <remarks>Only the key is prefixed. Null count is unlimited; zero returns none; negative counts are server-defined.</remarks>
    RespirePending<byte[][]> Range(RespireKey key, RespireValue start, RespireValue end, long? count = null);
}

internal sealed class BatchVectorSetCommands(IPendingSink sink) : IBatchVectorSetCommands
{
    public RespirePending<bool> Add(RespireKey key, ReadOnlyMemory<float> vector, RespireValue member,
        RespireVectorAddOptions options = default, RespireVectorEncoding encoding = default)
        => Queue("VADD", VectorSetCommands.BuildAdd(sink.Client, key, vector, member, options, encoding),
            static (_, value) => VectorSetParser.Flag(in value));

    public RespirePending<RespireVectorMatch[]> Search(RespireKey key, ReadOnlyMemory<float> vector,
        RespireVectorSearchOptions options = default, RespireVectorEncoding encoding = default)
        => Queue("VSIM", VectorSetCommands.BuildSearch(sink.Client, key, vector, options, encoding),
            (_, value) => VectorSetParser.Matches(in value, options));

    public RespirePending<RespireVectorMatch[]> SearchByMember(RespireKey key, RespireValue member, RespireVectorSearchOptions options = default)
        => Queue("VSIM", VectorSetCommands.BuildSearchByMember(sink.Client, key, member, options),
            (_, value) => VectorSetParser.Matches(in value, options));

    public RespirePending<bool> Remove(RespireKey key, RespireValue member)
        => Queue("VREM", VectorSetCommands.Build(sink.Client, RespireCommands.VectorSet.VREM.Verb, key, member),
            static (_, value) => VectorSetParser.Flag(in value));
    public RespirePending<long> Count(RespireKey key)
        => Queue("VCARD", VectorSetCommands.Build(sink.Client, RespireCommands.VectorSet.VCARD.Verb, key),
            static (_, value) => VectorSetParser.Number(in value));
    public RespirePending<long> Dimensions(RespireKey key)
        => Queue("VDIM", VectorSetCommands.Build(sink.Client, RespireCommands.VectorSet.VDIM.Verb, key),
            static (_, value) => VectorSetParser.Number(in value));
    public RespirePending<float[]?> Embedding(RespireKey key, RespireValue member)
        => Queue("VEMB", VectorSetCommands.Build(sink.Client, RespireCommands.VectorSet.VEMB.Verb, key, member),
            static (_, value) => VectorSetParser.Embedding(in value));
    public RespirePending<bool> Contains(RespireKey key, RespireValue member)
        => Queue("VISMEMBER", VectorSetCommands.Build(sink.Client, RespireCommands.VectorSet.VISMEMBER.Verb, key, member),
            static (_, value) => VectorSetParser.Flag(in value));
    public RespirePending<byte[]?> GetAttributesJson(RespireKey key, RespireValue member)
        => Queue("VGETATTR", VectorSetCommands.Build(sink.Client, RespireCommands.VectorSet.VGETATTR.Verb, key, member),
            static (_, value) => VectorSetParser.NullableBytes(in value));
    public RespirePending<bool> SetAttributesJson(RespireKey key, RespireValue member, RespireValue json)
        => Queue("VSETATTR", VectorSetCommands.Build(sink.Client, RespireCommands.VectorSet.VSETATTR.Verb, key, member, json),
            static (_, value) => VectorSetParser.Flag(in value));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public RespirePending<T?> GetAttributes<T>(RespireKey key, RespireValue member)
        => Queue("VGETATTR", VectorSetCommands.Build(sink.Client, RespireCommands.VectorSet.VGETATTR.Verb, key, member),
            static (client, value) => VectorSetCommands.DeserializeAttributes<T>(client, in value));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public RespirePending<bool> SetAttributes<T>(RespireKey key, RespireValue member, T attributes)
        => SetAttributesJson(key, member, VectorSetCommands.SerializeAttributes(sink.Client, attributes));

    public RespirePending<RespireVectorSetInfo?> Info(RespireKey key)
        => Queue("VINFO", VectorSetCommands.Build(sink.Client, RespireCommands.VectorSet.VINFO.Verb, key),
            static (_, value) => VectorSetParser.Info(in value));
    public RespirePending<RespireVectorMatch[][]?> Links(RespireKey key, RespireValue member, bool includeScores = false)
        => Queue("VLINKS", VectorSetCommands.BuildLinks(sink.Client, key, member, includeScores),
            (_, value) => VectorSetParser.Links(in value, includeScores));
    public RespirePending<byte[]?> RandomMember(RespireKey key)
        => Queue("VRANDMEMBER", VectorSetCommands.Build(sink.Client, RespireCommands.VectorSet.VRANDMEMBER.Verb, key),
            static (_, value) => VectorSetParser.NullableBytes(in value));
    public RespirePending<byte[][]> RandomMembers(RespireKey key, long count)
        => Queue("VRANDMEMBER", VectorSetCommands.BuildRandom(sink.Client, key, count),
            static (_, value) => VectorSetParser.Members(in value));
    public RespirePending<byte[][]> Range(RespireKey key, RespireValue start, RespireValue end, long? count = null)
        => Queue("VRANGE", VectorSetCommands.BuildRange(sink.Client, key, start, end, count),
            static (_, value) => VectorSetParser.Members(in value));

    private RespirePending<T> Queue<TCommand, T>(string operation, TCommand command, Func<RespireClient, RespValue, T> convert)
        where TCommand : struct, IRespCommand
    {
        // Transactions serialize immediately. Batches otherwise retain borrowed member/vector memory.
        if (sink.DefersSerialization)
            return sink.Add(operation, SnapshotCommand.Create(in command), convert);
        return sink.Add(operation, command, convert);
    }
}
