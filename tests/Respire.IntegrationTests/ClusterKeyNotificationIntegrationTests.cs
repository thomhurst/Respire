using System.Globalization;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Respire.Internal;
using TUnit.Core;
using TUnit.Core.Interfaces;

namespace Respire.IntegrationTests;

[NotInParallel]
[ClassDataSource<ClusterKeyNotificationRedisCluster>(Shared = SharedType.PerTestSession)]
public sealed class ClusterKeyNotificationIntegrationTests(ClusterKeyNotificationRedisCluster cluster)
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task PatternSubkeyAndExactKeyRoutesCoverRequiredPrimaries(int protocol)
    {
        var options = cluster.Options(protocol);
        await using var client = await RespireClient.ConnectAsync(options);
        var prefix = $"cluster-notifications:{Guid.NewGuid():N}:";
        var keys = Enumerable.Range(0, 3).Select(node => cluster.KeyForPrimary(node, prefix)).ToArray();

        await using (var subscription = await client.SubscribeAsync(RespireChannel.KeySpacePrefix(prefix, 0)))
        {
            foreach (var key in keys) await client.SetAsync(key, "value");
            await WaitForNotificationsAsync(subscription, keys, RespireKeyNotificationType.Set);
        }

        await using (var subscription = await client.SubscribeAsync(RespireChannel.SubKeySpacePrefix(prefix, 0)))
        {
            foreach (var key in keys)
            {
                using (var deleted = await client.ExecuteAsync("DEL", key)) { }
                using (var reply = await client.ExecuteAsync("HSET", key, "field", "value")) { }
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await foreach (var message in subscription.WithCancellation(deadline.Token))
            {
                if (!message.TryParseKeyNotification(out var notification)
                    || notification.Type != RespireKeyNotificationType.HSet
                    || notification.GetSubKeys().Count == 0) continue;
                seen.Add(Encoding.UTF8.GetString(notification.KeyBytes.Span));
                if (seen.Count == keys.Length) break;
            }
            seen.Should().BeEquivalentTo(keys);
        }

        var exact = RespireChannel.KeySpaceSingleKey(keys[0], 0);
        await using (var subscription = await client.SubscribeAsync(exact))
        {
            var channel = exact.ToString();
            var counts = new int[3];
            for (var node = 0; node < counts.Length; node++)
            {
                var result = await cluster.CommandAsync(node, "PUBSUB", "NUMSUB", channel);
                var fields = result.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                counts[node] = int.Parse(fields[^1], CultureInfo.InvariantCulture);
            }
            counts.Should().Equal(1, 0, 0);
            await subscription.DisposeAsync();
            for (var node = 0; node < counts.Length; node++)
            {
                var result = await cluster.CommandAsync(node, "PUBSUB", "NUMSUB", channel);
                var fields = result.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                int.Parse(fields[^1], CultureInfo.InvariantCulture).Should().Be(0);
            }
        }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task RestartedPrimaryResubscribesAndDeliversAgain(int protocol)
    {
        var options = cluster.Options(protocol);
        await using var client = await RespireClient.ConnectAsync(options);
        var prefix = $"cluster-restart:{Guid.NewGuid():N}:";
        var restartedKey = cluster.KeyForPrimary(1, prefix);
        await using var subscription = await client.SubscribeAsync(RespireChannel.KeySpacePrefix(prefix, 0));
        var endpoint = new RespireEndpoint("127.0.0.1", cluster.Port(1));
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += change =>
        {
            if (change.Endpoint == endpoint && change.State == RespireConnectionState.Connected
                && change.ReconnectSource == RespireReconnectSource.PubSub)
                recovered.TrySetResult();
        };

        await cluster.RestartPrimaryAsync(1);
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            while (int.Parse((await cluster.CommandAsync(1, "PUBSUB", "NUMPAT")).Trim(), CultureInfo.InvariantCulture) == 0)
                await Task.Delay(10, deadline.Token);
        }
        await client.SetAsync(restartedKey, "after-restart");
        await WaitForNotificationsAsync(subscription, [restartedKey], RespireKeyNotificationType.Set);
    }

    private static async Task WaitForNotificationsAsync(RespireSubscription subscription,
        string[] keys, RespireKeyNotificationType expectedType)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await foreach (var message in subscription.WithCancellation(deadline.Token))
        {
            if (!message.TryParseKeyNotification(out var notification) || notification.Type != expectedType) continue;
            seen.Add(Encoding.UTF8.GetString(notification.KeyBytes.Span));
            if (seen.Count == keys.Length) break;
        }
        seen.Should().BeEquivalentTo(keys);
    }
}

