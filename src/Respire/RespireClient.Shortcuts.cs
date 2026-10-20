using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Internal;
using Respire.Serialization;

namespace Respire;

/// <summary>
/// Root-level shortcuts for the operations that dominate real usage.
/// </summary>
public sealed partial class RespireClient
{
    /// <inheritdoc cref="IRespireStringCommands.GetStringAsync"/>
    public ValueTask<string?> GetStringAsync(RespireKey key, CancellationToken cancellationToken = default)
        => CachedGetStringAsync(ResolveKey(key), cancellationToken);

    /// <inheritdoc cref="IRespireStringCommands.GetAsync{T}"/>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T?> GetAsync<T>(RespireKey key, CancellationToken cancellationToken = default)
        => CachedDeserializeAsync<T>(ResolveKey(key), cancellationToken);

    /// <inheritdoc cref="IRespireStringCommands.TryGetAsync{T}"/>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<RespireGetResult<T>> TryGetAsync<T>(RespireKey key, CancellationToken cancellationToken = default)
        => CachedGetAsync(
            ResolveKey(key),
            cancellationToken,
            static (RespireClient client, in Protocol.RespValue value) => client.TryDeserializeBorrowed<T>(in value));

    /// <inheritdoc cref="IRespireStringCommands.GetBytesAsync"/>
    public ValueTask<byte[]?> GetBytesAsync(RespireKey key, CancellationToken cancellationToken = default)
        => CachedGetBytesAsync(ResolveKey(key), cancellationToken);

    /// <inheritdoc cref="IRespireStringCommands.SetAsync(RespireKey, RespireValue, RespireExpiry, RespireSetWhen, CancellationToken)"/>
    public ValueTask<bool> SetAsync(
        RespireKey key,
        RespireValue value,
        RespireExpiry expiry = default,
        RespireSetWhen when = RespireSetWhen.Always,
        CancellationToken cancellationToken = default)
        => Strings.SetAsync(key, value, expiry, when, cancellationToken);

    /// <inheritdoc cref="IRespireStringCommands.SetAsync{T}(RespireKey, T, RespireExpiry, RespireSetWhen, CancellationToken)"/>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<bool> SetAsync<T>(
        RespireKey key,
        T value,
        RespireExpiry expiry = default,
        RespireSetWhen when = RespireSetWhen.Always,
        CancellationToken cancellationToken = default)
        => Strings.SetAsync(key, value, expiry, when, cancellationToken);

    /// <inheritdoc cref="IRespireKeyCommands.DeleteAsync(ReadOnlySpan{RespireKey})"/>
    public ValueTask<long> DeleteAsync(params ReadOnlySpan<RespireKey> keys)
        => Keys.DeleteAsync(keys);

    /// <inheritdoc cref="IRespireKeyCommands.DeleteAsync(ReadOnlySpan{RespireKey}, CancellationToken)"/>
    public ValueTask<long> DeleteAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken)
        => Keys.DeleteAsync(keys, cancellationToken);

    /// <summary>Deletes one key with cancellation. Redis: DEL.</summary>
    [OverloadResolutionPriority(1)]
    public ValueTask<long> DeleteAsync(RespireKey key, CancellationToken cancellationToken)
        => DeleteAsync([key], cancellationToken);

    /// <inheritdoc cref="IRespireKeyCommands.ExistsAsync"/>
    public ValueTask<bool> ExistsAsync(RespireKey key, CancellationToken cancellationToken = default)
        => Keys.ExistsAsync(key, cancellationToken);

    /// <inheritdoc cref="IRespireStringCommands.IncrementAsync(RespireKey, long, CancellationToken)"/>
    public ValueTask<long> IncrementAsync(RespireKey key, long by = 1, CancellationToken cancellationToken = default)
        => Strings.IncrementAsync(key, by, cancellationToken);

    /// <inheritdoc cref="IRespireStringCommands.DecrementAsync"/>
    public ValueTask<long> DecrementAsync(RespireKey key, long by = 1, CancellationToken cancellationToken = default)
        => Strings.DecrementAsync(key, by, cancellationToken);

    /// <inheritdoc cref="IRespireKeyCommands.ExpireAsync"/>
    public ValueTask<bool> ExpireAsync(
        RespireKey key,
        RespireExpiry expiry,
        RespireExpireWhen when = RespireExpireWhen.Always,
        CancellationToken cancellationToken = default)
        => Keys.ExpireAsync(key, expiry, when, cancellationToken);
}
