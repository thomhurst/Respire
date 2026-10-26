namespace Respire.DependencyInjection;

/// <summary>
/// Host startup behavior for a client registered with <c>AddRespire</c> or <c>AddKeyedRespire</c>.
/// Configure the default client with <c>services.Configure&lt;RespireStartupOptions&gt;(...)</c>,
/// and a keyed client by passing its service key as the options name.
/// </summary>
public sealed class RespireStartupOptions
{
    /// <summary>
    /// Start connecting in the background when the host starts, so readiness checks do not wait
    /// for application traffic. Failed attempts retry with backoff until the first connection
    /// succeeds or the host stops. Host startup never waits for Redis. Defaults to <see langword="true"/>.
    /// </summary>
    public bool ConnectOnStart { get; set; } = true;

    internal TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    internal TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(30);
}
