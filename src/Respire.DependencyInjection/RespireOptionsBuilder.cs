namespace Respire.DependencyInjection;

/// <summary>
/// Mutable builder for every public <see cref="RespireOptions"/> property. Properties are
/// generated from the options record and use its defaults.
/// </summary>
public sealed partial class RespireOptionsBuilder
{
    private RespireOptions _options = new();

    /// <summary>Enables RESP3 server-assisted client-side caching with the supplied policy.</summary>
    public RespireOptionsBuilder UseClientSideCaching(RespireClientSideCacheOptions? options = null)
    {
        ClientSideCache = options ?? new RespireClientSideCacheOptions();
        return this;
    }

    internal RespireOptions Build() => _options with
    {
        Endpoints = _options.Endpoints.ToArray(),
        ReplicaEndpoints = _options.ReplicaEndpoints.ToArray(),
    };
}
