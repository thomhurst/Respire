using System.Diagnostics.CodeAnalysis;
using Respire.Commands;
using Respire.Internal;
using Respire.Serialization;

namespace Respire;

/// <summary>
/// Root-level shortcuts for the operations that dominate real usage.
/// </summary>
public sealed partial class RespireClient
{
    /// <inheritdoc cref="IStringCommands.GetStringAsync"/>
    public ValueTask<string?> GetStringAsync(RespireKey key, CancellationToken cancellationToken = default)
        => CachedGetAsync(
            ResolveKey(key),
            cancellationToken,
            static (RespireClient _, in Protocol.RespValue value) => ResponseReader.StringOrNull(in value));

    /// <inheritdoc cref="IStringCommands.GetAsync{T}"/>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T?> GetAsync<T>(RespireKey key, CancellationToken cancellationToken = default)
        => CachedGetAsync(
            ResolveKey(key),
            cancellationToken,
            static (RespireClient client, in Protocol.RespValue value) => client.DeserializeBorrowed<T>(in value));

    /// <inheritdoc cref="IStringCommands.TryGetAsync{T}"/>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<RespireGet<T>> TryGetAsync<T>(RespireKey key, CancellationToken cancellationToken = default)
        => CachedGetAsync(
            ResolveKey(key),
            cancellationToken,
            static (RespireClient client, in Protocol.RespValue value) => client.TryDeserializeBorrowed<T>(in value));

    /// <inheritdoc cref="IStringCommands.GetBytesAsync"/>
    public ValueTask<byte[]?> GetBytesAsync(RespireKey key, CancellationToken cancellationToken = default)
        => CachedGetAsync(
            ResolveKey(key),
            cancellationToken,
            static (RespireClient _, in Protocol.RespValue value) => ResponseReader.BytesOrNull(in value));

    /// <inheritdoc cref="IStringCommands.SetAsync(RespireKey, RespireValue, RespireExpiry, SetWhen, CancellationToken)"/>
    public ValueTask<bool> SetAsync(
        RespireKey key,
        RespireValue value,
        RespireExpiry expiry = default,
        SetWhen when = SetWhen.Always,
        CancellationToken cancellationToken = default)
        => Strings.SetAsync(key, value, expiry, when, cancellationToken);

    /// <inheritdoc cref="IStringCommands.SetAsync{T}(RespireKey, T, RespireExpiry, SetWhen, CancellationToken)"/>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<bool> SetAsync<T>(
        RespireKey key,
        T value,
        RespireExpiry expiry = default,
        SetWhen when = SetWhen.Always,
        CancellationToken cancellationToken = default)
        => Strings.SetAsync(key, value, expiry, when, cancellationToken);

    /// <inheritdoc cref="IKeyCommands.DeleteAsync(ReadOnlySpan{RespireKey})"/>
    public ValueTask<long> DeleteAsync(params ReadOnlySpan<RespireKey> keys)
        => Keys.DeleteAsync(keys);

    /// <inheritdoc cref="IKeyCommands.DeleteAsync(ReadOnlySpan{RespireKey}, CancellationToken)"/>
    public ValueTask<long> DeleteAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken)
        => Keys.DeleteAsync(keys, cancellationToken);

    /// <inheritdoc cref="IKeyCommands.ExistsAsync"/>
    public ValueTask<bool> ExistsAsync(RespireKey key, CancellationToken cancellationToken = default)
        => Keys.ExistsAsync(key, cancellationToken);

    /// <inheritdoc cref="IStringCommands.IncrementAsync(RespireKey, long, CancellationToken)"/>
    public ValueTask<long> IncrementAsync(RespireKey key, long by = 1, CancellationToken cancellationToken = default)
        => Strings.IncrementAsync(key, by, cancellationToken);

    /// <inheritdoc cref="IStringCommands.DecrementAsync"/>
    public ValueTask<long> DecrementAsync(RespireKey key, long by = 1, CancellationToken cancellationToken = default)
        => Strings.DecrementAsync(key, by, cancellationToken);

    /// <inheritdoc cref="IKeyCommands.ExpireAsync"/>
    public ValueTask<bool> ExpireAsync(
        RespireKey key,
        RespireExpiry expiry,
        ExpireWhen when = ExpireWhen.Always,
        CancellationToken cancellationToken = default)
        => Keys.ExpireAsync(key, expiry, when, cancellationToken);
}
