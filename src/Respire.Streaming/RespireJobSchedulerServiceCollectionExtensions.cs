using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Respire.Streaming;

/// <summary>Registers recurring scheduling for an existing named typed job queue.</summary>
public static class RespireJobSchedulerServiceCollectionExtensions
{
    /// <summary>Adds a hosted scheduler and a keyed RespireJobScheduler for a previously registered queue.</summary>
    /// <remarks>Register AddRespireJobQueue first. Register TimeProvider to override the UTC clock and timers.
    /// Multiple hosts can schedule the same queue. Polling failures are logged and retried on the next pass.</remarks>
    public static IServiceCollection AddRespireJobScheduler<T>(this IServiceCollection services, string queueName,
        TimeSpan? pollInterval = null, int maximumTicksPerPass = 64)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        var interval = pollInterval ?? TimeSpan.FromSeconds(1);
        JobQueueScheduling.ValidateDuration(interval, nameof(pollInterval));
        if (maximumTicksPerPass is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(maximumTicksPerPass));
        if (!services.Any(service => service.IsKeyedService && service.ServiceType == typeof(RespireJobQueue<T>)
            && Equals(service.ServiceKey, queueName)))
            throw new ArgumentException("Register the named typed job queue before its scheduler.", nameof(queueName));
        if (services.Any(service => service.IsKeyedService && service.ServiceType == typeof(RespireJobScheduler<T>)
            && Equals(service.ServiceKey, queueName)))
            throw new ArgumentException("Each job queue can have only one scheduler registration per host.", nameof(queueName));
        services.AddKeyedSingleton<RespireJobScheduler<T>>(queueName, (provider, _) =>
            provider.GetRequiredKeyedService<RespireJobQueue<T>>(queueName)
                .CreateScheduler(provider.GetService<TimeProvider>()));
        services.AddSingleton<IHostedService>(provider => new RecurringJobWorker<T>(
            provider.GetRequiredKeyedService<RespireJobScheduler<T>>(queueName),
            provider.GetService<TimeProvider>() ?? TimeProvider.System,
            provider.GetRequiredService<ILogger<RecurringJobWorker<T>>>(), interval, maximumTicksPerPass));
        return services;
    }
}

internal sealed partial class RecurringJobWorker<T>(RespireJobScheduler<T> scheduler, TimeProvider clock,
    ILogger<RecurringJobWorker<T>> logger, TimeSpan pollInterval, int maximumTicks) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await scheduler.RunDueAsync(maximumTicks, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    LogPassFailure(logger, exception);
                }
                await Task.Delay(pollInterval, clock, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Recurring job scheduling pass failed; retrying after the polling interval.")]
    private static partial void LogPassFailure(ILogger logger, Exception exception);
}
