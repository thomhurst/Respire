using System.Globalization;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Respire.Internal;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ParallelLimiter<DockerHeavy>]
public class ValkeyClusterScanIntegrationTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ResumableScanSelectsModernAndLegacyValkeyInOneRun(int protocol)
    {
        foreach (var image in new[] { "valkey/valkey:9.0-alpine", "valkey/valkey:9.1-alpine" })
        {
            await using var cluster = await OwnedCluster.StartAsync(image);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var token = deadline.Token;
            var options = new RespireOptions
            {
                UseCluster = true, Database = 1, Connections = 1, Endpoints = [cluster.Endpoint(0)],
                Protocol = protocol == 2 ? RespProtocol.Resp2 : RespProtocol.Resp3,
                ConnectTimeout = TimeSpan.FromSeconds(5), CommandTimeout = TimeSpan.FromSeconds(5),
                MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
            };
            byte[] prefix = [255, (byte)'*', 0];
            var expected = new[] { 0, 6000, 12000 }.Select(slot => $"{{{Tag(slot)}}}:selected").ToArray();
            RespireClusterScanCursor checkpoint;
            var found = new HashSet<string>(StringComparer.Ordinal);
            await using (var producer = await RespireClient.ConnectAsync(options, token))
            {
                var view = producer.WithKeyPrefix((RespireKey)prefix);
                foreach (var key in expected) await view.SetAsync(key, "selected-db", cancellationToken: token);
                await view.Lists.RightPushAsync($"{{{Tag(0)}}}:selected-list", ["wrong-type"], token);
                await producer.SetAsync($"outside:{{{Tag(0)}}}:selected", "wrong-prefix", cancellationToken: token);
                await using (var zero = await RespireClient.ConnectAsync(options with { Database = 0 }, token))
                    await zero.WithKeyPrefix((RespireKey)prefix).SetAsync($"{{{Tag(6000)}}}:selected", "wrong-db", cancellationToken: token);
                var evidence = await producer.Server.CommandInfoAsync([RespireCommands.Cluster.CLUSTERSCAN], token);
                (evidence[0] is not null).Should().Be(image.Contains("9.1", StringComparison.Ordinal));
                var first = await view.Keys.ScanClusterPageAsync(RespireClusterScanCursor.Start,
                    match: "*:selected", type: RespireKeyType.String, countHint: 1, cancellationToken: token);
                found.UnionWith(first.Keys);
                checkpoint = RespireClusterScanCursor.Parse(first.Cursor.ToString());
                // Modern Valkey bootstraps an opaque slot cursor; older Valkey finishes
                // a numeric primary pass, preserving the same public checkpoint API.
                (checkpoint.State!.ValkeyCursor is not null).Should().Be(image.Contains("9.1", StringComparison.Ordinal),
                    $"{image}: active={checkpoint.State.ActiveNode}, completed={checkpoint.CompletedSlotCount}, numeric={checkpoint.State.Cursor}");
            }
            await using var resumed = await RespireClient.ConnectAsync(options, token);
            var resumedView = resumed.WithKeyPrefix((RespireKey)prefix.ToArray());
            var cursor = checkpoint;
            while (!cursor.IsComplete)
            {
                var page = await resumedView.Keys.ScanClusterPageAsync(cursor, match: "*:selected",
                    type: RespireKeyType.String, countHint: 1, cancellationToken: token);
                found.UnionWith(page.Keys);
                cursor = RespireClusterScanCursor.Parse(page.Cursor.ToString());
            }
            found.Should().BeEquivalentTo(expected);
            // Fixed patterns must reach their owner without one bootstrap per preceding slot.
            var fixedView = resumed.WithKeyPrefix("fixed:");
            foreach (var (key, pattern) in new[]
            {
                ($"{{{Tag(16383)}}}:selected", $"{{{Tag(16383)}}}:*"),
                ("literal:" + Tag(16383), "literal:" + Tag(16383)),
            })
            {
                await fixedView.SetAsync(key, "fixed-match", cancellationToken: token);
                var fixedCursor = RespireClusterScanCursor.Start;
                var fixedFound = new HashSet<string>();
                for (var pages = 0; pages < 3 && !fixedCursor.IsComplete; pages++)
                {
                    var page = await fixedView.Keys.ScanClusterPageAsync(fixedCursor, pattern, countHint: 250,
                        cancellationToken: token);
                    fixedFound.UnionWith(page.Keys);
                    fixedCursor = RespireClusterScanCursor.Parse(page.Cursor.ToString());
                }
                fixedCursor.IsComplete.Should().BeTrue("a fixed physical pattern needs at most three pages");
                fixedFound.Should().BeEquivalentTo([key]);
            }
            await using var wrongDatabase = await RespireClient.ConnectAsync(options with { Database = 0 }, token);
            var mismatch = async () => await wrongDatabase.WithKeyPrefix((RespireKey)prefix).Keys.ScanClusterPageAsync(checkpoint,
                match: "*:selected", type: RespireKeyType.String, cancellationToken: token);
            await mismatch.Should().ThrowAsync<ArgumentException>();
        }
    }

    [Test]
    [Arguments("valkey/valkey:9.0-alpine", 2)]
    [Arguments("valkey/valkey:9.0-alpine", 3)]
    [Arguments("valkey/valkey:9.1-alpine", 2)]
    [Arguments("valkey/valkey:9.1-alpine", 3)]
    public async Task ServerCursorPreservesBinaryKeysDatabaseAndVersionBoundary(string image, int protocol)
    {
        await using var cluster = await OwnedCluster.StartAsync(image);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = deadline.Token;
        var options = new RespireOptions
        {
            UseCluster = true, Database = 1, Connections = 1, Endpoints = [cluster.Endpoint(0)],
            Protocol = protocol == 2 ? RespProtocol.Resp2 : RespProtocol.Resp3,
            ConnectTimeout = TimeSpan.FromSeconds(5), CommandTimeout = TimeSpan.FromSeconds(5),
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
        };
        await using var producer = await RespireClient.ConnectAsync(options, token);
        if (image.Contains("9.0", StringComparison.Ordinal))
        {
            var unsupported = async () => await producer.Keys.ScanValkeyClusterPageAsync(cancellationToken: token);
            await unsupported.Should().ThrowAsync<RespireServerException>().WithMessage("*unknown command*");
            // The old resumable client scan remains independently available.
            await producer.Keys.ScanClusterPageAsync(RespireClusterScanCursor.Start, cancellationToken: token);
            return;
        }

        const string prefix = "scan:*:";
        var view = producer.WithKeyPrefix(prefix);
        var slots = new[] { 0, 6000, 12000 };
        var keys = slots.Select(slot => new RespireKey((byte[])[.. Encoding.UTF8.GetBytes($"{{{Tag(slot)}}}:"), 255, 0])).ToArray();
        foreach (var key in keys) await view.SetAsync(key, "selected-db", cancellationToken: token);
        await view.Lists.RightPushAsync($"{{{Tag(0)}}}:list", ["not-a-string"], token);
        await producer.SetAsync($"outside:{{{Tag(0)}}}", "outside-view", cancellationToken: token);
        await using (var zero = await RespireClient.ConnectAsync(options with { Database = 0 }, token))
            await zero.WithKeyPrefix(prefix).SetAsync($"{{{Tag(6000)}}}:db-zero-only", "other-db", cancellationToken: token);

        var first = await view.Keys.ScanValkeyClusterPageAsync(type: RespireKeyType.String, countHint: 1, cancellationToken: token);
        first.IsComplete.Should().BeFalse();
        await producer.DisposeAsync();
        await using var resumed = await RespireClient.ConnectAsync(options, token);
        var resumedView = resumed.WithKeyPrefix(prefix);
        var found = new HashSet<RespireKey>(first.Keys);
        var cursor = first.Cursor;
        RespireValkeyClusterScanPage? retained = null;
        do
        {
            var page = await resumedView.Keys.ScanValkeyClusterPageAsync(cursor, type: RespireKeyType.String,
                countHint: 1, cancellationToken: token);
            foreach (var key in page.Keys) found.Add(key);
            if (page.Keys.Count != 0) retained = page;
            cursor = page.Cursor;
        } while (cursor != "0");
        found.Should().BeEquivalentTo(keys);

        var slotStart = await resumedView.Keys.ScanValkeyClusterPageAsync(type: RespireKeyType.String,
            countHint: 1, slot: 6000, cancellationToken: token);
        slotStart.IsComplete.Should().BeFalse();
        var mismatch = async () => await resumedView.Keys.ScanValkeyClusterPageAsync(slotStart.Cursor, slot: 0, cancellationToken: token);
        await mismatch.Should().ThrowAsync<RespireServerException>().WithMessage("*Cursor slot mismatch*");
        var slotKeys = new HashSet<RespireKey>(slotStart.Keys);
        cursor = slotStart.Cursor;
        do
        {
            var page = await resumedView.Keys.ScanValkeyClusterPageAsync(cursor, type: RespireKeyType.String,
                countHint: 1, slot: 6000, cancellationToken: token);
            foreach (var key in page.Keys) slotKeys.Add(key);
            cursor = page.Cursor;
        } while (cursor != "0");
        slotKeys.Should().BeEquivalentTo([keys[1]]);

        // All nodes use the same ACL: reconnecting or moving to another primary cannot widen database access.
        for (var node = 0; node < 3; node++)
            await cluster.CommandAsync(node, token, "ACL", "SETUSER", "scan-user", "on", ">scan-test-password",
                "+@all", "~*", "resetdbs", "db=1");
        var restrictedOptions = options with { Username = "scan-user", Password = "scan-test-password" };
        await using (var restricted = await RespireClient.ConnectAsync(restrictedOptions, token))
        {
            var restrictedPage = await restricted.Keys.ScanValkeyClusterPageAsync(slot: 6000, cancellationToken: token);
            restrictedPage.IsComplete.Should().BeFalse();
            (await restricted.Keys.ScanValkeyClusterPageAsync(restrictedPage.Cursor, slot: 6000, cancellationToken: token))
                .Keys.Should().NotBeEmpty();
        }
        var denied = async () =>
        {
            await using var forbidden = await RespireClient.ConnectAsync(restrictedOptions with { Database = 2 }, token);
            await forbidden.Keys.ScanValkeyClusterPageAsync(cancellationToken: token);
        };
        var denial = await denied.Should().ThrowAsync<RespireConnectionException>();
        denial.Which.ToString().Should().Contain("NOPERM");
        await resumed.DisposeAsync();
        retained.Should().NotBeNull();
        retained!.Keys.Should().OnlyContain(key => found.Contains(key));
    }

    private static string Tag(int slot) => Enumerable.Range(0, 1_000_000).Select(index => $"valkey-scan-{index}")
        .First(tag => ClusterHash.GetSlot(tag) == slot);

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task SurrogateNamespaceRoundTripsOwnedBinaryKeysAndDeferredPages(int protocol)
    {
        await using var cluster = await OwnedCluster.StartAsync("valkey/valkey:9.1-alpine");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = deadline.Token;
        var options = new RespireOptions
        {
            UseCluster = true, Database = 1, Connections = 1, Endpoints = [cluster.Endpoint(0)],
            Protocol = protocol == 2 ? RespProtocol.Resp2 : RespProtocol.Resp3,
        };
        await using var client = await RespireClient.ConnectAsync(options, token);
        var prefix = $"{{{Tag(0)}}}:tenant*\uD83D";
        var view = client.WithKeyPrefix(prefix);
        await view.SetAsync("\uDE00:joined", "joined", cancellationToken: token);
        await view.SetAsync("\uDE01:joined", "joined-second", cancellationToken: token);
        await view.SetAsync((byte[])[255, 0], "binary", cancellationToken: token);
        byte[] foreign = [.. Encoding.UTF8.GetBytes(prefix + "\uDE01"), 255, 0];
        await client.SetAsync(foreign, "foreign", cancellationToken: token);
        await client.SetAsync(prefix[..^1] + "\uD840\uDC00:outside", "outside", cancellationToken: token);
        var found = new List<RespireKey>();
        var cursor = "0";
        do
        {
            var page = await view.Keys.ScanValkeyClusterPageAsync(cursor, countHint: 1, cancellationToken: token);
            found.AddRange(page.Keys);
            cursor = page.Cursor;
        } while (cursor != "0");
        found.Should().HaveCount(4);
        new HashSet<RespireKey>(found).Should().HaveCount(4);
        var values = new List<string?>();
        foreach (var key in found) values.Add(await view.GetStringAsync(key, token));
        values.Should().BeEquivalentTo(["joined", "joined-second", "binary", "foreign"]);

        var batchKeys = new List<RespireKey>();
        cursor = "0";
        do
        {
            using var batch = view.CreateBatch();
            var page = batch.Keys.ScanValkeyClusterPage(cursor, match: "*joined", countHint: 1000, slot: 0);
            await batch.ExecuteAsync(token);
            batchKeys.AddRange(page.Result.Keys);
            cursor = page.Result.Cursor;
        } while (cursor != "0");
        batchKeys.Should().HaveCount(2);
        new HashSet<RespireKey>(batchKeys).Should().HaveCount(2);
        var batchValues = new List<string?>();
        foreach (var key in batchKeys) batchValues.Add(await view.GetStringAsync(key, token));
        batchValues.Should().BeEquivalentTo(["joined", "joined-second"]);
        var transactionKeys = new List<RespireKey>();
        cursor = "0";
        do
        {
            var transaction = view.CreateTransaction();
            var deferred = transaction.Keys.ScanValkeyClusterPage(cursor, match: "*joined", countHint: 1000, slot: 0);
            await transaction.CommitAsync(token);
            transactionKeys.AddRange(deferred.Result.Keys);
            cursor = deferred.Result.Cursor;
        } while (cursor != "0");
        transactionKeys.Should().HaveCount(2);
        new HashSet<RespireKey>(transactionKeys).Should().HaveCount(2);
        var transactionValues = new List<string?>();
        foreach (var key in transactionKeys) transactionValues.Add(await view.GetStringAsync(key, token));
        transactionValues.Should().BeEquivalentTo(["joined", "joined-second"]);
        await client.DisposeAsync();
        await using var resumed = await RespireClient.ConnectAsync(options, token);
        var resumedView = resumed.WithKeyPrefix(prefix);
        for (var index = 0; index < found.Count; index++)
            (await resumedView.GetStringAsync(found[index], token)).Should().Be(values[index]);
    }

    private sealed class OwnedCluster(IContainer container) : IAsyncDisposable
    {
        internal RespireEndpoint Endpoint(int index) => new(container.Hostname, container.GetMappedPublicPort(7000 + index));

        internal static async Task<OwnedCluster> StartAsync(string image)
        {
            var container = new ContainerBuilder(image)
                .WithPortBinding(7000, true).WithPortBinding(7001, true).WithPortBinding(7002, true)
                .WithEntrypoint("sh", "-c")
                .WithCommand("for port in 7000 7001 7002; do mkdir -p /data/$port; valkey-server --port $port --dir /data/$port --cluster-enabled yes --cluster-databases 4 --cluster-config-file nodes.conf --cluster-node-timeout 1000 --cluster-announce-ip 127.0.0.1 --appendonly no --protected-mode no & done; wait")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(7000)
                    .UntilInternalTcpPortIsAvailable(7001).UntilInternalTcpPortIsAvailable(7002)).Build();
            var cluster = new OwnedCluster(container);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await container.StartAsync(deadline.Token);
                for (var node = 0; node < 3; node++)
                {
                    await cluster.CommandAsync(node, deadline.Token, "CONFIG", "SET", "cluster-announce-port", cluster.Endpoint(node).Port.ToString(CultureInfo.InvariantCulture));
                    await cluster.CommandAsync(node, deadline.Token, "CLUSTER", "SET-CONFIG-EPOCH", (node + 1).ToString(CultureInfo.InvariantCulture));
                    await cluster.CommandAsync(node, deadline.Token, "CLUSTER", "ADDSLOTSRANGE",
                        (node * 5461).ToString(CultureInfo.InvariantCulture), (node == 2 ? 16383 : (node + 1) * 5461 - 1).ToString(CultureInfo.InvariantCulture));
                }
                await cluster.CommandAsync(0, deadline.Token, "CLUSTER", "MEET", "127.0.0.1", "7001");
                await cluster.CommandAsync(0, deadline.Token, "CLUSTER", "MEET", "127.0.0.1", "7002");
                for (var node = 0; node < 3; node++)
                    while (!(await cluster.CommandAsync(node, deadline.Token, "CLUSTER", "INFO")).Contains("cluster_state:ok", StringComparison.Ordinal))
                        await Task.Delay(50, deadline.Token);
                return cluster;
            }
            catch { await cluster.DisposeAsync(); throw; }
        }

        internal async Task<string> CommandAsync(int node, CancellationToken token, params string[] arguments)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var result = await container.ExecAsync(["valkey-cli", "-e", "--raw", "-p", (7000 + node).ToString(CultureInfo.InvariantCulture), .. arguments], deadline.Token);
            if (result.ExitCode != 0) throw new InvalidOperationException($"Owned cluster command failed: {result.Stdout} {result.Stderr}");
            return result.Stdout;
        }

        public ValueTask DisposeAsync() => container.DisposeAsync();
    }
}
