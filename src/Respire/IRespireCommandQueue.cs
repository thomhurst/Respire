using System.Diagnostics.CodeAnalysis;
using Respire.Serialization;

namespace Respire;

/// <summary>
/// The commands that can be queued by both a <see cref="RespireBatch"/> and a
/// <see cref="RespireTransaction"/> or <see cref="RespireWatchedTransaction"/>.
/// </summary>
/// <remarks>
/// Use this interface when helper code should queue the same work into either deferred execution
/// model. Execution remains model-specific: call <see cref="RespireBatch.ExecuteAsync"/> for a
/// pipeline or <see cref="RespireTransaction.CommitAsync(CancellationToken)"/> for a transaction.
/// </remarks>
public interface IRespireCommandQueue
{
    /// <summary>Queues a raw command with a supported key layout, prefixing and snapshotting its arguments.</summary>
    /// <remarks>
    /// Only known nonblocking command forms are supported; unknown/module layouts and commands
    /// requiring connection affinity are rejected before enqueueing. All keys in one command must
    /// share a Cluster slot. The result owns GC-managed storage; disposal is optional but invalidates
    /// it and its nested views. No inline arguments, flags, or per-command cancellation are accepted.
    /// </remarks>
    [Experimental("RSP0005", UrlFormat = "https://respire.tomlonghurst.dev/docs/diagnostics/{0}")]
    RespirePending<RespireResult> Execute(RespireCommand command, params RespireValue[] args);

    /// <summary>String (plain value) commands.</summary>
    IRespireBatchStringCommands Strings { get; }

    /// <summary>Generic key management commands.</summary>
    IRespireBatchKeyCommands Keys { get; }

    /// <summary>Server flush commands affecting only the execution node.</summary>
    IRespireBatchServerCommands Server { get; }

    /// <summary>Hash (field → value map) commands.</summary>
    IRespireBatchHashCommands Hashes { get; }

    /// <summary>List commands.</summary>
    IRespireBatchListCommands Lists { get; }

    /// <summary>Redis sparse array commands.</summary>
    IRespireBatchArrayCommands Arrays { get; }

    /// <summary>Set (unordered, unique members) commands.</summary>
    IRespireBatchSetCommands Sets { get; }

    /// <summary>Sorted set (score-ordered members) commands.</summary>
    IRespireBatchSortedSetCommands SortedSets { get; }

    /// <summary>Bitmap commands.</summary>
    IRespireBatchBitmapCommands Bitmaps { get; }

    /// <summary>HyperLogLog commands.</summary>
    IRespireBatchHyperLogLogCommands HyperLogLog { get; }

    /// <summary>Geospatial commands.</summary>
    IRespireBatchGeoCommands Geo { get; }

    /// <summary>Redis vector-set commands.</summary>
    IRespireBatchVectorSetCommands VectorSets { get; }

    /// <summary>Lua script evaluation.</summary>
    IRespireBatchScriptCommands Scripts { get; }

    /// <summary>Redis Functions, without automatic reload or replay.</summary>
    IRespireBatchFunctionCommands Functions { get; }

    /// <summary>Non-blocking stream append, range, count, acknowledge, remove, and trim commands.</summary>
    IRespireBatchStreamCommands Streams { get; }

    /// <inheritdoc cref="IRespireBatchStringCommands.GetString"/>
    RespirePending<string?> GetString(RespireKey key);

    /// <inheritdoc cref="IRespireBatchStringCommands.Get{T}"/>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    RespirePending<T?> Get<T>(RespireKey key);

    /// <inheritdoc cref="IRespireBatchStringCommands.TryGet{T}"/>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    RespirePending<RespireGetResult<T>> TryGet<T>(RespireKey key);

    /// <inheritdoc cref="IRespireBatchStringCommands.GetBytes"/>
    RespirePending<byte[]?> GetBytes(RespireKey key);

    /// <inheritdoc cref="IRespireBatchStringCommands.Set(RespireKey, RespireValue, RespireExpiry, RespireSetWhen)"/>
    RespirePending<bool> Set(
        RespireKey key,
        RespireValue value,
        RespireExpiry expiry = default,
        RespireSetWhen when = RespireSetWhen.Always);

    /// <inheritdoc cref="IRespireBatchStringCommands.Set{T}(RespireKey, T, RespireExpiry, RespireSetWhen)"/>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    RespirePending<bool> Set<T>(
        RespireKey key,
        T value,
        RespireExpiry expiry = default,
        RespireSetWhen when = RespireSetWhen.Always);

    /// <inheritdoc cref="IRespireBatchKeyCommands.Delete(ReadOnlySpan{RespireKey})"/>
    RespirePending<long> Delete(params ReadOnlySpan<RespireKey> keys);

    /// <inheritdoc cref="IRespireBatchKeyCommands.Exists"/>
    RespirePending<bool> Exists(RespireKey key);

    /// <inheritdoc cref="IRespireBatchStringCommands.Increment(RespireKey, long)"/>
    RespirePending<long> Increment(RespireKey key, long by = 1);

    /// <inheritdoc cref="IRespireBatchStringCommands.Decrement"/>
    RespirePending<long> Decrement(RespireKey key, long by = 1);

    /// <inheritdoc cref="IRespireBatchKeyCommands.Expire"/>
    RespirePending<bool> Expire(
        RespireKey key,
        RespireExpiry expiry,
        RespireExpireWhen when = RespireExpireWhen.Always);
}
