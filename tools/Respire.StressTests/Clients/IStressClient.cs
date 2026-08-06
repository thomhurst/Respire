namespace Respire.StressTests.Clients;

/// <summary>
/// The common operation surface both clients are driven through, so every scenario
/// issues an identical workload regardless of which client is under test.
/// Values are always materialized as strings, as a typical caller would use them.
/// </summary>
internal interface IStressClient : IAsyncDisposable
{
    string Name { get; }

    ValueTask PingAsync();

    ValueTask<string?> GetStringAsync(string key);

    ValueTask SetStringAsync(string key, string value);

    ValueTask<long> IncrementAsync(string key);

    ValueTask HashSetAsync(string key, string field, string value);

    ValueTask<string?> HashGetAsync(string key, string field);

    ValueTask ListLeftPushAsync(string key, string value);

    ValueTask<string?> ListLeftPopAsync(string key);
}
