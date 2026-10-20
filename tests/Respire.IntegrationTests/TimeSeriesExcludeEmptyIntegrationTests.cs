using Respire.TimeSeries;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ClassDataSource<VersionedServerFixture>(Shared = SharedType.PerTestSession)]
public class TimeSeriesExcludeEmptyIntegrationTests(VersionedServerFixture servers)
{
    [Test]
    [Arguments("redis:8.8-alpine", 2, false)]
    [Arguments("redis:8.8-alpine", 3, true)]
    [Arguments("redis:8.10-alpine", 2, false)]
    [Arguments("redis:8.10-alpine", 2, true)]
    [Arguments("redis:8.10-alpine", 3, false)]
    [Arguments("redis:8.10-alpine", 3, true)]
    public async Task ExcludesEmptySeriesButRetainsNaN(string image, int protocol, bool reverse)
    {
        var server = await servers.LeaseAsync(image);
        await using var client = await RespireClient.ConnectAsync(server.ConnectionString(protocol));
        var info = await client.Server.InfoAsync("server");
        var versionText = info.Split('\n').Single(line => line.StartsWith("redis_version:", StringComparison.Ordinal))
            .Split(':')[1].Trim();
        Skip.When(Version.Parse(versionText) < new Version(8, 10), "EXCLUDEEMPTY requires Redis 8.10.");

        var timeSeries = client.TimeSeries;
        var id = Guid.NewGuid().ToString("N");
        var labels = new RespireTimeSeriesOptions { Labels = new Dictionary<string, string> { ["run"] = id } };
        var empty = $"{id}:empty";
        var outside = $"{id}:outside";
        var values = $"{id}:values";
        var nan = $"{id}:nan";
        foreach (var key in new[] { empty, outside, values, nan }) await timeSeries.CreateAsync(key, labels);
        await timeSeries.AddAsync(outside, 200, 2);
        await timeSeries.AddAsync(values, 10, 1);
        await timeSeries.AddAsync(values, 20, 2);
        await timeSeries.AddAsync(nan, 10, double.NaN);

        var options = new RespireTimeSeriesRangeOptions { Filters = [$"run={id}"], WithLabels = true };
        var all = await ReadAsync(options);
        await Assert.That(all.Count).IsEqualTo(4);
        await Assert.That(all.Single(series => series.Key == empty).Samples).IsEmpty();
        await Assert.That(all.Single(series => series.Key == outside).Samples).IsEmpty();

        var nonempty = await ReadAsync(options with { ExcludeEmpty = true });
        await Assert.That(nonempty.Select(series => series.Key)).IsEquivalentTo([values, nan]);
        await Assert.That(double.IsNaN(nonempty.Single(series => series.Key == nan).Samples.Single().Value)).IsTrue();
        await Assert.That(nonempty.Single(series => series.Key == values).Samples[0].Timestamp).IsEqualTo(reverse ? 20L : 10L);
        await Assert.That(nonempty.All(series => series.Labels["run"] == id)).IsTrue();

        ValueTask<IReadOnlyList<RespireTimeSeriesSeries>> ReadAsync(RespireTimeSeriesRangeOptions value)
            => reverse ? timeSeries.MultiReverseRangeAsync(new(0, 100), value) : timeSeries.MultiRangeAsync(new(0, 100), value);
    }
}
