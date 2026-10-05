using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using TUnit.Core;
using TUnit.Core.Interfaces;

namespace Respire.IntegrationTests;

public sealed class StreamConfigurationRedisContainer : IAsyncInitializer, IAsyncDisposable
{
    private readonly IContainer _container = new ContainerBuilder("redis:8.10-alpine")
        .WithPortBinding(6379, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build();
    public string ConnectionString => $"redis://{_container.Hostname}:{_container.GetMappedPublicPort(6379)}";
    public Task InitializeAsync() => _container.StartAsync();
    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

[ClassDataSource<StreamConfigurationRedisContainer>(Shared = SharedType.PerTestSession)]
public class StreamConfigurationIntegrationTests(StreamConfigurationRedisContainer fixture)
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ConfiguredRetentionEventuallyExpiresIdentities(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });
        var key = $"configuration-expiry:{Guid.NewGuid():N}";
        try
        {
            await client.Streams.AddAsync(key, ("f", "seed"));
            await client.Streams.ConfigureAsync(key, new() { IdempotencyDurationSeconds = 1 });
            var options = new StreamAddOptions { Idempotency = StreamIdempotency.Manual("producer", "message") };
            var first = await client.Streams.AddAsync(key, options, ("f", "v"));
            (await client.Streams.AddAsync(key, options, ("f", "duplicate"))).Should().Be(first);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            RespireStreamId? next;
            do
            {
                await Task.Delay(100, timeout.Token);
                next = await client.Streams.AddAsync(key, options, [("f", "after expiry")], timeout.Token);
            } while (next == first);
            (await client.Streams.CountAsync(key)).Should().Be(3);
        }
        finally { await client.Keys.DeleteAsync(key); }
    }

    [Test]
    [MatrixDataSource]
    public async Task SettingsControlDeduplicationAcrossSurfaces([Matrix(2, 3)] int protocol,
        [Matrix(0, 1, 2)] int surface, [Matrix(false, true)] bool fake)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(
            (fake ? server.CreateOptions() : RespireOptions.Parse(fixture.ConnectionString)) with { Protocol = (RespProtocol)protocol });
        var prefix = $"configuration:{Guid.NewGuid():N}:";
        var view = client.WithKeyPrefix(prefix);
        var identity = new StreamAddOptions { Idempotency = StreamIdempotency.Manual(new byte[] { 0, 255 }, new byte[] { 254, 0 }) };
        try
        {
            var first = await view.Streams.AddAsync("s", identity, ("f", "first"));
            await Configure(view, surface, new() { IdempotencyDurationSeconds = 100, IdempotencyMaxSize = 100 });
            (await view.Streams.AddAsync("s", identity, ("f", "duplicate"))).Should().Be(first);
            await Configure(view, surface, new() { IdempotencyDurationSeconds = 86400 });
            var changed = await view.Streams.AddAsync("s", identity, ("f", "changed"));
            changed.Should().NotBe(first);
            await Configure(view, surface, new() { IdempotencyMaxSize = 1 });
            var reset = await view.Streams.AddAsync("s", identity, ("f", "reset"));
            reset.Should().NotBe(changed);
            (await view.Streams.AddAsync("s", identity, ("f", "duplicate"))).Should().Be(reset);
            await view.Streams.AddAsync("s", identity with { Idempotency = StreamIdempotency.Manual(new byte[] { 0, 255 }, "other") }, ("f", "evict"));
            (await view.Streams.AddAsync("s", identity, ("f", "evicted"))).Should().NotBe(reset);
            (await view.Streams.CountAsync("s")).Should().Be(5);
            if (!fake)
            {
                using var info = await client.ExecuteAsync(RespireCommands.Stream.XINFO_STREAM, prefix + "s");
                ReadNumber(info, "idmp-duration").Should().Be(86400);
                ReadNumber(info, "idmp-maxsize").Should().Be(1);
            }
            await view.SetAsync("wrong", "value");
            foreach (var key in new[] { "missing", "wrong" })
            {
                Func<Task> configure = async () => await Configure(view, surface, new() { IdempotencyMaxSize = 1 }, key);
                await configure.Should().ThrowAsync<RespireServerException>();
            }
            (await view.Keys.ExistsAsync("missing")).Should().BeFalse();
            (await view.GetStringAsync("wrong")).Should().Be("value");
        }
        finally { await view.Keys.DeleteAsync("s", "wrong", "missing"); }
    }

    private static long ReadNumber(RespireResult fields, string name)
    {
        for (var index = 0; index < fields.Count; index += 2)
            if (fields[index].AsString() == name) return fields[index + 1].AsInteger();
        throw new InvalidOperationException($"Missing XINFO field {name}.");
    }

    private static async Task Configure(IRespireClient client, int surface, StreamConfigurationOptions options, string key = "s")
    {
        if (surface == 0) { (await client.Streams.ConfigureAsync(key, options)).Should().BeTrue(); return; }
        using var batch = surface == 1 ? client.CreateBatch() : null;
        await using var transaction = surface == 2 ? client.CreateTransaction() : null;
        IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
        var pending = queue.Streams.Configure(key, options);
        if (transaction is not null) await transaction.CommitAsync();
        else await batch!.ExecuteAsync();
        pending.Result.Should().BeTrue();
    }
}
