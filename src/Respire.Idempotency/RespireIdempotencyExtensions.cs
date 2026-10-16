using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Respire.Coordination;

namespace Respire.Idempotency;

/// <summary>Registers and enables opt-in HTTP idempotency.</summary>
public static class RespireIdempotencyExtensions
{
    /// <summary>Registers HTTP idempotency using the application's registered <see cref="IRespireClient"/>.</summary>
    public static IServiceCollection AddRespireIdempotency(this IServiceCollection services,
        Action<RespireIdempotencyOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddOptions<RespireIdempotencyOptions>().Configure(configure)
            .Validate(o => o.CallerScope is not null, "CallerScope is required for response isolation.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.KeyPrefix), "KeyPrefix must not be empty.")
            .Validate(o => o.TimeToLive >= TimeSpan.FromMilliseconds(1), "TimeToLive must be at least one millisecond.")
            .Validate(o => o.MaxRequestBodyBytes is > 0 and <= 16 * 1024 * 1024
                && o.MaxResponseBodyBytes is > 0 and <= 16 * 1024 * 1024, "Body limits must be between 1 byte and 16 MiB.")
            .Validate(o => o.StoredStatusCodes.All(s => s is >= 200 and <= 599), "Stored status codes must be between 200 and 599.")
            .ValidateOnStart();
        services.TryAddSingleton<RespireIdempotency>();
        return services;
    }

    /// <summary>Adds middleware after routing, authentication, and authorization, and before endpoint execution.</summary>
    public static IApplicationBuilder UseRespireIdempotency(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(next => new RespireIdempotencyMiddleware(next,
            app.ApplicationServices.GetRequiredService<RespireIdempotency>(),
            app.ApplicationServices.GetRequiredService<IOptions<RespireIdempotencyOptions>>(),
            app.ApplicationServices.GetRequiredService<ILogger<RespireIdempotencyMiddleware>>()).InvokeAsync);
    }

    /// <summary>Enables buffering and replay for an endpoint under an application-unique stable name.</summary>
    public static TBuilder WithRespireIdempotency<TBuilder>(this TBuilder builder, string endpointName)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointName);
        builder.Add(endpoint => endpoint.Metadata.Add(new IdempotencyEndpoint(endpointName)));
        return builder;
    }
}

internal sealed record IdempotencyEndpoint(string Name);
