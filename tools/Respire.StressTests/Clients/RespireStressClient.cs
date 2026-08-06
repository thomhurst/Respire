namespace Respire.StressTests.Clients;

internal sealed class RespireStressClient : IStressClient
{
    public const string ClientName = "Respire";

    private readonly RespireClient _client;

    private RespireStressClient(RespireClient client) => _client = client;

    public static async Task<RespireStressClient> ConnectAsync(string host, int port)
    {
        // RESPIRE_CONNECTIONS overrides the multiplexed connection count for perf sweeps.
        var connections = Environment.GetEnvironmentVariable("RESPIRE_CONNECTIONS");
        var connectionString = string.IsNullOrEmpty(connections)
            ? $"{host}:{port}"
            : $"redis://{host}:{port}?connections={connections}";
        return new(await RespireClient.ConnectAsync(connectionString).ConfigureAwait(false));
    }

    public string Name => ClientName;

    public async ValueTask PingAsync() =>
        _ = await _client.PingAsync().ConfigureAwait(false);

    public ValueTask<string?> GetStringAsync(string key) => _client.GetStringAsync(key);

    public async ValueTask SetStringAsync(string key, string value) =>
        _ = await _client.SetAsync(key, value).ConfigureAwait(false);

    public ValueTask<long> IncrementAsync(string key) => _client.IncrementAsync(key);

    public async ValueTask HashSetAsync(string key, string field, string value) =>
        _ = await _client.Hashes.SetAsync(key, field, value).ConfigureAwait(false);

    public ValueTask<string?> HashGetAsync(string key, string field) =>
        _client.Hashes.GetStringAsync(key, field);

    public async ValueTask ListLeftPushAsync(string key, string value) =>
        _ = await _client.Lists.LeftPushAsync(key, value).ConfigureAwait(false);

    public ValueTask<string?> ListLeftPopAsync(string key) => _client.Lists.LeftPopAsync(key);

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
