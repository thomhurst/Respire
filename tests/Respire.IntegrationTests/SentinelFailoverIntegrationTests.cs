using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Respire.Testing.Containers;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
public class SentinelFailoverIntegrationTests
{
    [Test]
    [Arguments(RespireContainerServer.Redis, false)]
    [Arguments(RespireContainerServer.Valkey, false)]
    [Arguments(RespireContainerServer.Redis, true)]
    [Arguments(RespireContainerServer.Valkey, true)]
    public async Task SentinelNotificationMovesClientToPromotedPrimaryAndRejectsOldPrimary(
        RespireContainerServer serverFamily, bool abortFirstElection)
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new()
        {
            Server = serverFamily,
            Topology = RespireContainerTopology.Sentinel,
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var logger = new FailoverLogger();
        var options = fixture.CreateOptions() with
        {
            LoggerFactory = logger,
            Protocol = RespProtocol.Resp3,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            CommandTimeout = TimeSpan.FromSeconds(3),
        };
        await using var client = await RespireClient.ConnectAsync(options, deadline.Token);
        var stateChanges = new ConcurrentQueue<RespireConnectionStateChange>();
        client.ConnectionStateChanged += stateChanges.Enqueue;
        var oldPrimary = fixture.DataEndpoints[0];
        var promotedPrimary = fixture.DataEndpoints[1];
        client.Endpoint.Should().Be(oldPrimary);
        (await client.SetAsync("sentinel-failover:before", "written", cancellationToken: deadline.Token)).Should().BeTrue();

        var sentinelRouter = client.Core.Sentinel!;
        // Count distinct Sentinels: a monitor that reconnects during startup must not stand in for another.
        await sentinelRouter.Monitoring.WaitForSubscriptionsAsync(fixture.SentinelEndpoints.Count, deadline.Token);

        await using var replica = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [promotedPrimary], AllowAdmin = true,
        }, deadline.Token);
        if (abortFirstElection)
        {
            using var configured = await replica.ExecuteAsync(RespireCommands.Server.CONFIG_SET, ["replica-priority", "0"], cancellationToken: deadline.Token);
            foreach (var endpoint in fixture.SentinelEndpoints)
            {
                await using var sentinel = await RespireClient.ConnectAsync(new RespireOptions
                {
                    Endpoints = [endpoint], Protocol = RespProtocol.Resp2, AllowAdmin = true,
                }, deadline.Token);
                while (!await ReplicaHasPriorityAsync(sentinel, "0", deadline.Token))
                    await Task.Delay(100, deadline.Token);
            }
        }

        try
        {
            await fixture.StopDataNodeAsync(0, deadline.Token);
            if (abortFirstElection)
            {
                while (!(await fixture.ReadServerLogsAsync(deadline.Token)).Contains("-failover-abort-no-good-slave", StringComparison.Ordinal))
                    await Task.Delay(100, deadline.Token);
                using var configured = await replica.ExecuteAsync(RespireCommands.Server.CONFIG_SET, ["replica-priority", "100"], cancellationToken: deadline.Token);
            }
            while (!EndpointIs(client, promotedPrimary)) await Task.Delay(100, deadline.Token);
            while (!stateChanges.Any(change => change.Endpoint == promotedPrimary && change.State == RespireConnectionState.Connected))
                await Task.Delay(100, deadline.Token);

            (await client.SetAsync("sentinel-failover:after", "promoted", cancellationToken: deadline.Token)).Should().BeTrue();
            await fixture.StartDataNodeAsync(0, deadline.Token);
            await fixture.WaitForDataNodeReplicaAsync(0, deadline.Token);

            (await client.SetAsync("sentinel-failover:after-return", "still-promoted", cancellationToken: deadline.Token)).Should().BeTrue();
            client.Endpoint.Should().Be(promotedPrimary);

            // Disposal joins the Sentinel monitor tasks and closes their subscriptions.
            await client.DisposeAsync();
        }
        catch
        {
            using var diagnostics = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { Console.WriteLine(await fixture.ReadServerLogsAsync(diagnostics.Token)); }
            catch (Exception error) { Console.WriteLine($"Server diagnostics unavailable: {error.Message}"); }
            throw;
        }
    }

    private static async Task<bool> ReplicaHasPriorityAsync(RespireClient sentinel, string priority, CancellationToken cancellationToken)
    {
        using var replicas = await sentinel.ExecuteAsync(RespireCommands.Sentinel.SENTINEL_REPLICAS,
            [RespireContainerFixture.SentinelServiceName], cancellationToken: cancellationToken);
        if (replicas.Count != 1) return false;
        for (var index = 0; index + 1 < replicas[0].Count; index += 2)
            if (replicas[0][index].AsString() == "slave-priority") return replicas[0][index + 1].AsString() == priority;
        return false;
    }

    private static bool EndpointIs(IRespireClient client, RespireEndpoint endpoint)
    {
        try { return client.Endpoint == endpoint; }
        catch (InvalidOperationException) { return false; }
    }

    // Preserve discovery and monitor failures in the test report when promotion stalls on CI.
    private sealed class FailoverLogger : ILoggerFactory, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Debug;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Console.WriteLine($"{DateTime.UtcNow:O} [{level}] {formatter(state, exception)} {exception}");
    }
}
