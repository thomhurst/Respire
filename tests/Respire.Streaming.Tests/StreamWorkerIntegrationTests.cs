using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Respire.Testing;
using Testcontainers.Redis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class StreamWorkerIntegrationTests(RedisTestContainer fixture)
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task EmptyGroupReadsRegisterConsumersLikeRedis(int protocol, bool modern = false)
    {
        await using var modernServer = modern ? new RedisBuilder("redis:7.2.11-alpine").Build() : null;
        if (modernServer is not null) await modernServer.StartAsync();
        var options = modernServer is null ? RespireOptions.Parse(fixture.ConnectionString)
            : new RespireOptions { Endpoints = [new(modernServer.Hostname, modernServer.GetMappedPublicPort(6379))] };
        await using var real = await RespireClient.ConnectAsync(options with { Protocol = (RespProtocol)protocol });
        var createsEmptyConsumers = await CreatesEmptyConsumersAsync(real);
        await using var server = new RespireFakeServer(clock: null, createsEmptyConsumers);
        await using var fake = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
        async Task<long> ReadEmptyGroupAsync(IRespireClient client)
        {
            await client.Streams.CreateGroupAsync("empty", "group", RespireStreamId.Beginning);
            await Assert.That(await client.Streams.ReadGroupOnceAsync("empty", "group", "consumer")).IsEmpty();
            return (await client.Streams.GroupInfoAsync("empty")).Single().Consumers;
        }
        var expected = await ReadEmptyGroupAsync(real);
        var actual = await ReadEmptyGroupAsync(fake);
        await Assert.That(actual).IsEqualTo(expected);
        await Assert.That(expected).IsEqualTo(createsEmptyConsumers ? 1L : 0L);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task HostedConsumerAcknowledgesSuccessAndRetainsNackOnRedis(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString)
            with { Protocol = (RespProtocol)protocol });
        var view = client.WithKeyPrefix("tenant:");
        await view.Streams.AddAsync("events", ("payload", "nack"));
        await view.Streams.AddAsync("events", ("payload", "ack"));
        var deliveries = Channel.CreateUnbounded<RespireStreamEntry>();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IRespireClient>(view);
        builder.Services.AddSingleton(deliveries);
        builder.Services.AddRespireStreamWorker<Handler>("events", "workers", new() { ConsumerCount = 2 });
        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await deliveries.Reader.ReadAsync(deadline.Token);
            await deliveries.Reader.ReadAsync(deadline.Token);
            while ((await view.Streams.PendingSummaryAsync("events", "workers")).Count != 1)
                await Task.Delay(10, deadline.Token);
            await host.StopAsync(deadline.Token);
            var pending = await view.Streams.PendingAsync("events", "workers");
            await Assert.That(pending.Single().DeliveryCount).IsEqualTo(1);
            await Assert.That(await client.Streams.CountAsync("events")).IsEqualTo(0);
            await Assert.That(await client.PingAsync()).IsGreaterThanOrEqualTo(TimeSpan.Zero);
        }
        finally { await host.StopAsync(); }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task StableConsumerReplaysOnlyItsOwnPendingOnRedis(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString)
            with { Protocol = (RespProtocol)protocol });
        var view = client.WithKeyPrefix("tenant:");
        for (var i = 1; i <= 3; i++)
            await view.Streams.AddAsync("resume", new StreamAddOptions { Id = $"{i}-0" }, ("payload", "ack"));
        await view.Streams.CreateGroupAsync("resume", "workers", RespireStreamId.Beginning);
        await view.Streams.ReadGroupOnceAsync("resume", "workers", "stable-0", new() { Count = 1 });
        await view.Streams.ReadGroupOnceAsync("resume", "workers", "other-0", new() { Count = 1 });
        var deliveries = Channel.CreateUnbounded<RespireStreamEntry>();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IRespireClient>(view);
        builder.Services.AddSingleton(deliveries);
        builder.Services.AddRespireStreamWorker<Handler>("resume", "workers", new()
        {
            ConsumerName = "stable", BatchSize = 1,
        });
        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var replay = await deliveries.Reader.ReadAsync(deadline.Token);
            var fresh = await deliveries.Reader.ReadAsync(deadline.Token);
            await Assert.That(replay.Id).IsEqualTo((RespireStreamId)"1-0");
            await Assert.That(fresh.Id).IsEqualTo((RespireStreamId)"3-0");
            while ((await view.Streams.PendingSummaryAsync("resume", "workers")).Count != 1)
                await Task.Delay(10, deadline.Token);
            var pending = (await view.Streams.PendingAsync("resume", "workers")).Single();
            await Assert.That(pending.Id).IsEqualTo((RespireStreamId)"2-0");
            await Assert.That(pending.Consumer).IsEqualTo("other-0");
            await Assert.That(pending.DeliveryCount).IsEqualTo(1);
        }
        finally { await host.StopAsync(); }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(2, "$")]
    [Arguments(3, "$")]
    [Arguments(2, "2-0")]
    [Arguments(3, "2-0")]
    [Arguments(2, "2-1")]
    [Arguments(3, "2-1")]
    [Arguments(2, "0", true)]
    [Arguments(3, "0", true)]
    public async Task FakePendingAndGroupMetadataMatchRedis(int protocol, string start = "0", bool unicodeConsumers = false)
    {
        await using var real = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString)
            with { Protocol = (RespProtocol)protocol });
        await using var server = new RespireFakeServer(clock: null, await CreatesEmptyConsumersAsync(real));
        await using var fake = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
        async Task<(RespireStreamPendingSummary Summary, RespireStreamPendingEntry[] Pending, RespireStreamGroupInfo Group)> ReadState(IRespireClient client)
        {
            var first = unicodeConsumers ? "\U00010000" : "a";
            var second = unicodeConsumers ? "\uFFFF" : "b";
            for (var i = 1; i <= 3; i++)
                await client.Streams.AddAsync("s", new StreamAddOptions { Id = $"{i}-0" }, ("f", "value"));
            await client.Streams.CreateGroupAsync("s", "g", start);
            await client.Streams.ReadGroupOnceAsync("s", "g", first, new() { Count = 2 });
            await client.Streams.ReadGroupOnceAsync("s", "g", first, new() { Count = 1 }, RespireStreamId.Beginning);
            await client.Streams.ReadGroupOnceAsync("s", "g", second, new() { Count = 1 });
            await client.Streams.AcknowledgeAsync("s", "g", ["2-0"]);
            return (await client.Streams.PendingSummaryAsync("s", "g"),
                await client.Streams.PendingAsync(new StreamPendingOptions { Start = "(0-0" }, "s", "g"),
                (await client.Streams.GroupInfoAsync("s")).Single());
        }
        var expected = await ReadState(real);
        var actual = await ReadState(fake);
        await Assert.That(actual.Group).IsEqualTo(expected.Group);
        await Assert.That(actual.Summary.Count).IsEqualTo(expected.Summary.Count);
        await Assert.That(actual.Summary.SmallestId).IsEqualTo(expected.Summary.SmallestId);
        await Assert.That(actual.Summary.GreatestId).IsEqualTo(expected.Summary.GreatestId);
        await Assert.That(actual.Summary.Consumers).IsEquivalentTo(expected.Summary.Consumers);
        await Assert.That(actual.Summary.Consumers.SequenceEqual(expected.Summary.Consumers)).IsTrue();
        await Assert.That(actual.Pending.Select(entry => (entry.Id, entry.Consumer, entry.DeliveryCount)))
            .IsEquivalentTo(expected.Pending.Select(entry => (entry.Id, entry.Consumer, entry.DeliveryCount)));
    }

    public sealed class Handler(Channel<RespireStreamEntry> deliveries) : IRespireStreamHandler<RespireStreamEntry>
    {
        public ValueTask<RespireStreamWorkerResult> HandleAsync(RespireStreamEntry message, CancellationToken cancellationToken)
        {
            deliveries.Writer.TryWrite(message);
            return ValueTask.FromResult(message.GetString("payload") == "ack"
                ? RespireStreamWorkerResult.Ack : RespireStreamWorkerResult.Nack);
        }
    }

    private static async Task<bool> CreatesEmptyConsumersAsync(RespireClient client)
    {
        using var info = await client.ExecuteAsync("INFO", "SERVER");
        var version = info.AsString()!.Split('\n').Single(line => line.StartsWith("redis_version:", StringComparison.Ordinal));
        return Version.Parse(version["redis_version:".Length..].Trim()) >= new Version(7, 2);
    }
}
