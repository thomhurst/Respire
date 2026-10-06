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
/// pipeline or <see cref="RespireTransaction.CommitAsync"/> for a transaction.
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
    RespirePending<RespireResult> Execute(RespireCommand command, params RespireValue[] args);

    /// <summary>String (plain value) commands.</summary>
    IBatchStringCommands Strings { get; }

    /// <summary>Generic key management commands.</summary>
    IBatchKeyCommands Keys { get; }

    /// <summary>Server flush commands affecting only the execution node.</summary>
    IBatchServerCommands Server => throw new NotSupportedException("This queue does not support server flush commands.");

    /// <summary>Hash (field → value map) commands.</summary>
    IBatchHashCommands Hashes { get; }

    /// <summary>List commands.</summary>
    IBatchListCommands Lists { get; }

    /// <summary>Redis sparse array commands.</summary>
    /// <remarks>The default getter throws NotSupportedException when an implementation does not support arrays.</remarks>
    IBatchArrayCommands Arrays => throw new NotSupportedException("This queue does not support Redis arrays.");

    /// <summary>Set (unordered, unique members) commands.</summary>
    IBatchSetCommands Sets { get; }

    /// <summary>Sorted set (score-ordered members) commands.</summary>
    IBatchSortedSetCommands SortedSets { get; }

    /// <summary>Bitmap commands.</summary>
    IBatchBitmapCommands Bitmaps { get; }

    /// <summary>HyperLogLog commands.</summary>
    IBatchHyperLogLogCommands HyperLogLog { get; }

    /// <summary>Geospatial commands.</summary>
    IBatchGeoCommands Geo { get; }

    /// <summary>Redis vector-set commands.</summary>
    IBatchVectorSetCommands VectorSets { get; }

    /// <summary>Lua script evaluation.</summary>
    IBatchScriptCommands Scripts { get; }

    /// <summary>Redis Functions, without automatic reload or replay.</summary>
    IBatchFunctionCommands Functions => throw new NotSupportedException("This queue does not support Redis Functions.");

    /// <summary>Non-blocking stream append, range, count, acknowledge, remove, and trim commands.</summary>
    IBatchStreamCommands Streams { get; }

    /// <inheritdoc cref="IBatchStringCommands.GetString"/>
    RespirePending<string?> GetString(RespireKey key);

    /// <inheritdoc cref="IBatchStringCommands.Get{T}"/>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    RespirePending<T?> Get<T>(RespireKey key);

    /// <inheritdoc cref="IBatchStringCommands.TryGet{T}"/>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    RespirePending<RespireGet<T>> TryGet<T>(RespireKey key);

    /// <inheritdoc cref="IBatchStringCommands.GetBytes"/>
    RespirePending<byte[]?> GetBytes(RespireKey key);

    /// <inheritdoc cref="IBatchStringCommands.Set(RespireKey, RespireValue, RespireExpiry, SetWhen)"/>
    RespirePending<bool> Set(
        RespireKey key,
        RespireValue value,
        RespireExpiry expiry = default,
        SetWhen when = SetWhen.Always);

    /// <inheritdoc cref="IBatchStringCommands.Set{T}(RespireKey, T, RespireExpiry, SetWhen)"/>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    RespirePending<bool> Set<T>(
        RespireKey key,
        T value,
        RespireExpiry expiry = default,
        SetWhen when = SetWhen.Always);

    /// <inheritdoc cref="IBatchKeyCommands.Delete(ReadOnlySpan{RespireKey})"/>
    RespirePending<long> Delete(params ReadOnlySpan<RespireKey> keys);

    /// <inheritdoc cref="IBatchKeyCommands.Exists"/>
    RespirePending<bool> Exists(RespireKey key);

    /// <inheritdoc cref="IBatchStringCommands.Increment(RespireKey, long)"/>
    RespirePending<long> Increment(RespireKey key, long by = 1);

    /// <inheritdoc cref="IBatchStringCommands.Decrement"/>
    RespirePending<long> Decrement(RespireKey key, long by = 1);

    /// <inheritdoc cref="IBatchKeyCommands.Expire"/>
    RespirePending<bool> Expire(
        RespireKey key,
        RespireExpiry expiry,
        ExpireWhen when = ExpireWhen.Always);
}
