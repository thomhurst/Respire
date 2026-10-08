using Microsoft.Extensions.DependencyInjection;

namespace Respire.SignalR;

/// <summary>Registers SignalR scale-out using a Respire client registered in the service collection.</summary>
public static class RespireSignalRServiceCollectionExtensions
{
    /// <summary>Uses the DI-owned <see cref="RespireClient"/> for pub/sub. Register that client before resolving the lifetime manager.</summary>
    public static ISignalRServerBuilder AddRespire(this ISignalRServerBuilder builder,
        Action<RespireSignalROptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = builder.Services.AddOptions<RespireSignalROptions>();
        if (configure is not null) options.Configure(configure);
        options.Validate(static value => value.ChannelPrefix is not null
            && value.SubscriptionBufferSize > 0 && value.GroupAckTimeout > TimeSpan.Zero
            && value.GroupAckTimeout <= TimeSpan.FromMilliseconds(uint.MaxValue - 1), "Invalid Respire SignalR options.");
        builder.Services.AddSingleton(typeof(HubLifetimeManager<>), typeof(RespireHubLifetimeManager<>));
        return builder;
    }
}
