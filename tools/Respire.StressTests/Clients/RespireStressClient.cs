using Respire.FastClient;

namespace Respire.StressTests.Clients;

internal sealed class RespireStressClient : IStressClient
{
    public const string ClientName = "Respire";

    private readonly RespireClient _client;

    private RespireStressClient(RespireClient client) => _client = client;

    public static async Task<RespireStressClient> ConnectAsync(string host, int port) =>
        new(await RespireClient.CreateAsync(host, port).ConfigureAwait(false));

    public string Name => ClientName;

    public async ValueTask PingAsync() =>
        _ = await _client.PingAsync().ConfigureAwait(false);

    public async ValueTask<string?> GetStringAsync(string key)
    {
        var value = await _client.GetAsync(key).ConfigureAwait(false);
        var result = value.AsString();
        value.Dispose();
        return result;
    }

    public ValueTask SetStringAsync(string key, string value) => _client.SetAsync(key, value);

    public ValueTask<long> IncrementAsync(string key) => _client.IncrAsync(key);

    public async ValueTask HashSetAsync(string key, string field, string value) =>
        _ = await _client.HSetAsync(key, field, value).ConfigureAwait(false);

    public async ValueTask<string?> HashGetAsync(string key, string field)
    {
        var value = await _client.HGetAsync(key, field).ConfigureAwait(false);
        var result = value.AsString();
        value.Dispose();
        return result;
    }

    public async ValueTask ListLeftPushAsync(string key, string value) =>
        _ = await _client.LPushAsync(key, value).ConfigureAwait(false);

    public async ValueTask<string?> ListLeftPopAsync(string key)
    {
        var value = await _client.LPopAsync(key).ConfigureAwait(false);
        var result = value.AsString();
        value.Dispose();
        return result;
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
