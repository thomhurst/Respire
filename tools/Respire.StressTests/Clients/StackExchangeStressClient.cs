using StackExchange.Redis;

namespace Respire.StressTests.Clients;

internal sealed class StackExchangeStressClient : IStressClient
{
    public const string ClientName = "StackExchange.Redis";

    private readonly ConnectionMultiplexer _connection;
    private readonly IDatabase _db;

    private StackExchangeStressClient(ConnectionMultiplexer connection)
    {
        _connection = connection;
        _db = connection.GetDatabase();
    }

    public static async Task<StackExchangeStressClient> ConnectAsync(string host, int port) =>
        new(await ConnectionMultiplexer.ConnectAsync($"{host}:{port}").ConfigureAwait(false));

    public string Name => ClientName;

    public async ValueTask PingAsync() =>
        _ = await _db.PingAsync().ConfigureAwait(false);

    public async ValueTask<string?> GetStringAsync(string key) =>
        await _db.StringGetAsync(key).ConfigureAwait(false);

    public async ValueTask SetStringAsync(string key, string value) =>
        _ = await _db.StringSetAsync(key, value).ConfigureAwait(false);

    public async ValueTask<long> IncrementAsync(string key) =>
        await _db.StringIncrementAsync(key).ConfigureAwait(false);

    public async ValueTask HashSetAsync(string key, string field, string value) =>
        _ = await _db.HashSetAsync(key, field, value).ConfigureAwait(false);

    public async ValueTask<string?> HashGetAsync(string key, string field) =>
        await _db.HashGetAsync(key, field).ConfigureAwait(false);

    public async ValueTask ListLeftPushAsync(string key, string value) =>
        _ = await _db.ListLeftPushAsync(key, value).ConfigureAwait(false);

    public async ValueTask<string?> ListLeftPopAsync(string key) =>
        await _db.ListLeftPopAsync(key).ConfigureAwait(false);

    public async ValueTask DisposeAsync() =>
        await _connection.DisposeAsync().ConfigureAwait(false);
}
