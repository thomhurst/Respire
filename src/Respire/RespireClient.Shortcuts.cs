namespace Respire;

/// <summary>
/// Root-level shortcuts for the operations that dominate real usage. Each delegates to the
/// matching facet, which carries the full command set.
/// </summary>
public sealed partial class RespireClient
{
    /// <inheritdoc cref="IStringCommands.GetStringAsync"/>
    public ValueTask<string?> GetStringAsync(RespireKey key, CancellationToken cancellationToken = default)
        => Strings.GetStringAsync(key, cancellationToken);

    /// <inheritdoc cref="IStringCommands.GetAsync{T}"/>
    public ValueTask<T?> GetAsync<T>(RespireKey key, CancellationToken cancellationToken = default)
        => Strings.GetAsync<T>(key, cancellationToken);

    /// <inheritdoc cref="IStringCommands.TryGetAsync{T}"/>
    public ValueTask<RespireGet<T>> TryGetAsync<T>(RespireKey key, CancellationToken cancellationToken = default)
        => Strings.TryGetAsync<T>(key, cancellationToken);

    /// <inheritdoc cref="IStringCommands.GetBytesAsync"/>
    public ValueTask<byte[]?> GetBytesAsync(RespireKey key, CancellationToken cancellationToken = default)
        => Strings.GetBytesAsync(key, cancellationToken);

    /// <inheritdoc cref="IStringCommands.SetAsync(RespireKey, RespireValue, RespireExpiry, SetWhen, CancellationToken)"/>
    public ValueTask<bool> SetAsync(
        RespireKey key,
        RespireValue value,
        RespireExpiry expiry = default,
        SetWhen when = SetWhen.Always,
        CancellationToken cancellationToken = default)
        => Strings.SetAsync(key, value, expiry, when, cancellationToken);

    /// <inheritdoc cref="IStringCommands.SetAsync{T}(RespireKey, T, RespireExpiry, SetWhen, CancellationToken)"/>
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
    public ValueTask<bool> ExpireAsync(RespireKey key, TimeSpan expiry, CancellationToken cancellationToken = default)
        => Keys.ExpireAsync(key, expiry, cancellationToken);
}
