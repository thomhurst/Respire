using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Respire.OutputCaching;

/// <summary>Registration for ASP.NET Core output caching on an existing Respire client.</summary>
public static class RespireOutputCacheServiceCollectionExtensions
{
    /// <summary>Registers output caching, replaces its store, and schedules expired-tag cleanup.</summary>
    /// <remarks>Register IRespireClient before resolving the store. Client lifetime stays with its existing registration.</remarks>
    public static IServiceCollection AddRespireOutputCache(this IServiceCollection services,
        Action<RespireOutputCacheOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddLogging();
        services.AddOutputCache();
        services.AddOptions<RespireOutputCacheOptions>()
            .Validate(options => options.CleanupInterval > TimeSpan.Zero && options.CleanupInterval.TotalMilliseconds <= uint.MaxValue - 1,
                "CleanupInterval must be positive and fit a timer interval.")
            .Validate(options => options.TimeProvider is not null, "TimeProvider is required.");
        if (configure is not null) services.Configure(configure);
        services.TryAddSingleton(provider => new RespireOutputCacheStore(
            provider.GetRequiredService<IRespireClient>(), provider.GetRequiredService<IOptions<RespireOutputCacheOptions>>().Value));
        services.Replace(ServiceDescriptor.Singleton<IOutputCacheStore>(provider => provider.GetRequiredService<RespireOutputCacheStore>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, OutputCacheCleanupService>());
        return services;
    }
}

internal sealed class OutputCacheCleanupService(RespireOutputCacheStore store,
    IOptions<RespireOutputCacheOptions> options, ILogger<OutputCacheCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.CleanupInterval, options.Value.TimeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try { await store.CollectExpiredTagsAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) { logger.LogWarning(error, "Respire output-cache tag cleanup failed."); }
        }
    }
}
