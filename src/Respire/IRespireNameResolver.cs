namespace Respire;

/// <summary>Optional physical-name resolution for integrations that track keys or route channels.</summary>
/// <remarks>
/// Implemented by <see cref="RespireClient"/> and its views. A decorator used by an integration
/// that needs physical names must implement this capability and forward to its wrapped resolver.
/// This capability is separate from the command and connection contract in <see cref="IRespireClient"/>.
/// </remarks>
public interface IRespireNameResolver
{
    /// <summary>Returns the effective Redis key after applying this view's key transformation.</summary>
    RespireKey ResolveKey(RespireKey key);

    /// <summary>Resolves a logical typed pub/sub target to its physical channel or escaped pattern.</summary>
    /// <remarks>Notification descriptors are already physical and remain unchanged. Do not pass the result
    /// back through a prefixed view: channel resolution is not idempotent.</remarks>
    RespireChannel ResolveChannel(RespireChannel channel);
}

// Only integrations and tests with internal access use this bridge. Public callers explicitly
// test for IRespireNameResolver instead of assuming that every client exposes physical names.
internal static class RespireNameResolution
{
    internal static RespireKey ResolveKey(this IRespireClient client, RespireKey key)
        => GetResolver(client).ResolveKey(key);

    internal static RespireChannel ResolveChannel(this IRespireClient client, RespireChannel channel)
        => GetResolver(client).ResolveChannel(channel);

    private static IRespireNameResolver GetResolver(IRespireClient client)
        => client as IRespireNameResolver ?? throw new RespireConfigurationException(
            "This integration requires IRespireNameResolver. Client decorators must forward physical-name resolution to the wrapped client.");
}
