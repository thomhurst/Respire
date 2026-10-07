using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;
using Respire.Serialization;

namespace Respire;

/// <summary>Redis vector-set commands. Requires a server with the requested vector-set command.</summary>
/// <remarks>Input memory is borrowed until completion. Replies own their storage. Attributes must be JSON,
/// including when produced by a custom serializer. No fallback or automatic conversion of existing data is performed.</remarks>
public interface IVectorSetCommands
{
    /// <summary>VADD: adds or replaces a vector; true means a new member was added.</summary>
    ValueTask<bool> AddAsync(RespireKey key, ReadOnlyMemory<float> vector, RespireValue member, RespireVectorAddOptions options = default, RespireVectorEncoding encoding = default, CancellationToken cancellationToken = default);
    /// <summary>VSIM: searches by vector, preserving server result order.</summary>
    ValueTask<RespireVectorMatch[]> SearchAsync(RespireKey key, ReadOnlyMemory<float> vector, RespireVectorSearchOptions options = default, RespireVectorEncoding encoding = default, CancellationToken cancellationToken = default);
    /// <summary>VSIM ELE: searches using an existing member's vector.</summary>
    ValueTask<RespireVectorMatch[]> SearchByMemberAsync(RespireKey key, RespireValue member, RespireVectorSearchOptions options = default, CancellationToken cancellationToken = default);
    /// <summary>VREM: removes one member, returning whether it existed.</summary>
    ValueTask<bool> RemoveAsync(RespireKey key, RespireValue member, CancellationToken cancellationToken = default);
    /// <summary>VCARD: returns the number of members, or zero for a missing key.</summary>
    ValueTask<long> CountAsync(RespireKey key, CancellationToken cancellationToken = default);
    /// <summary>VDIM: returns stored dimensions; a missing key is a server error.</summary>
    ValueTask<long> DimensionsAsync(RespireKey key, CancellationToken cancellationToken = default);
    /// <summary>VEMB: returns the normalized, reconstructed vector, or null for a missing member.</summary>
    ValueTask<float[]?> EmbeddingAsync(RespireKey key, RespireValue member, CancellationToken cancellationToken = default);
    /// <summary>VISMEMBER: returns whether the member exists.</summary>
    ValueTask<bool> ContainsAsync(RespireKey key, RespireValue member, CancellationToken cancellationToken = default);
    /// <summary>VGETATTR: returns owned JSON bytes, or null for absent attributes/member.</summary>
    ValueTask<byte[]?> GetAttributesJsonAsync(RespireKey key, RespireValue member, CancellationToken cancellationToken = default);
    /// <summary>VSETATTR: sets raw JSON, or removes attributes with an empty string; false means missing member.</summary>
    ValueTask<bool> SetAttributesJsonAsync(RespireKey key, RespireValue member, RespireValue json, CancellationToken cancellationToken = default);
    /// <summary>VGETATTR: deserializes JSON through the configured serializer, or returns default when absent.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<T?> GetAttributesAsync<T>(RespireKey key, RespireValue member, CancellationToken cancellationToken = default);
    /// <summary>VSETATTR: serializes attributes through the configured serializer, which must produce server-valid JSON.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<bool> SetAttributesAsync<T>(RespireKey key, RespireValue member, T attributes, CancellationToken cancellationToken = default);
    /// <summary>VINFO: returns owned metadata, or null for a missing key.</summary>
    ValueTask<RespireVectorSetInfo?> InfoAsync(RespireKey key, CancellationToken cancellationToken = default);
    /// <summary>VLINKS: returns neighbors per graph level, highest level first; null means missing member.</summary>
    ValueTask<RespireVectorMatch[][]?> LinksAsync(RespireKey key, RespireValue member, bool includeScores = false, CancellationToken cancellationToken = default);
    /// <summary>VRANDMEMBER: returns one owned binary member, or null for a missing key.</summary>
    ValueTask<byte[]?> RandomMemberAsync(RespireKey key, CancellationToken cancellationToken = default);
    /// <summary>VRANDMEMBER count: positive counts request distinct members; negative counts permit duplicates.</summary>
    ValueTask<byte[][]> RandomMembersAsync(RespireKey key, long count, CancellationToken cancellationToken = default);
    /// <summary>VRANGE: returns owned members within binary-safe wire bounds: -, +, [inclusive, or (exclusive.</summary>
    /// <remarks>Only the key is prefixed. Null count is unlimited; zero returns none; negative counts are server-defined.</remarks>
    ValueTask<byte[][]> RangeAsync(RespireKey key, RespireValue start, RespireValue end, long? count = null, CancellationToken cancellationToken = default);
}

