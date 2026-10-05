using Respire.TimeSeries;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.PerTestSession)]
public class TimeSeriesReadIntegrationTests(ModernRedisTestContainer fixture)
{
    // Command statistics are server-wide, so the rate assertion reads them on servers that only
    // serialized commandstats tests use; concurrent readers on the shared fixture cannot disturb it.
    [ClassDataSource<VersionedServerFixture>(Shared = SharedType.Keyed, Key = VersionedServerFixture.CommandStatsKey)]
    public required VersionedServerFixture CommandStatsServers { get; init; }

    [Test]
    [NotInParallel(VersionedServerFixture.CommandStatsKey)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task FollowingMissingKeyHasBoundedCommandCount(int protocol)
    {
        var server = await CommandStatsServers.LeaseAsync("redis:8.10-alpine");
        await using var client = await RespireClient.ConnectAsync(server.ConnectionString(protocol));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        // Ensure TS.READ is present in commandstats before measuring the follower. Redis may
        // leave its current blocked call out of the completed-command count until cancellation.
        await client.TimeSeries.ReadAsync("missing", 0, cancellationToken: timeout.Token);
        var before = await ReadCallCountAsync();
        await Assert.That(before).IsGreaterThan(0);
        await using var reader = client.TimeSeries.FollowAsync("missing", 0, cancellationToken: cancel.Token).GetAsyncEnumerator();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var next = reader.MoveNextAsync().AsTask();
        await Task.Delay(350, timeout.Token);
        cancel.Cancel();
        await Assert.That(async () => await next).Throws<OperationCanceledException>();
        var maximumCalls = (long)Math.Ceiling(elapsed.Elapsed.TotalMilliseconds / 100) + 1;
        var calls = await ReadCallCountAsync() - before;
        await Assert.That(calls).IsGreaterThanOrEqualTo(0);
        await Assert.That(calls).IsLessThanOrEqualTo(maximumCalls);

        async Task<long> ReadCallCountAsync()
        {
            var info = await client.Server.InfoAsync("commandstats", timeout.Token);
            var match = System.Text.RegularExpressions.Regex.Match(info, @"(?m)^cmdstat_ts\.read:calls=(\d+)");
            await Assert.That(match.Success).IsTrue();
            return long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task NewFollowerKeepsSamplesAddedAfterDeletion(int protocol)
    {
        var name = $"follow-recreate-{Guid.NewGuid():N}";
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString)
            with { Protocol = (RespProtocol)protocol, ClientName = name });
        await using var observer = await RespireClient.ConnectAsync(fixture.ConnectionString);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.TimeSeries.AddAsync(name, 10, 1, cancellationToken: timeout.Token);
        await using var reader = client.TimeSeries.FollowAsync(name, RespireTimeSeriesTimestamp.New,
            cancellationToken: timeout.Token).GetAsyncEnumerator();
        var next = reader.MoveNextAsync().AsTask();
        await WaitForBlockedAsync(observer, name, timeout.Token);
        using var deleted = await observer.ExecuteAsync("DEL", [name], cancellationToken: timeout.Token);
        await observer.TimeSeries.AddAsync(name, 20, 2, cancellationToken: timeout.Token);
        await Assert.That(await next).IsTrue();
        await Assert.That(reader.Current.Timestamp).IsEqualTo(20);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ExplicitRangesLabelsAndReadRoundTrip(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync($"{fixture.ConnectionString}?protocol={protocol}");
        var id = Guid.NewGuid().ToString("N");
        var view = client.WithKeyPrefix($"{{{id}}}:").TimeSeries;
        var options = new RespireTimeSeriesAddOptions { Labels = new Dictionary<string, string> { ["run"] = id, ["room"] = "kitchen" } };
        await view.AddAsync("a", 10, 1, options);
        await view.AddAsync("a", 20, 3);
        await view.AddAsync("b", 20, 2, options);
        var rows = await view.RangeKeysAsync(["a", "b", "a"], new(0, 100));
        await Assert.That(rows.Select(row => row.Timestamp).ToArray()).IsEquivalentTo([10L, 20L]);
        await Assert.That(double.IsNaN(rows[0].Values[1])).IsTrue();
        await Assert.That(rows[1].Values).IsEquivalentTo([3.0, 2.0, 3.0]);
        var reversed = await view.ReverseRangeKeysAsync(["a", "b"], new(0, 100), new()
        {
            Aggregators = [[RespireTimeSeriesAggregation.Min, RespireTimeSeriesAggregation.Max], [RespireTimeSeriesAggregation.Sum]],
            BucketMilliseconds = 100,
        });
        await Assert.That(reversed.Single().Values).IsEquivalentTo([1.0, 3.0, 2.0]);
        await Assert.That(await client.TimeSeries.QueryLabelsAsync([$"run={id}"])).IsEquivalentTo(["room", "run"]);
        await Assert.That(await client.TimeSeries.QueryLabelValuesAsync("room", [$"run={id}"])).IsEquivalentTo(["kitchen"]);
        await Assert.That((await view.ReadAsync("a", 10, new() { MaximumCount = 1 })).Samples.Single().Timestamp).IsEqualTo(10);
        await Assert.That((await view.ReadAsync("a", RespireTimeSeriesTimestamp.Maximum)).Samples.Single().Timestamp).IsEqualTo(20);
        await Assert.That((await view.ReadAsync("a", 21, new() { BlockMilliseconds = 1 })).Samples).IsEmpty();
        await Assert.That((await view.ReadAsync("missing", 0)).Samples).IsEmpty();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task FollowingKeepsOrdinaryTrafficResponsiveAndCancellationReleasesConnection(int protocol)
    {
        var name = $"ts-follow-{Guid.NewGuid():N}";
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with
        { Protocol = (RespProtocol)protocol, Connections = 1, ClientName = name });
        await using var observer = await RespireClient.ConnectAsync(fixture.ConnectionString);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await client.TimeSeries.AddAsync(name, 10, 1);
        await using var reader = client.TimeSeries.FollowAsync(name, RespireTimeSeriesTimestamp.New,
            cancellationToken: cancel.Token).GetAsyncEnumerator();
        var next = reader.MoveNextAsync().AsTask();
        await WaitForBlockedAsync(observer, name, timeout.Token);
        await client.Strings.SetAsync($"{name}:ordinary", (RespireValue)"responsive", cancellationToken: timeout.Token);
        await Assert.That(next.IsCompleted).IsFalse();
        await client.TimeSeries.AddAsync(name, 20, 2, cancellationToken: timeout.Token);
        await Assert.That(await next).IsTrue();
        await Assert.That(reader.Current.Timestamp).IsEqualTo(20);
        var canceled = reader.MoveNextAsync().AsTask();
        await WaitForBlockedAsync(observer, name, timeout.Token);
        cancel.Cancel();
        await Assert.That(async () => await canceled).Throws<OperationCanceledException>();
        var reread = await client.TimeSeries.ReadAsync(name, 10, new() { BlockMilliseconds = 1 }, timeout.Token);
        await Assert.That(reread.Samples.Count).IsEqualTo(2);
    }

    private static async Task WaitForBlockedAsync(IRespireClient observer, string name, CancellationToken cancellationToken)
    {
        while (true)
        {
            var clients = await observer.Server.ClientsAsync(cancellationToken);
            if (clients.Any(client => client.Name == name && client.Flags.Contains('b') && client.Command == "ts.read")) return;
            await Task.Delay(10, cancellationToken);
        }
    }
}
