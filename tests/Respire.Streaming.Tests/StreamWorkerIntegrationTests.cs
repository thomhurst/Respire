using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Respire.Testing;
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
        await using var server = new RespireFakeServer();
        await using var fake = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
        await using var real = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString)
            with { Protocol = (RespProtocol)protocol });
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
}