internal sealed class VectorSetCommands(RespireClient client) : IVectorSetCommands
{
    public ValueTask<bool> AddAsync(RespireKey key, ReadOnlyMemory<float> vector, RespireValue member,
        RespireVectorAddOptions options = default, RespireVectorEncoding encoding = default, CancellationToken cancellationToken = default)
        => Convert("VADD", BuildAdd(client, key, vector, member, options, encoding), cancellationToken,
            static (VectorSetCommands _, in RespValue value) => VectorSetParser.Flag(in value));

    public ValueTask<RespireVectorMatch[]> SearchAsync(RespireKey key, ReadOnlyMemory<float> vector,
        RespireVectorSearchOptions options = default, RespireVectorEncoding encoding = default, CancellationToken cancellationToken = default)
        => Convert("VSIM", BuildSearch(client, key, vector, options, encoding), cancellationToken,
            options, static (RespireVectorSearchOptions state, in RespValue value) => VectorSetParser.Matches(in value, state));

    public ValueTask<RespireVectorMatch[]> SearchByMemberAsync(RespireKey key, RespireValue member,
        RespireVectorSearchOptions options = default, CancellationToken cancellationToken = default)
        => Convert("VSIM", BuildSearchByMember(client, key, member, options), cancellationToken,
            options, static (RespireVectorSearchOptions state, in RespValue value) => VectorSetParser.Matches(in value, state));

    public ValueTask<bool> RemoveAsync(RespireKey key, RespireValue member, CancellationToken cancellationToken = default)
        => Convert("VREM", Build(client, RespireCommands.VectorSet.VREM.Verb, key, member), cancellationToken,
            static (VectorSetCommands _, in RespValue value) => VectorSetParser.Flag(in value));
    public ValueTask<long> CountAsync(RespireKey key, CancellationToken cancellationToken = default)
        => Convert("VCARD", Build(client, RespireCommands.VectorSet.VCARD.Verb, key), cancellationToken,
            static (VectorSetCommands _, in RespValue value) => VectorSetParser.Number(in value));
    public ValueTask<long> DimensionsAsync(RespireKey key, CancellationToken cancellationToken = default)
        => Convert("VDIM", Build(client, RespireCommands.VectorSet.VDIM.Verb, key), cancellationToken,
            static (VectorSetCommands _, in RespValue value) => VectorSetParser.Number(in value));
    public ValueTask<float[]?> EmbeddingAsync(RespireKey key, RespireValue member, CancellationToken cancellationToken = default)
        => Convert("VEMB", Build(client, RespireCommands.VectorSet.VEMB.Verb, key, member), cancellationToken,
            static (VectorSetCommands _, in RespValue value) => VectorSetParser.Embedding(in value));
    public ValueTask<bool> ContainsAsync(RespireKey key, RespireValue member, CancellationToken cancellationToken = default)
        => Convert("VISMEMBER", Build(client, RespireCommands.VectorSet.VISMEMBER.Verb, key, member), cancellationToken,
            static (VectorSetCommands _, in RespValue value) => VectorSetParser.Flag(in value));
    public ValueTask<byte[]?> GetAttributesJsonAsync(RespireKey key, RespireValue member, CancellationToken cancellationToken = default)
        => Convert("VGETATTR", Build(client, RespireCommands.VectorSet.VGETATTR.Verb, key, member), cancellationToken,
            static (VectorSetCommands _, in RespValue value) => VectorSetParser.NullableBytes(in value));
    public ValueTask<bool> SetAttributesJsonAsync(RespireKey key, RespireValue member, RespireValue json, CancellationToken cancellationToken = default)
        => Convert("VSETATTR", Build(client, RespireCommands.VectorSet.VSETATTR.Verb, key, member, json), cancellationToken,
            static (VectorSetCommands _, in RespValue value) => VectorSetParser.Flag(in value));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T?> GetAttributesAsync<T>(RespireKey key, RespireValue member, CancellationToken cancellationToken = default)
        => Convert("VGETATTR", Build(client, RespireCommands.VectorSet.VGETATTR.Verb, key, member), cancellationToken,
            client, static (RespireClient state, in RespValue value) => DeserializeAttributes<T>(state, in value));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<bool> SetAttributesAsync<T>(RespireKey key, RespireValue member, T attributes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return SetAttributesJsonAsync(key, member, SerializeAttributes(client, attributes), cancellationToken);
    }

