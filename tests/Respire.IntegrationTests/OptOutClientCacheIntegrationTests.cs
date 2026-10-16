using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.Keyed, Key = TestConstraints.ClientCacheServer)]
[NotInParallel(TestConstraints.ClientCacheHits)]
public class OptOutClientCacheIntegrationTests(RedisTestContainer fixture)
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExternalInvalidationAndReconnectPreserveOptOutTracking(bool noLoop)
    {
        var key = $"optout:{Guid.NewGuid():N}";
        var options = RespireOptions.Parse(fixture.ConnectionString) with { AllowAdmin = true, Connections = 1 };
        await using var writer = await RespireClient.ConnectAsync(options);
        await writer.SetAsync(key, "old");
        await using var reader = await RespireClient.ConnectAsync(options with
        {
            ClientSideCache = new() { TrackingMode = RespireClientTrackingMode.OptOut, NoLoop = noLoop },
        });
        await AssertTrackingAsync(reader, noLoop);
        await Assert.That(await reader.GetStringAsync(key)).IsEqualTo("old");
        var hits = reader.ClientSideCache!.GetStatistics().Hits;
        await Assert.That(await reader.GetStringAsync(key)).IsEqualTo("old");
        await Assert.That(reader.ClientSideCache.GetStatistics().Hits).IsEqualTo(hits + 1);
        await writer.SetAsync(key, "new");
        await BroadcastClientCacheIntegrationTests.UntilAsync(() => reader.ClientSideCache.Count == 0);
        await Assert.That(await reader.GetStringAsync(key)).IsEqualTo("new");

        var connection = await reader.Server.GetClientConnectionAsync();
        var flushes = reader.ClientSideCache.GetStatistics().ContinuityFlushes;
        using var killed = await writer.ExecuteAsync("CLIENT", "KILL", "ID", connection.Id);
        await BroadcastClientCacheIntegrationTests.UntilAsync(
            () => reader.ClientSideCache.GetStatistics().ContinuityFlushes > flushes && reader.IsConnected);
        await AssertTrackingAsync(reader, noLoop);
        await writer.SetAsync(key, "after reconnect");
        await Assert.That(await reader.GetStringAsync(key)).IsEqualTo("after reconnect");
        await writer.SetAsync(key, "tracking restored");
        await BroadcastClientCacheIntegrationTests.UntilAsync(() => reader.ClientSideCache.Count == 0);
        await Assert.That(await reader.GetStringAsync(key)).IsEqualTo("tracking restored");
    }

    [Test]
    [Arguments(RespireClientTrackingMode.OptIn)]
    [Arguments(RespireClientTrackingMode.Broadcast)]
    [Arguments(RespireClientTrackingMode.OptOut)]
    public async Task NoLoopStillEvictsLocalWritesIncludingBypassViews(RespireClientTrackingMode mode)
    {
        var key = $"noloop:{Guid.NewGuid():N}";
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with
        {
            AllowAdmin = true,
            Connections = 1,
            ClientSideCache = new() { TrackingMode = mode, NoLoop = true },
        });
        var connection = await client.Server.GetClientConnectionAsync();
        await Assert.That((await connection.TrackingInfoAsync()).Flags.Contains("noloop")).IsTrue();
        await client.SetAsync(key, "old");
        await Assert.That(await client.GetStringAsync(key)).IsEqualTo("old");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(1);
        await client.SetAsync(key, "new");
        await Assert.That(client.ClientSideCache.Count).IsEqualTo(0);
        await Assert.That(await client.GetStringAsync(key)).IsEqualTo("new");
        await client.WithoutClientCache().SetAsync(key, "bypass write");
        await Assert.That(client.ClientSideCache.Count).IsEqualTo(0);
        await Assert.That(await client.GetStringAsync(key)).IsEqualTo("bypass write");
    }

    [Test]
    [Arguments("typed")]
    [Arguments("raw")]
    [Arguments("stream")]
    [Arguments("batch")]
    [Arguments("transaction")]
    [Arguments("transaction-retry")]
    [Arguments("watched-transaction")]
    [Arguments("fire-and-forget")]
    public async Task BypassDoesNotPopulateOrRegisterFreshKeysButKeepsPriorTracking(string surface)
    {
        var prefix = $"optout-bypass:{Guid.NewGuid():N}:";
        var freshKey = prefix + "fresh";
        var trackedKey = prefix + "tracked";
        var options = RespireOptions.Parse(fixture.ConnectionString) with { AllowAdmin = true, Connections = 1 };
        await using var writer = await RespireClient.ConnectAsync(options);
        await writer.SetAsync(freshKey, "fresh");
        await writer.SetAsync(trackedKey, "tracked");
        await using var reader = await RespireClient.ConnectAsync(options with
        {
            ClientSideCache = new() { TrackingMode = RespireClientTrackingMode.OptOut },
            RetryPolicy = surface == "transaction-retry" ? new() { MaxAttempts = 2 } : null,
        });
        await Assert.That(await reader.GetStringAsync(trackedKey)).IsEqualTo("tracked");
        var hits = reader.ClientSideCache!.GetStatistics().Hits;
        var bypass = reader.WithoutClientCache();
        if (surface == "raw")
        {
            using var fresh = await bypass.ExecuteAsync(RespireCommands.String.GET, freshKey);
            using var tracked = await bypass.ExecuteAsync(RespireCommands.String.GET, trackedKey);
            await Assert.That(fresh.AsString()).IsEqualTo("fresh");
            await Assert.That(tracked.AsString()).IsEqualTo("tracked");
        }
        else if (surface == "stream")
        {
            foreach (var key in new[] { freshKey, trackedKey })
            {
                await using var stream = await bypass.Strings.GetStreamAsync(key);
                using var text = new StreamReader(stream!);
                await Assert.That(await text.ReadToEndAsync()).IsEqualTo(key == freshKey ? "fresh" : "tracked");
            }
        }
        else if (surface == "batch")
        {
            using var batch = bypass.CreateBatch();
            var fresh = batch.GetString(freshKey);
            var tracked = batch.GetString(trackedKey);
            await batch.ExecuteAsync();
            await Assert.That(fresh.Result).IsEqualTo("fresh");
            await Assert.That(tracked.Result).IsEqualTo("tracked");
        }
        else if (surface.StartsWith("transaction", StringComparison.Ordinal))
        {
            await using var transaction = bypass.CreateTransaction();
            var fresh = transaction.GetString(freshKey);
            _ = transaction.Set(prefix + "written", "write");
            var tracked = transaction.GetString(trackedKey);
            await transaction.CommitAsync();
            await Assert.That(fresh.Result).IsEqualTo("fresh");
            await Assert.That(tracked.Result).IsEqualTo("tracked");
        }
        else if (surface == "watched-transaction")
        {
            await using var transaction = await bypass.CreateTransactionAsync([freshKey]);
            var fresh = transaction.GetString(freshKey);
            var tracked = transaction.GetString(trackedKey);
            await Assert.That(await transaction.CommitAsync()).IsTrue();
            await Assert.That(fresh.Result).IsEqualTo("fresh");
            await Assert.That(tracked.Result).IsEqualTo("tracked");
        }
        else if (surface == "fire-and-forget")
        {
            await bypass.ExecuteFireAndForgetAsync(RespireCommands.String.GET, freshKey);
            await bypass.ExecuteFireAndForgetAsync(RespireCommands.String.GET, trackedKey);
            await reader.PingAsync();
        }
        else
        {
            await Assert.That(await bypass.GetStringAsync(freshKey)).IsEqualTo("fresh");
            await Assert.That(await bypass.GetStringAsync(trackedKey)).IsEqualTo("tracked");
        }
        // Deferred units conservatively fence the whole cache, including read-only units.
        var fenced = surface == "batch" || surface.Contains("transaction", StringComparison.Ordinal);
        await Assert.That(reader.ClientSideCache.Count).IsEqualTo(fenced ? 0 : 1);
        await Assert.That(reader.ClientSideCache.GetStatistics().Hits).IsEqualTo(hits);
        if (fenced)
            await Assert.That(await reader.GetStringAsync(trackedKey)).IsEqualTo("tracked");
        var invalidations = reader.ClientSideCache.GetStatistics().Invalidations;
        await writer.SetAsync(freshKey, "changed fresh");
        await writer.SetAsync(trackedKey, "changed tracked");
        // The second write is a positive barrier on the same reader connection. Its push must
        // follow any invalidation caused by the first write; no negative timing assertion is needed.
        await BroadcastClientCacheIntegrationTests.UntilAsync(() => reader.ClientSideCache.Count == 0);
        var connection = await reader.Server.GetClientConnectionAsync();
        await connection.EchoAsync("processed"u8.ToArray());
        await Assert.That(reader.ClientSideCache.GetStatistics().Invalidations).IsEqualTo(invalidations + 1);
        await Assert.That(await reader.GetStringAsync(trackedKey)).IsEqualTo("changed tracked");
    }

    private static async Task AssertTrackingAsync(RespireClient client, bool noLoop)
    {
        var connection = await client.Server.GetClientConnectionAsync();
        var flags = (await connection.TrackingInfoAsync()).Flags;
        await Assert.That(flags.Contains("on")).IsTrue();
        await Assert.That(flags.Contains("optout")).IsTrue();
        await Assert.That(flags.Contains("noloop")).IsEqualTo(noLoop);
    }
}
