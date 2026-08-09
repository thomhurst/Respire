namespace Respire.StressTests.Clients;

internal sealed class RespireStressClient : IStressClient
{
    public const string ClientName = "Respire";

    private readonly RespireClient _client;

    private RespireStressClient(RespireClient client) => _client = client;

    public static async Task<RespireStressClient> ConnectAsync(string host, int port)
    {
        // RESPIRE_CONNECTIONS overrides the multiplexed connection count and
        // RESPIRE_DEDICATED_IO=1 switches to dedicated blocking IO threads, for perf sweeps.
        var options = new RespireOptions
        {
            Endpoints = { new RespireEndpoint(host, port) },
            Connections = int.TryParse(
                Environment.GetEnvironmentVariable("RESPIRE_CONNECTIONS"), out var connections)
                ? connections
                : 0,
            DedicatedIoThreads = Environment.GetEnvironmentVariable("RESPIRE_DEDICATED_IO") == "1",
        };
        return new(await RespireClient.ConnectAsync(options).ConfigureAwait(false));
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
