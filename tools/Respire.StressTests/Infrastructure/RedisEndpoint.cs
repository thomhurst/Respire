using DotNet.Testcontainers.Containers;
using Testcontainers.Redis;

namespace Respire.StressTests.Infrastructure;

/// <summary>
/// The Redis endpoint under test: REDIS_HOST / REDIS_PORT when set (as in CI, where a
/// Redis service container is provided), otherwise a throwaway Redis Testcontainer
/// started for the duration of the run.
/// </summary>
internal sealed class RedisEndpoint : IAsyncDisposable
{
    private readonly IContainer? _container;

    private RedisEndpoint(string host, int port, IContainer? container)
    {
        Host = host;
        Port = port;
        _container = container;
    }

    public string Host { get; }
    public int Port { get; }

    public static async Task<RedisEndpoint> CreateAsync()
    {
        var host = Environment.GetEnvironmentVariable("REDIS_HOST");
        if (!string.IsNullOrEmpty(host))
        {
            var port = int.TryParse(Environment.GetEnvironmentVariable("REDIS_PORT"), out var p) ? p : 6379;
            return new RedisEndpoint(host, port, container: null);
        }

        var container = new RedisBuilder("redis:8.0").Build();
        await container.StartAsync().ConfigureAwait(false);
        return new RedisEndpoint("localhost", container.GetMappedPublicPort(6379), container);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }
}
