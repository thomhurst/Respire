using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Respire.DependencyInjection;

/// <summary>Records a client registered through <c>AddRespire</c> or <c>AddKeyedRespire</c>.</summary>
internal sealed record RespireClientRegistration(string? ServiceKey);

/// <summary>
/// Connects registered lazy clients in the background when the host starts. A host behind a
/// readiness probe receives no traffic until it is ready, so it cannot rely on the first command
/// to connect. Startup returns immediately; failures retry with backoff until the host stops.
/// </summary>
internal sealed partial class RespireStartupConnector(
    IServiceProvider services,
    IEnumerable<RespireClientRegistration> registrations,
    IOptionsMonitor<RespireStartupOptions> options,
    ILoggerFactory? loggerFactory = null) : IHostedService
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly ILogger _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<RespireStartupConnector>();
    private Task _connecting = Task.CompletedTask;

    /// <summary>Completes when every startup connection has succeeded, given up, or stopped.</summary>
    internal Task Connecting => _connecting;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        List<Task>? connecting = null;
        foreach (var registration in registrations)
        {
            var startup = options.Get(registration.ServiceKey ?? Options.DefaultName);
            if (startup.ConnectOnStart)
            {
                (connecting ??= []).Add(Task.Run(() => ConnectAsync(registration, startup, _stopping.Token)));
            }
        }

        if (connecting is not null)
        {
            _connecting = Task.WhenAll(connecting);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        // ConnectAsync observes its own failures; only the caller's shutdown deadline can end this wait.
        await _connecting.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private async Task ConnectAsync(RespireClientRegistration registration, RespireStartupOptions startup, CancellationToken stopping)
    {
        var name = registration.ServiceKey ?? "default";
        RespireClient client;
        try
        {
            client = registration.ServiceKey is null
                ? services.GetRequiredService<RespireClient>()
                : services.GetRequiredKeyedService<RespireClient>(registration.ServiceKey);
        }
        catch (Exception exception)
        {
            // Resolution fails the same way every time; the first command reports it again.
            LogResolveFailed(_logger, name, exception);
            return;
        }

        var delay = startup.InitialRetryDelay;
        for (var attempt = 1; ; attempt++)
        {
            // Application traffic may have connected the client already.
            if (client.IsConnected)
            {
                return;
            }

            try
            {
                await client.PingAsync(stopping).ConfigureAwait(false);
                LogConnected(_logger, name, attempt);
                return;
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception exception)
            {
                LogConnectFailed(_logger, name, attempt, delay, exception);
            }

            try
            {
                await Task.Delay(delay, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, startup.MaxRetryDelay.Ticks));
        }
    }

    [LoggerMessage(1, LogLevel.Debug, "Respire client '{Client}' connected at startup after {Attempts} attempt(s)", EventName = "StartupConnected")]
    private static partial void LogConnected(ILogger logger, string client, int attempts);

    [LoggerMessage(2, LogLevel.Warning, "Respire client '{Client}' failed to connect at startup (attempt {Attempt}); retrying in {Delay}", EventName = "StartupConnectFailed")]
    private static partial void LogConnectFailed(ILogger logger, string client, int attempt, TimeSpan delay, Exception exception);

    [LoggerMessage(3, LogLevel.Error, "Respire client '{Client}' could not be created for startup connection", EventName = "StartupResolveFailed")]
    private static partial void LogResolveFailed(ILogger logger, string client, Exception exception);
}