    public ValueTask<RespireVectorSetInfo?> InfoAsync(RespireKey key, CancellationToken cancellationToken = default)
        => Convert("VINFO", Build(client, RespireCommands.VectorSet.VINFO.Verb, key), cancellationToken,
            static (VectorSetCommands _, in RespValue value) => VectorSetParser.Info(in value));
    public ValueTask<RespireVectorMatch[][]?> LinksAsync(RespireKey key, RespireValue member, bool includeScores = false, CancellationToken cancellationToken = default)
        => Convert("VLINKS", BuildLinks(client, key, member, includeScores), cancellationToken,
            includeScores, static (bool scores, in RespValue value) => VectorSetParser.Links(in value, scores));
    public ValueTask<byte[]?> RandomMemberAsync(RespireKey key, CancellationToken cancellationToken = default)
        => Convert("VRANDMEMBER", Build(client, RespireCommands.VectorSet.VRANDMEMBER.Verb, key), cancellationToken,
            static (VectorSetCommands _, in RespValue value) => VectorSetParser.NullableBytes(in value));
    public ValueTask<byte[][]> RandomMembersAsync(RespireKey key, long count, CancellationToken cancellationToken = default)
        => Convert("VRANDMEMBER", BuildRandom(client, key, count), cancellationToken,
            static (VectorSetCommands _, in RespValue value) => VectorSetParser.Members(in value));
    public ValueTask<byte[][]> RangeAsync(RespireKey key, RespireValue start, RespireValue end, long? count = null, CancellationToken cancellationToken = default)
        => Convert("VRANGE", BuildRange(client, key, start, end, count), cancellationToken,
            static (VectorSetCommands _, in RespValue value) => VectorSetParser.Members(in value));

    private ValueTask<T> Convert<TCommand, T>(string operation, TCommand command, CancellationToken cancellationToken,
        ResponseConverter<VectorSetCommands, T> convert) where TCommand : struct, IRespCommand
        => Convert(operation, command, cancellationToken, this, convert);

    private ValueTask<T> Convert<TCommand, TState, T>(string operation, TCommand command, CancellationToken cancellationToken,
        TState state, ResponseConverter<TState, T> convert) where TCommand : struct, IRespCommand
    {
        cancellationToken.ThrowIfCancellationRequested();
        return client.ConvertResponseAsync(operation, command, cancellationToken, state, convert);
    }

    internal static VectorCommand BuildAdd(RespireClient client, RespireKey key, ReadOnlyMemory<float> vector,
        RespireValue member, RespireVectorAddOptions options, RespireVectorEncoding encoding)
    {
        ValidateVector(vector.Span, encoding);
        RespireValue.ThrowIfNull(member, nameof(member));
        if (options.ReduceDimensions is <= 0) throw new ArgumentOutOfRangeException(nameof(options), "ReduceDimensions must be positive.");
        ValidateEffort(options.ExplorationFactor, nameof(options));
        if (options.Links is < 4 or > 4096) throw new ArgumentOutOfRangeException(nameof(options), "Links must be between 4 and 4096.");
        if (!Enum.IsDefined(options.Quantization)) throw new ArgumentOutOfRangeException(nameof(options), "Quantization must be a supported value.");
        RespireValue[] before = options.ReduceDimensions is { } dimensions ? ["REDUCE", dimensions] : [];
        List<RespireValue> after = [member];
        if (options.CheckAndSet) after.Add("CAS");
        if (options.Quantization != RespireVectorQuantization.Default)
            after.Add(options.Quantization switch { RespireVectorQuantization.None => "NOQUANT", RespireVectorQuantization.Q8 => "Q8", _ => "BIN" });
        if (options.ExplorationFactor is { } effort) { after.Add("EF"); after.Add(effort); }
        if (!options.AttributesJson.IsNull) { after.Add("SETATTR"); after.Add(options.AttributesJson); }
        if (options.Links is { } links) { after.Add("M"); after.Add(links); }
        return new(RespireCommands.VectorSet.VADD.Verb, client.Key(in key), vector, encoding, before, after.ToArray());
    }

    internal static VectorCommand BuildSearch(RespireClient client, RespireKey key, ReadOnlyMemory<float> vector,
        RespireVectorSearchOptions options, RespireVectorEncoding encoding)
    {
        ValidateVector(vector.Span, encoding);
        return new(RespireCommands.VectorSet.VSIM.Verb, client.Key(in key), vector, encoding, [], SearchArguments(options));
    }

