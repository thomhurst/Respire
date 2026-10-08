using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Respire.Streaming;

/// <summary>Registers hosted consumers using a DI-owned IRespireClient.</summary>
public static class RespireStreamWorkerServiceCollectionExtensions
{
    /// <summary>Registers a scoped handler that receives owned stream entry fields.</summary>
    public static IServiceCollection AddRespireStreamWorker<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(
        this IServiceCollection services, string stream, string group, RespireStreamWorkerOptions? options = null)
        where THandler : class, IRespireStreamHandler<RespireStreamEntry>
        => services.AddRespireStreamWorker<THandler, RespireStreamEntry>(stream, group, static entry => entry, options);

    /// <summary>Registers a typed handler with an explicit serializer, suitable for source-generated JSON or custom codecs.</summary>
    /// <remarks>The serializer can run concurrently on different consumers. Register IRespireClient separately;
    /// the worker does not dispose it. Each registration creates its own hosted service and consumer identities.</remarks>
    public static IServiceCollection AddRespireStreamWorker<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler, TMessage>(
        this IServiceCollection services, string stream, string group,
        Func<RespireStreamEntry, TMessage> deserialize, RespireStreamWorkerOptions? options = null)
        where THandler : class, IRespireStreamHandler<TMessage>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentNullException.ThrowIfNull(deserialize);
        options ??= new RespireStreamWorkerOptions();
        options.Validate();
        services.TryAddScoped<THandler>();
        services.AddSingleton<IHostedService>(provider => new RespireStreamWorker<THandler, TMessage>(
            provider.GetRequiredService<IRespireClient>(), provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetService<ILogger<RespireStreamWorker<THandler, TMessage>>>()
                ?? NullLogger<RespireStreamWorker<THandler, TMessage>>.Instance,
            stream, group, deserialize, options));
        return services;
    }
}
