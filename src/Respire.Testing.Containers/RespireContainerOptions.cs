namespace Respire.Testing.Containers;

/// <summary>The server binaries used by a fixture image.</summary>
public enum RespireContainerServer
{
    /// <summary>Redis server and CLI binaries.</summary>
    Redis,
    /// <summary>Valkey server and CLI binaries.</summary>
    Valkey,
}

/// <summary>The topology created inside an owned container.</summary>
public enum RespireContainerTopology
{
    /// <summary>A standalone server.</summary>
    Standalone,
    /// <summary>Three primaries covering all Cluster slots, without replicas.</summary>
    Cluster,
    /// <summary>One primary, one replica, and three Sentinels with quorum two.</summary>
    Sentinel,
}

/// <summary>Configuration for a disposable test deployment.</summary>
public sealed record RespireContainerOptions
{
    /// <summary>Server family. Defaults to Redis.</summary>
    public RespireContainerServer Server { get; init; }
    /// <summary>Deployment topology. Defaults to standalone.</summary>
    public RespireContainerTopology Topology { get; init; }
    /// <summary>Image override, including its tag or digest. It must contain the selected server's binaries and /bin/sh.</summary>
    /// <remarks>Defaults to redis:7.2-alpine or valkey/valkey:8.1-alpine. Cluster requires Redis 7+ or Valkey.</remarks>
    public string? Image { get; init; }
    /// <summary>Maximum startup and readiness time, including pulling the image. Defaults to two minutes.</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromMinutes(2);
}
