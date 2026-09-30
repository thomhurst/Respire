using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using TUnit.Core;
using TUnit.Core.Interfaces;

namespace Respire.IntegrationTests;

public sealed class StreamProductionRedisContainer : IAsyncInitializer, IAsyncDisposable
{
    private readonly IContainer _container = new ContainerBuilder("redis:8.6-alpine")
        .WithPortBinding(6379, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build();
    public string ConnectionString => $"redis://{_container.Hostname}:{_container.GetMappedPublicPort(6379)}";
    public Task InitializeAsync() => _container.StartAsync();
    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

[ClassDataSource<StreamProductionRedisContainer>(Shared = SharedType.PerTestSession)]
public class StreamIdempotencyIntegrationTests(StreamProductionRedisContainer fixture)
{
    [Test]
    [MatrixDataSource]
    public async Task DuplicatesReturnOriginalOwnedIdsAcrossSurfaces([Matrix(2, 3)] int protocol, [Matrix(0, 1, 2)] int surface)
    {
        await using var client = await RespireClient.ConnectAsync($"{fixture.ConnectionString}?protocol={protocol}");
        var view = client.WithKeyPrefix($"production:{Guid.NewGuid():N}:");
        try
        {
            byte[] producer = [0, 255, 32], message = [254, 0];
            var manual = new StreamAddOptions { Idempotency = StreamIdempotency.Manual(producer, message) };
            var automatic = new StreamAddOptions { Idempotency = StreamIdempotency.Automatic(producer) };
            Array.Fill(producer, (byte)'x'); Array.Fill(message, (byte)'x');
            StreamAddOptions[] options = [manual, manual, manual with { Idempotency = StreamIdempotency.Manual(new byte[] { 0, 255, 32 }, "distinct") },
                manual with { Idempotency = StreamIdempotency.Manual("other-producer", new byte[] { 254, 0 }) }, automatic, automatic, automatic];
            string[] values = ["first", "ignored", "first", "first", "auto", "auto", "changed"];
            List<RespireStreamId?> ids = [];
            using var batch = surface == 1 ? view.CreateBatch() : null;
            await using var transaction = surface == 2 ? view.CreateTransaction() : null;
            IRespireCommandQueue? queue = transaction ?? (IRespireCommandQueue?)batch;
            List<RespirePending<RespireStreamId?>> pending = [];
            for (var index = 0; index < options.Length; index++)
            {
                if (queue is null) ids.Add(await view.Streams.AddAsync("events", options[index], ("f", values[index])));
                else pending.Add(queue.Streams.Add("events", options[index], ("f", values[index])));
            }
            if (transaction is not null) await transaction.CommitAsync();
            else if (batch is not null) await batch.ExecuteAsync();
            ids.AddRange(pending.Select(x => x.Result));
            ids.Should().NotContainNulls();
            ids[1].Should().Be(ids[0]);
            ids[5].Should().Be(ids[4]);
            ids.Distinct().Should().HaveCount(5);
            (await view.Streams.CountAsync("events")).Should().Be(5);
            var entries = await view.Streams.RangeAsync("events");
            entries[0].GetString("f").Should().Be("first");
            (await view.Streams.AddAsync("missing", manual with { CreateStream = false }, ("f", "v"))).Should().BeNull();
            // The 8.6 compatibility floor supports production but deliberately rejects XNACK (8.8+).
            Func<Task> unsupported = async () => await view.Streams.NegativeAcknowledgeAsync("events", "g", StreamNackMode.Fail, "1-0");
            await unsupported.Should().ThrowAsync<RespireServerException>();
            await view.Keys.DeleteAsync("events");
            await client.DisposeAsync();
            ids[0]!.Value.Value.Should().NotBeEmpty();
            entries[0].GetString("f").Should().Be("first");
        }
        finally { if (!client.Core.Disposed) await view.Keys.DeleteAsync("events", "missing"); }
    }
}

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class LegacyStreamIdempotencyIntegrationTests(RedisTestContainer fixture)
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task OlderServersRejectWithoutWriteFallback(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync($"{fixture.ConnectionString}&protocol={protocol}");
        var key = $"production-legacy:{Guid.NewGuid():N}";
        try
        {
            foreach (var mode in new[] { StreamIdempotency.Automatic("p"), StreamIdempotency.Manual("p", "i") })
            {
                Func<Task> write = async () => await client.Streams.AddAsync(key, new StreamAddOptions { Idempotency = mode }, ("f", "v"));
                await write.Should().ThrowAsync<RespireServerException>();
            }
            Func<Task> nack = async () => await client.Streams.NegativeAcknowledgeAsync(key, "g", StreamNackMode.Fail, "1-0");
            await nack.Should().ThrowAsync<RespireServerException>();
            (await client.Keys.ExistsAsync(key)).Should().BeFalse();
        }
        finally { await client.Keys.DeleteAsync(key); }
    }
}
