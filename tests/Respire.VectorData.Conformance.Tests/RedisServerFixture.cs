using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Respire.VectorData.Conformance.Tests;

[CollectionDefinition(Name)]
public sealed class RedisConformanceCollection : ICollectionFixture<RedisServerFixture>
{
    public const string Name = "Official VectorData conformance";
}

public sealed class RedisServerFixture : IAsyncLifetime
{
    // Same Redis Query Engine image as the VectorData CI service.
    private const string Image = "mirror.gcr.io/library/redis:8.10-alpine@sha256:3811787313eba226a2ef38658c6ccb91cd5e110edc89c37767de373120a0e5a0";
    private IContainer? _container;

    public string ConnectionString { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var externalConnection = Environment.GetEnvironmentVariable("RESPIRE_VECTOR_CONFORMANCE_CONNECTION");
        if (!string.IsNullOrWhiteSpace(externalConnection))
        {
            ConnectionString = externalConnection;
            await AssertCapabilitiesAsync().ConfigureAwait(false);
            return;
        }

        _container = await Respire.Testing.ContainerStartRetry.StartAsync(() => new ContainerBuilder(Image)
            .WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379))
            .Build(), [6379]).ConfigureAwait(false);
        try
        {
            ConnectionString = $"redis://{_container.Hostname}:{_container.GetMappedPublicPort(6379)}";
            await AssertCapabilitiesAsync().ConfigureAwait(false);
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task AssertCapabilitiesAsync()
    {
        foreach (var protocol in new[] { RespireProtocol.Resp2, RespireProtocol.Resp3 })
        {
            await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(ConnectionString) with { Protocol = protocol }).ConfigureAwait(false);
            string[] commands = ["JSON.SET", "JSON.GET", "FT.CREATE", "FT.SEARCH", "FT.HYBRID", "FT.DROPINDEX", "FT._LIST", "HGETALL", "EVAL", "SCAN"];
            using var info = await client.ExecuteAsync(RespireCommands.Server.COMMAND_INFO,
                commands.Select(command => (RespireValue)command).ToArray()).ConfigureAwait(false);
            if (info.Count != commands.Length) throw new InvalidOperationException("COMMAND INFO returned incomplete capability metadata.");
            for (var i = 0; i < commands.Length; i++)
                if (info[i].IsNull) throw new NotSupportedException($"Official VectorData conformance requires {commands[i]} on {protocol}.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is { } container)
        {
            _container = null;
            await container.DisposeAsync().ConfigureAwait(false);
        }
        GC.SuppressFinalize(this);
    }
}
