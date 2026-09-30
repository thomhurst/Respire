using FluentAssertions;
using Respire.Testing.Containers;
using TUnit.Core;

namespace Respire.IntegrationTests;

[NotInParallel]
public class CredentialProviderIntegrationTests
{
    [Test]
    [Arguments(RespireContainerTopology.Standalone)]
    [Arguments(RespireContainerTopology.Cluster)]
    [Arguments(RespireContainerTopology.Sentinel)]
    public async Task AclRotationPreservesConnectionsAndSubscribedDelivery(RespireContainerTopology topology)
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new() { Topology = topology });
        var clock = new CredentialTestClock();
        var data = new Provider(new("data-user", "first", clock.GetUtcNow().AddSeconds(30)));
        var discovery = new Provider(new("sentinel-user", "sentinel-first"));
        var administrators = new List<RespireClient>();
        var sentinelAdministrators = new List<RespireClient>();
        try
        {
            foreach (var endpoint in fixture.DataEndpoints)
            {
                var admin = await RespireClient.ConnectAsync(new RespireOptions { Endpoints = [endpoint], AllowAdmin = true });
                administrators.Add(admin);
                await SetPasswordAsync(admin, "data-user", "first");
            }
            foreach (var endpoint in fixture.SentinelEndpoints)
            {
                var admin = await RespireClient.ConnectAsync(new RespireOptions { Endpoints = [endpoint], AllowAdmin = true });
                sentinelAdministrators.Add(admin);
                await SetPasswordAsync(admin, "sentinel-user", "sentinel-first");
            }
            var options = fixture.CreateOptions() with
            {
                CredentialProvider = data, SentinelCredentialProvider = discovery,
                CredentialTimeProvider = clock, CredentialRefreshBeforeExpiry = TimeSpan.FromSeconds(10),
                CredentialRefreshRetryDelay = TimeSpan.FromSeconds(1), Protocol = RespProtocol.Resp3,
                ClientName = "credential-rotation", Connections = 1, ConnectTimeout = TimeSpan.FromSeconds(10),
                ClientSideCache = new RespireClientSideCacheOptions(),
            };
            await using var client = await RespireClient.ConnectAsync(options);
            // These hash tags exercise all three Cluster primaries.
            foreach (var key in new[] { "{a}:credential", "{b}:credential", "{c}:credential" })
            {
                (await client.SetAsync(key, "before")).Should().BeTrue();
                (await client.GetStringAsync(key)).Should().Be("before");
            }
            client.ClientSideCache!.Count.Should().Be(3);
            await using var subscription = await client.SubscribeAsync("credential-channel");
            using var messagesCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var messages = subscription.GetAsyncEnumerator(messagesCancellation.Token);
            var before = messages.MoveNextAsync().AsTask();
            (await client.PublishAsync("credential-channel", "before")).Should().Be(1);
            (await before).Should().BeTrue();
            messages.Current.Text.Should().Be("before");

            // A bounded server-blocking command occupies a dedicated authenticated socket.
            var blocked = client.Lists.LeftPopAsync("{a}:credential-block", TimeSpan.FromSeconds(10)).AsTask();
            Dictionary<string, string> initial = [];
            await UntilAsync(async () =>
            {
                initial = await ConnectionsAsync(administrators);
                return initial.Values.Contains("blpop");
            });
            initial.Count.Should().BeGreaterThanOrEqualTo(topology == RespireContainerTopology.Cluster ? 5 : 3);
            foreach (var admin in administrators) await SetPasswordAsync(admin, "data-user", "second");
            foreach (var admin in sentinelAdministrators) await SetPasswordAsync(admin, "sentinel-user", "sentinel-second");
            data.Current = new("data-user", "second", clock.GetUtcNow().AddSeconds(60));
            discovery.Current = new("sentinel-user", "sentinel-second");
            clock.Advance(TimeSpan.FromSeconds(20));
            // Every non-blocked physical connection processes AUTH without reconnecting.
            await UntilAsync(async () =>
            {
                var current = await ConnectionsAsync(administrators);
                return current.Count == initial.Count && current.All(pair =>
                    initial.ContainsKey(pair.Key) && (initial[pair.Key] == "blpop" || pair.Value == "auth"));
            });
            // Redis cannot process queued AUTH until BLPOP finishes. Unblock before its deadline.
            await using (var writer = await RespireClient.ConnectAsync(fixture.CreateOptions()))
                await writer.Lists.LeftPushAsync("{a}:credential-block", "value");
            _ = await blocked.WaitAsync(TimeSpan.FromSeconds(5));
            await UntilAsync(async () =>
            {
                var current = await ConnectionsAsync(administrators);
                return current.Count == initial.Count && current.All(pair => initial.ContainsKey(pair.Key) && pair.Value == "auth");
            });
            client.ClientSideCache.Count.Should().Be(0);
            client.ClientSideCache.GetStatistics().ContinuityFlushes.Should().BeGreaterThanOrEqualTo(2);
            foreach (var key in new[] { "{a}:credential", "{b}:credential", "{c}:credential" })
                (await client.GetStringAsync(key)).Should().Be("before");
            await using (var writer = await RespireClient.ConnectAsync(fixture.CreateOptions()))
                await writer.SetAsync("{c}:credential", "after");
            await UntilAsync(() => Task.FromResult(client.ClientSideCache.Count < 3));
            (await client.GetStringAsync("{c}:credential")).Should().Be("after");
            var after = messages.MoveNextAsync().AsTask();
            (await client.PublishAsync("credential-channel", "after")).Should().Be(1);
            (await after).Should().BeTrue();
            messages.Current.Text.Should().Be("after");
            (await ConnectionsAsync(administrators)).Keys.Should().BeEquivalentTo(initial.Keys);

            var active = await ConnectionsAsync(administrators);
            var pooled = initial.First(pair => pair.Value is "get" or "publish");
            var identity = pooled.Key.Split(':');
            var reconnectCalls = data.Calls;
            using (var killed = await administrators[int.Parse(identity[0])].ExecuteAsync("CLIENT", "KILL", "ID", identity[1]))
                killed.AsInteger().Should().Be(1);
            await UntilAsync(async () =>
            {
                var current = await ConnectionsAsync(administrators);
                return data.Calls > reconnectCalls && current.Count == active.Count && !current.ContainsKey(pooled.Key);
            });
            (await client.GetStringAsync("{a}:credential")).Should().Be("before");

            // Fresh discovery and data sockets must fetch the rotated credentials too.
            var calls = data.Calls;
            var sentinelCalls = discovery.Calls;
            await using var replacement = await RespireClient.ConnectAsync(options with { ClientName = "replacement" });
            (await replacement.GetStringAsync("{a}:credential")).Should().Be("before");
            data.Calls.Should().BeGreaterThan(calls);
            if (topology == RespireContainerTopology.Sentinel) discovery.Calls.Should().BeGreaterThan(sentinelCalls);
        }
        finally
        {
            foreach (var admin in sentinelAdministrators) await admin.DisposeAsync();
            foreach (var admin in administrators) await admin.DisposeAsync();
        }
    }

    private static async Task SetPasswordAsync(RespireClient admin, string username, string password)
    {
        using var result = await admin.ExecuteAsync("ACL", "SETUSER", username, "on", "resetpass", ">" + password,
            "~*", "&*", "+@all");
        result.AsString().Should().Be("OK");
    }

    private static async Task<Dictionary<string, string>> ConnectionsAsync(List<RespireClient> administrators)
    {
        var connections = new Dictionary<string, string>();
        for (var index = 0; index < administrators.Count; index++)
        {
            using var result = await administrators[index].ExecuteAsync("CLIENT", "LIST");
            foreach (var line in result.AsString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(field => field.Split('=', 2)).ToDictionary(field => field[0], field => field[1]);
                if (fields["name"] == "credential-rotation") connections.Add(index + ":" + fields["id"], fields["cmd"]);
            }
        }
        return connections;
    }

    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class Provider(RespireCredentials credentials) : IRespireCredentialProvider
    {
        public volatile RespireCredentials Current = credentials;
        public int Calls;
        public ValueTask<RespireCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return ValueTask.FromResult(Current);
        }
    }
}
