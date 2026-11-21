using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Respire.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.SignalR.Tests;

public class HostShutdownTests
{
    private const int SubscriptionEndedEventId = 17;

    // Microsoft DI 10 disposes the client through its interface alias first when that alias is
    // first resolved after the hub manager; the manager must still stop before the client goes.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NormalStopDoesNotReportEndedSubscriptions(bool aliasFirst)
    {
        await using var redis = new RespireFakeServer();
        var logs = new ErrorLog();
        var host = BuildHost(redis, logs);
        await using (host as IAsyncDisposable)
        {
            if (aliasFirst) _ = host.Services.GetRequiredService<IRespireClient>();
            var manager = host.Services.GetRequiredService<HubLifetimeManager<ShutdownTestHub>>();
            await manager.SendAllAsync("probe", []);
            if (!aliasFirst) _ = host.Services.GetRequiredService<IRespireClient>();
            await host.StartAsync();
            await host.StopAsync();
        }

        await Assert.That(logs.Events.Where(static e => e.Id == SubscriptionEndedEventId)).IsEmpty();
    }

    [Test]
    public async Task ClientDisposedWhileRunningStillReportsEndedSubscription()
    {
        await using var redis = new RespireFakeServer();
        var logs = new ErrorLog();
        var host = BuildHost(redis, logs);
        await using (host as IAsyncDisposable)
        {
            var manager = host.Services.GetRequiredService<HubLifetimeManager<ShutdownTestHub>>();
            await manager.SendAllAsync("probe", []);
            await host.StartAsync();
            await host.Services.GetRequiredService<RespireClient>().DisposeAsync();
            var ended = await logs.SubscriptionEnded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(ended).Contains("ClientDisposed");
            await host.StopAsync();
        }
    }

    private static IHost BuildHost(RespireFakeServer redis, ErrorLog logs)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Logging.AddProvider(logs);
        builder.Services.AddRespire(_ => redis.CreateOptions());
        builder.Services.AddSignalR().AddRespire();
        return builder.Build();
    }

    private sealed class ErrorLog : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<(int Id, string Message)> Events { get; } = new();

        public TaskCompletionSource<string> SubscriptionEnded { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            Events.Enqueue((eventId.Id, message));
            if (eventId.Id == SubscriptionEndedEventId) SubscriptionEnded.TrySetResult(message);
        }
    }
}

public sealed class ShutdownTestHub : Hub;