    internal static Cmd1N BuildSearchByMember(RespireClient client, RespireKey key, RespireValue member, RespireVectorSearchOptions options)
    {
        RespireValue.ThrowIfNull(member, nameof(member));
        return Build(client, RespireCommands.VectorSet.VSIM.Verb, key, ["ELE", member, .. SearchArguments(options)]);
    }

    private static RespireValue[] SearchArguments(RespireVectorSearchOptions options)
    {
        if (options.Count is <= 0) throw new ArgumentOutOfRangeException(nameof(options), "Count must be positive.");
        ValidateEffort(options.ExplorationFactor, nameof(options));
        if (options.FilterExplorationFactor is <= 0) throw new ArgumentOutOfRangeException(nameof(options), "FilterExplorationFactor must be positive.");
        if (options.Epsilon is { } epsilon && (!double.IsFinite(epsilon) || epsilon is < 0 or > 1))
            throw new ArgumentOutOfRangeException(nameof(options), "Epsilon must be finite and between 0 and 1.");
        List<RespireValue> args = [];
        if (options.IncludeScores) args.Add(CommandOptionFrames.WITHSCORESValue);
        if (options.IncludeAttributes) args.Add("WITHATTRIBS");
        if (options.Count is { } count) { args.Add("COUNT"); args.Add(count); }
        if (options.Epsilon is { } distance) { args.Add("EPSILON"); args.Add(distance); }
        if (options.ExplorationFactor is { } effort) { args.Add("EF"); args.Add(effort); }
        if (options.Filter is { } filter) { args.Add("FILTER"); args.Add(filter); }
        if (options.FilterExplorationFactor is { } filterEffort) { args.Add("FILTER-EF"); args.Add(filterEffort); }
        if (options.Exact) args.Add("TRUTH");
        if (options.NoThread) args.Add("NOTHREAD");
        return args.ToArray();
    }

    private static void ValidateVector(ReadOnlySpan<float> vector, RespireVectorEncoding encoding)
    {
        if (!Enum.IsDefined(encoding)) throw new ArgumentOutOfRangeException(nameof(encoding));
        if (vector.IsEmpty || vector.Length > (int.MaxValue - 2) / sizeof(float))
            throw new ArgumentOutOfRangeException(nameof(vector), "Vector must contain a representable, nonzero number of components.");
        foreach (var component in vector)
            if (!float.IsFinite(component)) throw new ArgumentException("Vector components must be finite.", nameof(vector));
    }

    private static void ValidateEffort(int? effort, string parameterName)
    {
        if (effort is <= 0 or > 1_000_000)
            throw new ArgumentOutOfRangeException(parameterName, "ExplorationFactor must be between 1 and 1000000.");
    }

    internal static Cmd1N Build(RespireClient client, Verb verb, RespireKey key, params RespireValue[] arguments)
    {
        foreach (var argument in arguments) RespireValue.ThrowIfNull(argument, nameof(arguments));
        return new(verb, client.Key(in key), arguments);
    }

    internal static Cmd1N BuildLinks(RespireClient client, RespireKey key, RespireValue member, bool scores)
        => Build(client, RespireCommands.VectorSet.VLINKS.Verb, key, scores ? [member, CommandOptionFrames.WITHSCORESValue] : [member]);
    internal static Cmd1N BuildRandom(RespireClient client, RespireKey key, long count)
    {
        if (count == long.MinValue) throw new ArgumentOutOfRangeException(nameof(count));
        return Build(client, RespireCommands.VectorSet.VRANDMEMBER.Verb, key, count);
    }
    internal static Cmd1N BuildRange(RespireClient client, RespireKey key, RespireValue start, RespireValue end, long? count)
        => Build(client, RespireCommands.VectorSet.VRANGE.Verb, key, count is { } limit ? [start, end, limit] : [start, end]);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    internal static RespireValue SerializeAttributes<T>(RespireClient client, T attributes)
    {
        var buffer = new ArrayBufferWriter<byte>();
        client.Core.Options.Serializer.Serialize(buffer, attributes);
        return buffer.WrittenMemory;
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    internal static T? DeserializeAttributes<T>(RespireClient client, in RespValue value)
    {
        if (value.IsNull) return default;
        VectorSetParser.RequireString(in value);
        return client.Core.Options.Serializer.Deserialize<T>(value.AsSpan());
    }
}
