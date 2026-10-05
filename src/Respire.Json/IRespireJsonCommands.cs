namespace Respire.Json;

/// <summary>Source-generated command methods for the supported RedisJSON command set.</summary>
/// <remarks>
/// Every method sends exactly the arguments it declares. Commands whose modifiers are fixed tokens, such as
/// <c>JSON.SET ... NX</c> and <c>JSON.DEBUG MEMORY</c>, are exposed only through the typed
/// <see cref="RespireJsonClient"/> methods so that invalid modifiers cannot be sent.
/// </remarks>
[RespireCommands]
public interface IRespireJsonCommands
{
    /// <summary>JSON.MERGE applies an RFC 7396 merge patch.</summary>
    [RespireCommand("JSON.MERGE", Mutation = RespireCacheMutation.SingleKey)]
    ValueTask<RespireResult> MergeAsync(RespireKey key, string path, RespireValue json,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.ARRLEN returns a legacy length or JSONPath lengths, including null for non-arrays.</summary>
    [RespireCommand("JSON.ARRLEN", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<RespireResult> ArrayLengthAsync(RespireKey key, string path,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.NUMPOWBY raises matching numbers to a power.</summary>
    [RespireCommand("JSON.NUMPOWBY", Mutation = RespireCacheMutation.SingleKey)]
    ValueTask<RespireResult> NumberPowerByAsync(RespireKey key, string path, double exponent,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.GET with formatting options and one or more paths.</summary>
    [RespireCommand("JSON.GET", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<RespireResult> GetAsync(RespireKey key, string[] optionsAndPaths,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.GET for one path.</summary>
    [RespireCommand("JSON.GET", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<RespireResult> GetPathAsync(RespireKey key, string path,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.SET without a conditional modifier. <paramref name="json"/> may be UTF-8 bytes or text.</summary>
    [RespireCommand("JSON.SET", Mutation = RespireCacheMutation.SingleKey)]
    ValueTask<RespireResult> SetAsync(RespireKey key, string path, RespireValue json,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.MGET for keys sharing a Redis Cluster slot.</summary>
    [RespireCommand("JSON.MGET", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<RespireResult> MultiGetAsync(RespireKey[] keys, string path,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.MSET with flattened key, path, JSON triples.</summary>
    /// <remarks>
    /// The generator cannot express typed triples, so keys travel as values. The core key-layout table treats
    /// every third argument as a key for prefixing and Cluster slot validation.
    /// </remarks>
    [RespireCommand("JSON.MSET", Mutation = RespireCacheMutation.MultiKey)]
    ValueTask<RespireResult> MultiSetAsync(RespireValue[] keyPathJsonTriples,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.DEL.</summary>
    [RespireCommand("JSON.DEL", Mutation = RespireCacheMutation.SingleKey)]
    ValueTask<RespireResult> DeleteAsync(RespireKey key, string path,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.FORGET.</summary>
    [RespireCommand("JSON.FORGET", Mutation = RespireCacheMutation.SingleKey)]
    ValueTask<RespireResult> ForgetAsync(RespireKey key, string path,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.CLEAR.</summary>
    [RespireCommand("JSON.CLEAR", Mutation = RespireCacheMutation.SingleKey)]
    ValueTask<RespireResult> ClearAsync(RespireKey key, string path,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.ARRAPPEND.</summary>
    [RespireCommand("JSON.ARRAPPEND", Mutation = RespireCacheMutation.SingleKey)]
    ValueTask<RespireResult> ArrayAppendAsync(RespireKey key, string path, string[] jsonValues,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.ARRINDEX.</summary>
    [RespireCommand("JSON.ARRINDEX", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<RespireResult> ArrayIndexAsync(RespireKey key, string path, string jsonValue, long start, long stop,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.ARRINSERT.</summary>
    [RespireCommand("JSON.ARRINSERT", Mutation = RespireCacheMutation.SingleKey)]
    ValueTask<RespireResult> ArrayInsertAsync(RespireKey key, string path, long index, string[] jsonValues,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.ARRPOP.</summary>
    [RespireCommand("JSON.ARRPOP", Mutation = RespireCacheMutation.SingleKey)]
    ValueTask<RespireResult> ArrayPopAsync(RespireKey key, string path, long index,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.ARRTRIM.</summary>
    [RespireCommand("JSON.ARRTRIM", Mutation = RespireCacheMutation.SingleKey)]
    ValueTask<RespireResult> ArrayTrimAsync(RespireKey key, string path, long start, long stop,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.NUMINCRBY.</summary>
    [RespireCommand("JSON.NUMINCRBY", Mutation = RespireCacheMutation.SingleKey)]
    ValueTask<RespireResult> NumberIncrementByAsync(RespireKey key, string path, double increment,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.NUMMULTBY.</summary>
    [RespireCommand("JSON.NUMMULTBY", Mutation = RespireCacheMutation.SingleKey)]
    ValueTask<RespireResult> NumberMultiplyByAsync(RespireKey key, string path, double multiplier,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.OBJKEYS.</summary>
    [RespireCommand("JSON.OBJKEYS", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<RespireResult> ObjectKeysAsync(RespireKey key, string path,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.OBJLEN.</summary>
    [RespireCommand("JSON.OBJLEN", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<RespireResult> ObjectLengthAsync(RespireKey key, string path,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.STRAPPEND.</summary>
    [RespireCommand("JSON.STRAPPEND", Mutation = RespireCacheMutation.SingleKey)]
    ValueTask<RespireResult> StringAppendAsync(RespireKey key, string path, string jsonString,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.STRLEN.</summary>
    [RespireCommand("JSON.STRLEN", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<RespireResult> StringLengthAsync(RespireKey key, string path,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.TOGGLE.</summary>
    [RespireCommand("JSON.TOGGLE", Mutation = RespireCacheMutation.SingleKey)]
    ValueTask<RespireResult> ToggleAsync(RespireKey key, string path,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.TYPE.</summary>
    [RespireCommand("JSON.TYPE", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<RespireResult> TypeAsync(RespireKey key, string path,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.RESP.</summary>
    [RespireCommand("JSON.RESP", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<RespireResult> ResponseAsync(RespireKey key, string path,
        CancellationToken cancellationToken = default);
}

/// <summary>Generated commands whose fixed modifier tokens are supplied only by <see cref="RespireJsonClient"/>.</summary>
[RespireCommands]
internal interface IRespireJsonModifierCommands
{
    /// <summary>JSON.SET with a fixed NX or XX token.</summary>
    [RespireCommand("JSON.SET", Mutation = RespireCacheMutation.SingleKey)]
    ValueTask<RespireResult> SetConditionalAsync(RespireKey key, string path, RespireValue json, string condition,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.DEBUG with the fixed MEMORY subcommand.</summary>
    [RespireCommand("JSON.DEBUG", Mutation = RespireCacheMutation.ReadOnly)]
    ValueTask<RespireResult> DebugAsync(string subcommand, RespireKey key, string path,
        CancellationToken cancellationToken = default);
}
