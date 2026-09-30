using DotNet.Testcontainers.Builders;
using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

public class ServerDiagnosticsIntegrationTests
{
    [Test]
    [Arguments("redis:6.2.14-alpine", 2, false)]
    [Arguments("redis:6.2.14-alpine", 3, false)]
    [Arguments("redis:7.0.15-alpine", 2, true)]
    [Arguments("redis:7.0.15-alpine", 3, true)]
    [Arguments("valkey/valkey:8.1-alpine", 2, true)]
    [Arguments("valkey/valkey:8.1-alpine", 3, true)]
    public async Task NodeLocalDiagnosticsPreserveVersionedContracts(string image, int protocol, bool histogramSupported)
    {
        // Latency configuration, histograms, and slow logs are server-global: own this instance.
        string[] command = [image.StartsWith("valkey/", StringComparison.Ordinal) ? "valkey-server" : "redis-server",
            "--latency-monitor-threshold", "1", "--slowlog-log-slower-than", "0", "--slowlog-max-len", "128"];
        if (histogramSupported) command = [.. command, "--enable-debug-command", "yes"];
        await using var container = new ContainerBuilder(image).WithCommand(command).WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build();
        await container.StartAsync();
        var address = $"redis://{container.Hostname}:{container.GetMappedPublicPort(6379)}?protocol={protocol}";
        await using var client = await RespireClient.ConnectAsync(address + "&allowAdmin=true");
        var server = client.WithKeyPrefix("ignored:").Server;
        (await server.LatencyHistoryAsync("not-an-event")).Should().BeEmpty();
        if (histogramSupported) await server.SetConfigAsync("latency-tracking", "yes");
        using (var slept = await client.ExecuteAsync(RespireCommands.Server.DEBUG, "SLEEP", "0.02")) { }
        var history = await server.LatencyHistoryAsync("command");
        history.Should().NotBeEmpty();
        history.Should().Contain(sample => sample.Latency >= TimeSpan.FromMilliseconds(10));
        history.Should().OnlyContain(sample => sample.Timestamp >= DateTimeOffset.UnixEpoch);
        (await server.LatencyDoctorAsync()).Should().NotBeNullOrEmpty();
        (await server.MemoryDoctorAsync()).Should().NotBeNullOrEmpty();
        await client.SetAsync("key", "value");
        (await client.GetStringAsync("key")).Should().Be("value");
        (await client.GetStringAsync("key")).Should().Be("value");

        RespireLatencyHistogram[] histograms = [];
        if (histogramSupported)
        {
            histograms = await server.LatencyHistogramsAsync(["get", "get", "not-a-command"]);
            histograms.Should().HaveCount(2); // Redis preserves repeated requested names in its map-shaped reply.
            foreach (var histogram in histograms)
            {
                histogram.Command.Should().Be("get");
                histogram.Calls.Should().BeGreaterThanOrEqualTo(2);
                histogram.Buckets.Should().NotBeEmpty();
                histogram.Buckets[^1].CumulativeCount.Should().Be(histogram.Calls);
                histogram.Buckets.Select(bucket => bucket.UpperBoundMicroseconds).Should().BeInAscendingOrder();
            }
            (await server.LatencyHistogramsAsync(["not-a-command"])).Should().BeEmpty();
            (await server.LatencyHistogramsAsync()).Should().NotBeEmpty();
            (await server.LatencyHistogramsOnAllNodesAsync(["get"])).Single().Value.Should().ContainSingle();
        }
        else
        {
            Func<Task> unsupported = async () => await server.LatencyHistogramsAsync();
            await unsupported.Should().ThrowAsync<RespireServerException>();
            var failure = (await server.LatencyHistogramsOnAllNodesAsync()).Single();
            failure.Error.Should().BeOfType<RespireServerException>();
            failure.Endpoint.Port.Should().Be(container.GetMappedPublicPort(6379));
        }

        // Disable new slow-log entries before comparing two independent snapshots.
        await server.SetConfigAsync("slowlog-log-slower-than", -1);
        var length = await server.SlowLogLengthAsync();
        length.Should().BeGreaterThan(0);
        length.Should().Be((await server.SlowLogAsync(128)).LongLength);
        (await server.LatestLatencyAsync()).Should().NotBeEmpty();
        (await server.MemoryStatsAsync()).Values.Should().NotBeEmpty();
        await server.PurgeMemoryAsync();
        (await client.GetStringAsync("key")).Should().Be("value");
        (await server.LatencyDoctorOnAllNodesAsync()).Single().Value.Should().NotBeNullOrEmpty();
        (await server.LatencyHistoryOnAllNodesAsync("command")).Single().Value.Should().NotBeEmpty();
        (await server.MemoryDoctorOnAllNodesAsync()).Single().Value.Should().NotBeNullOrEmpty();
        (await server.PurgeMemoryOnAllNodesAsync()).Single().Value.Should().BeTrue();
        var result = (await server.SlowLogLengthOnAllNodesAsync()).Single();
        result.Endpoint.Port.Should().Be(container.GetMappedPublicPort(6379));
        result.Value.Should().Be(length);

        await using var denied = await RespireClient.ConnectAsync(address);
        Func<Task> purge = async () => await denied.Server.PurgeMemoryAsync();
        await purge.Should().ThrowAsync<NotSupportedException>();
        Func<Task> purgeAll = async () => await denied.Server.PurgeMemoryOnAllNodesAsync();
        await purgeAll.Should().ThrowAsync<NotSupportedException>();
        await client.DisposeAsync();
        history[0].Latency.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        if (histogramSupported) histograms[0].Buckets.Should().NotBeEmpty();
    }
}