public sealed class ClusterKeyNotificationRedisCluster : IAsyncInitializer, IAsyncDisposable
{
    private const string NotificationFlags = "KEAmoncSTIV";
    private readonly IContainer _container = new ContainerBuilder("redis:8.8-alpine")
        .WithPortBinding(7000, true).WithPortBinding(7001, true).WithPortBinding(7002, true)
        .WithEntrypoint("sh", "-c")
        .WithCommand("for port in 7000 7001 7002; do mkdir -p /data/$port; redis-server --port $port --dir /data/$port --cluster-enabled yes --cluster-config-file nodes.conf --cluster-node-timeout 1000 --cluster-announce-ip 127.0.0.1 --appendonly no --protected-mode no --enable-debug-command yes & done; wait")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(7000)
            .UntilInternalTcpPortIsAvailable(7001).UntilInternalTcpPortIsAvailable(7002))
        .Build();
    private readonly string[] _nodeIds = new string[3];

    internal int Port(int node) => _container.GetMappedPublicPort(7000 + node);
    internal RespireOptions Options(int protocol) => new()
    {
        UseCluster = true,
        Protocol = (RespProtocol)protocol,
        Connections = 1,
        Endpoints = [new RespireEndpoint("127.0.0.1", Port(0))],
    };

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        try
        {
            for (var node = 0; node < 3; node++)
            {
                await CommandAsync(node, "CONFIG", "SET", "cluster-announce-port", Port(node).ToString(CultureInfo.InvariantCulture));
                await CommandAsync(node, "CONFIG", "SET", "notify-keyspace-events", NotificationFlags);
                await CommandAsync(node, "CLUSTER", "SET-CONFIG-EPOCH", (node + 1).ToString(CultureInfo.InvariantCulture));
                var start = node * 5461;
                var end = node == 2 ? 16383 : (node + 1) * 5461 - 1;
                await CommandAsync(node, "CLUSTER", "ADDSLOTSRANGE", start.ToString(CultureInfo.InvariantCulture), end.ToString(CultureInfo.InvariantCulture));
                _nodeIds[node] = (await CommandAsync(node, "CLUSTER", "MYID")).Trim();
            }
            await CommandAsync(0, "CLUSTER", "MEET", "127.0.0.1", "7001");
            await CommandAsync(0, "CLUSTER", "MEET", "127.0.0.1", "7002");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            for (var node = 0; node < 3; node++)
            {
                while (!(await CommandAsync(node, "CLUSTER", "INFO")).Contains("cluster_state:ok", StringComparison.Ordinal))
                    await Task.Delay(100, deadline.Token);
            }
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    internal string KeyForPrimary(int node, string prefix)
    {
        var start = node * 5461;
        var targetSlot = start + (node == 2 ? 100 : 100);
        var tag = TagForSlot(targetSlot);
        return $"{prefix}{{{tag}}}:key";
    }

    internal async Task RestartPrimaryAsync(int node)
    {
        _ = await _container.ExecAsync(["redis-cli", "--raw", "-p", (7000 + node).ToString(CultureInfo.InvariantCulture), "SHUTDOWN", "NOSAVE"]);
        var internalPort = 7000 + node;
        _ = await _container.ExecAsync([
            "redis-server", "--port", internalPort.ToString(CultureInfo.InvariantCulture),
            "--dir", $"/data/{internalPort}", "--cluster-enabled", "yes", "--cluster-config-file", "nodes.conf",
            "--cluster-node-timeout", "1000", "--cluster-announce-ip", "127.0.0.1",
            "--cluster-announce-port", Port(node).ToString(CultureInfo.InvariantCulture),
            "--appendonly", "no", "--protected-mode", "no", "--enable-debug-command", "yes",
            "--notify-keyspace-events", NotificationFlags, "--daemonize", "yes",
            "--pidfile", $"/tmp/redis-{internalPort}.pid", "--logfile", $"/tmp/redis-{internalPort}.log",
        ]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            try
            {
                if ((await CommandAsync(node, "PING")).Trim() == "PONG") break;
            }
            catch (InvalidOperationException) { }
            await Task.Delay(100, deadline.Token);
        }
        while (!(await CommandAsync(node, "CLUSTER", "INFO")).Contains("cluster_state:ok", StringComparison.Ordinal))
            await Task.Delay(100, deadline.Token);
        await CommandAsync(node, "CONFIG", "SET", "notify-keyspace-events", NotificationFlags);
    }

    internal Task<string> CommandAsync(int node, params string[] arguments)
        => ExecAsync(["redis-cli", "-e", "--raw", "-p", (7000 + node).ToString(CultureInfo.InvariantCulture), .. arguments]);

    private async Task<string> ExecAsync(string[] arguments)
    {
        var result = await _container.ExecAsync(arguments);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Cluster fixture command failed: {string.Join(' ', arguments)}; {result.Stdout} {result.Stderr}");
        return result.Stdout;
    }

    private static string TagForSlot(int slot)
    {
        for (var index = 0; ; index++)
        {
            var tag = index.ToString(CultureInfo.InvariantCulture);
            if (ClusterHash.GetSlot(tag) == slot) return tag;
        }
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
