using System.Diagnostics.CodeAnalysis;
using Respire.Serialization;

namespace Respire;

/// <summary>
/// The commands that can be queued by both a <see cref="RespireBatch"/> and a
/// <see cref="RespireTransaction"/>.
/// </summary>
/// <remarks>
/// Use this interface when helper code should queue the same work into either deferred execution
/// model. Execution remains model-specific: call <see cref="RespireBatch.ExecuteAsync"/> for a
/// pipeline or <see cref="RespireTransaction.CommitAsync"/> for a transaction.
/// </remarks>
public interface IRespireCommandQueue
{
    /// <summary>String (plain value) commands.</summary>
    IBatchStringCommands Strings { get; }

    /// <summary>Generic key management commands.</summary>
    IBatchKeyCommands Keys { get; }

    /// <summary>Hash (field → value map) commands.</summary>
    IBatchHashCommands Hashes { get; }

    /// <summary>List commands.</summary>
    IBatchListCommands Lists { get; }

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

    /// <summary>Lua script evaluation.</summary>
    IBatchScriptCommands Scripts { get; }

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
