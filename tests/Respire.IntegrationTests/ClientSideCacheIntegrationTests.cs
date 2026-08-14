using StackExchange.Redis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class ClientSideCacheIntegrationTests(RedisTestContainer fixture)
{
    [Test]
    public async Task ExternalMutation_InvalidatesCachedValue()
    {
        await using var resources = await Resources.CreateAsync(fixture);
        await resources.Database.StringSetAsync("cache:key", "one");

        await Assert.That(await resources.Client.GetStringAsync("cache:key")).IsEqualTo("one");
        await resources.Database.StringSetAsync("cache:key", "two");
        await WaitForCacheEvictionAsync(resources.Client);

        await Assert.That(await resources.Client.GetStringAsync("cache:key")).IsEqualTo("two");
        await Assert.That(resources.Client.ClientSideCache!.GetStatistics().Hits).IsEqualTo(0);
    }

    [Test]
    public async Task MissingValue_IsCachedAndInvalidatedWhenCreated()
    {
        await using var resources = await Resources.CreateAsync(fixture);
        await resources.Database.KeyDeleteAsync("cache:missing");

        await Assert.That(await resources.Client.GetStringAsync("cache:missing")).IsNull();
        await Assert.That(await resources.Client.GetStringAsync("cache:missing")).IsNull();
        await resources.Database.StringSetAsync("cache:missing", "created");
        await WaitForCacheEvictionAsync(resources.Client);

        await Assert.That(await resources.Client.GetStringAsync("cache:missing")).IsEqualTo("created");
    }

    [Test]
    public async Task BinaryKeys_PreserveExactWireIdentity()
    {
        await using var resources = await Resources.CreateAsync(fixture);
        var first = new byte[] { 0xFF, 0x00, 0x01 };
        var second = new byte[] { 0xFF, 0x00, 0x02 };
        await resources.Database.StringSetAsync(first, "first");
        await resources.Database.StringSetAsync(second, "second");

        await Assert.That(await resources.Client.GetStringAsync(first)).IsEqualTo("first");
        await Assert.That(await resources.Client.GetStringAsync(second)).IsEqualTo("second");
        await Assert.That(await resources.Client.GetStringAsync(first)).IsEqualTo("first");
    }

    [Test]
    public async Task ConnectionLoss_FlushesBeforeReconnect()
    {
        var clientName = $"respire-cache-{Guid.NewGuid():N}";
        await using var resources = await Resources.CreateAsync(fixture, clientName);
        await resources.Database.StringSetAsync("cache:reconnect", "old");
        await resources.Client.GetStringAsync("cache:reconnect");

        var clientId = await FindClientIdAsync(resources.Database, clientName);
        await resources.Database.ExecuteAsync("CLIENT", "KILL", "ID", clientId);
        await WaitUntilAsync(() =>
            resources.Client.ClientSideCache!.GetStatistics().ContinuityFlushes > 0
            && resources.Client.IsConnected);
        await resources.Database.StringSetAsync("cache:reconnect", "new");

        await Assert.That(await resources.Client.GetStringAsync("cache:reconnect"))
            .IsEqualTo("new");
    }

    [Test]
    public async Task Disposal_ReleasesResidentEntries()
    {
        var resources = await Resources.CreateAsync(fixture);
        await resources.Database.StringSetAsync("cache:dispose", "value");
        await resources.Client.GetStringAsync("cache:dispose");
        var cache = resources.Client.ClientSideCache!;

        await resources.DisposeAsync();

        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(cache.SizeBytes).IsEqualTo(0);
    }

    private static async Task<long> FindClientIdAsync(IDatabase database, string clientName)
    {
        var list = (string?)await database.ExecuteAsync("CLIENT", "LIST") ?? string.Empty;
        foreach (var line in list.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!fields.Contains($"name={clientName}", StringComparer.Ordinal))
            {
                continue;
            }

            var id = fields.Single(static field =>
                field.StartsWith("id=", StringComparison.Ordinal)
                || field.StartsWith("txt:id=", StringComparison.Ordinal));
            return long.Parse(
                id.AsSpan(id.IndexOf('=') + 1),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        throw new InvalidOperationException($"Redis client '{clientName}' was not found.");
    }

    private static Task WaitForCacheEvictionAsync(RespireClient client)
        => WaitUntilAsync(() => client.ClientSideCache!.Count == 0);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class Resources : IAsyncDisposable
    {
        private Resources(RespireClient client, ConnectionMultiplexer multiplexer)
        {
            Client = client;
            Multiplexer = multiplexer;
            Database = multiplexer.GetDatabase();
        }

        public RespireClient Client { get; }
        public ConnectionMultiplexer Multiplexer { get; }
        public IDatabase Database { get; }

        public static async Task<Resources> CreateAsync(
            RedisTestContainer fixture,
            string? clientName = null)
        {
            var options = RespireOptions.Parse(fixture.ConnectionString) with
            {
                ClientName = clientName,
                ClientSideCache = new(),
            };
            var client = await RespireClient.ConnectAsync(options);
            var multiplexer = await ConnectionMultiplexer.ConnectAsync(
                fixture.StackExchangeConnectionString);
            return new Resources(client, multiplexer);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Multiplexer.DisposeAsync();
        }
    }
}
