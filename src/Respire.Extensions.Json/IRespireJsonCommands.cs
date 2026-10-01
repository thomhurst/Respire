namespace Respire.Extensions.Json;

/// <summary>Source-generated command methods for the supported RedisJSON command set.</summary>
/// <remarks>
/// Every method sends exactly the arguments it declares. Commands whose modifiers are fixed tokens, such as
/// <c>JSON.SET ... NX</c> and <c>JSON.DEBUG MEMORY</c>, are exposed only through the typed
/// <see cref="RespireJsonClient"/> methods so that invalid modifiers cannot be sent.
/// </remarks>
[RespireCommands]
public interface IRespireJsonCommands
{
    /// <summary>JSON.GET with formatting options and one or more paths.</summary>
    [RespireCommand("JSON.GET")]
    ValueTask<RespireResult> GetAsync(RespireKey key, string[] optionsAndPaths,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.GET for one path.</summary>
    [RespireCommand("JSON.GET")]
    ValueTask<RespireResult> GetPathAsync(RespireKey key, string path,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.SET without a conditional modifier. <paramref name="json"/> may be UTF-8 bytes or text.</summary>
    [RespireCommand("JSON.SET")]
    ValueTask<RespireResult> SetAsync(RespireKey key, string path, RespireValue json,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.MGET for keys sharing a Redis Cluster slot.</summary>
    [RespireCommand("JSON.MGET")]
    ValueTask<RespireResult> MultiGetAsync(RespireKey[] keys, string path,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.MSET with flattened key, path, JSON triples.</summary>
    /// <remarks>
    /// The generator cannot express typed triples, so keys travel as values. The core key-layout table treats
    /// every third argument as a key for prefixing and Cluster slot validation.
    /// </remarks>
    [RespireCommand("JSON.MSET")]
    ValueTask<RespireResult> MultiSetAsync(RespireValue[] keyPathJsonTriples,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.DEL.</summary>
    [RespireCommand("JSON.DEL")]
    ValueTask<RespireResult> DeleteAsync(RespireKey key, string path,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.FORGET.</summary>
    [RespireCommand("JSON.FORGET")]
    ValueTask<RespireResult> ForgetAsync(RespireKey key, string path,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.CLEAR.</summary>
    [RespireCommand("JSON.CLEAR")]
    ValueTask<RespireResult> ClearAsync(RespireKey key, string path,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.ARRAPPEND.</summary>
    [RespireCommand("JSON.ARRAPPEND")]
    ValueTask<RespireResult> ArrayAppendAsync(RespireKey key, string path, string[] jsonValues,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.ARRINDEX.</summary>
    [RespireCommand("JSON.ARRINDEX")]
    ValueTask<RespireResult> ArrayIndexAsync(RespireKey key, string path, string jsonValue, long start, long stop,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.ARRINSERT.</summary>
    [RespireCommand("JSON.ARRINSERT")]
    ValueTask<RespireResult> ArrayInsertAsync(RespireKey key, string path, long index, string[] jsonValues,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.ARRPOP.</summary>
    [RespireCommand("JSON.ARRPOP")]
    ValueTask<RespireResult> ArrayPopAsync(RespireKey key, string path, long index,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.ARRTRIM.</summary>
    [RespireCommand("JSON.ARRTRIM")]
    ValueTask<RespireResult> ArrayTrimAsync(RespireKey key, string path, long start, long stop,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.NUMINCRBY.</summary>
    [RespireCommand("JSON.NUMINCRBY")]
    ValueTask<RespireResult> NumberIncrementByAsync(RespireKey key, string path, double increment,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.NUMMULTBY.</summary>
    [RespireCommand("JSON.NUMMULTBY")]
    ValueTask<RespireResult> NumberMultiplyByAsync(RespireKey key, string path, double multiplier,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.OBJKEYS.</summary>
    [RespireCommand("JSON.OBJKEYS")]
    ValueTask<RespireResult> ObjectKeysAsync(RespireKey key, string path,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.OBJLEN.</summary>
    [RespireCommand("JSON.OBJLEN")]
    ValueTask<RespireResult> ObjectLengthAsync(RespireKey key, string path,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.STRAPPEND.</summary>
    [RespireCommand("JSON.STRAPPEND")]
    ValueTask<RespireResult> StringAppendAsync(RespireKey key, string path, string jsonString,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.STRLEN.</summary>
    [RespireCommand("JSON.STRLEN")]
    ValueTask<RespireResult> StringLengthAsync(RespireKey key, string path,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.TOGGLE.</summary>
    [RespireCommand("JSON.TOGGLE")]
    ValueTask<RespireResult> ToggleAsync(RespireKey key, string path,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.TYPE.</summary>
    [RespireCommand("JSON.TYPE")]
    ValueTask<RespireResult> TypeAsync(RespireKey key, string path,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.RESP.</summary>
    [RespireCommand("JSON.RESP")]
    ValueTask<RespireResult> ResponseAsync(RespireKey key, string path,
        CancellationToken cancellationToken = default);
}

/// <summary>Generated commands whose fixed modifier tokens are supplied only by <see cref="RespireJsonClient"/>.</summary>
[RespireCommands]
internal interface IRespireJsonModifierCommands
{
    /// <summary>JSON.SET with a fixed NX or XX token.</summary>
    [RespireCommand("JSON.SET")]
    ValueTask<RespireResult> SetConditionalAsync(RespireKey key, string path, RespireValue json, string condition,
        CancellationToken cancellationToken = default);

    /// <summary>JSON.DEBUG with the fixed MEMORY subcommand.</summary>
    [RespireCommand("JSON.DEBUG")]
    ValueTask<RespireResult> DebugAsync(string subcommand, RespireKey key, string path,
        CancellationToken cancellationToken = default);
}
